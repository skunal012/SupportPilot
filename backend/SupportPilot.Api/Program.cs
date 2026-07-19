using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SupportPilot.Api.Rag;

var builder = WebApplication.CreateBuilder(args);

// SupportPilot talks to a LOCAL Ollama server (http://localhost:11434) that runs
// open-source models on your own machine — no API key, no billing, no rate limits.
// We use a plain HttpClient (not a wrapper SDK) on purpose: every byte of the
// request and the streamed response is visible, so you can see exactly how an
// LLM call works end-to-end.
builder.Services.AddHttpClient("ollama", client =>
{
    var baseUrl = builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = Timeout.InfiniteTimeSpan; // a streaming response stays open a while
});

// DAY 2: Qdrant is the vector database (runs in Docker on port 6333). We talk to
// it over plain REST so every request is visible, same as the Ollama client above.
builder.Services.AddHttpClient("qdrant", client =>
{
    var baseUrl = builder.Configuration["Qdrant:BaseUrl"] ?? "http://localhost:6333";
    client.BaseAddress = new Uri(baseUrl);
});

// The two Day-2 building blocks: turn text into vectors, and store/search them.
builder.Services.AddSingleton<EmbeddingClient>();
builder.Services.AddSingleton<VectorStore>();

// DAY 3: the chunker splits documents into ~500-token overlapping pieces.
// Registered with an instance so its default sizing is used (DI can't fill the
// int constructor params on its own).
builder.Services.AddSingleton(new DocumentChunker());

var app = builder.Build();

// DAY 2 demo constants: the collection we store sample doc-vectors in.
const string DemoCollection = "supportpilot_demo";

// DAY 3: real ingested document chunks live in their own collection.
const string DocsCollection = "supportpilot_docs";

// The local model that writes the answer. Llama 3.2 3B fits on the GPU (fast).
// Swapping models later is a one-line change — that's the whole point of the
// "generation is a swappable adapter" idea.
const string GenerationModel = "llama3.2:3b";

// The base persona. Day 4 assembles the FULL system prompt per request inside
// /chat by appending the retrieved context and the grounding rules (RAG loop).
const string Persona =
    "You are SupportPilot, a friendly and concise customer-support assistant.";

app.MapGet("/", () => "SupportPilot API is running. Try: GET /chat?q=your+question");

// DAY 4 — THE RAG LOOP. The real assistant. Instead of asking the model blind
// (Day 1, which hallucinated because it has no company knowledge), we RETRIEVE
// relevant chunks from the ingested docs, GROUND the model in them, stream the
// answer token-by-token (SSE), and finish with CITATIONS.
app.MapGet("/chat", async (string? q, int? k, HttpResponse response,
    EmbeddingClient embedder, VectorStore store, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsync("Pass a question, e.g. /chat?q=What+is+your+refund+policy", ct);
        return;
    }

    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";

    // 1. RETRIEVE — embed the question, pull the top-K chunks from the docs
    //    ingested on Day 3. EnsureCollection avoids a 404 if nothing's ingested yet.
    await store.EnsureCollectionAsync(DocsCollection, EmbeddingClient.Dimensions, ct);
    var queryVector = await embedder.EmbedAsync(q, ct);
    var hits = await store.SearchAsync(DocsCollection, queryVector, limit: k ?? 5, ct);

    // 2. GROUND — assemble a context block the model must answer from. Numbering
    //    each chunk lets the model cite it as [1], [2], ...
    var context = new StringBuilder();
    for (var i = 0; i < hits.Count; i++)
        context.AppendLine($"[{i + 1}] (source: {hits[i].Filename} p.{hits[i].Page})\n{hits[i].Text}\n");

    // The grounding rules are the heart of RAG: answer ONLY from context, admit
    // ignorance otherwise (no hallucinating), and cite sources.
    var systemPrompt =
        Persona + " Answer the user's question using ONLY the context below. " +
        "If the answer is not in the context, reply exactly: " +
        "\"I don't know based on the available documents.\" Do not use any outside knowledge. " +
        "When you state a fact, cite the source number it came from, like [1].\n\n" +
        "Context:\n" +
        (hits.Count > 0 ? context.ToString() : "(no documents were retrieved)");

    // 3. GENERATE — stream the grounded answer token-by-token.
    OllamaMessage[] messages = [new("system", systemPrompt), new("user", q)];
    await StreamOllamaAnswer(messages, GenerationModel, response, httpFactory, ct);

    // 4. CITATIONS — after the answer, emit the sources as one structured event
    //    (sentinel-prefixed, same style as [DONE]) so the Day-5 frontend can render
    //    clickable citations.
    if (hits.Count > 0)
    {
        var citations = hits.Select((h, i) => new
        {
            n = i + 1,
            source = $"{h.Filename} (p.{h.Page})",
            score = Math.Round(h.Score, 4),
        });
        await WriteEvent(response, "[CITATIONS]" + JsonSerializer.Serialize(citations), ct);
    }

    await WriteEvent(response, "[DONE]", ct);
});

// DAY 2 — SEED: embed a handful of sample support sentences and store them in
// Qdrant. In a real app these would be chunks of your uploaded docs (Day 3);
// here they're hard-coded so we can see semantic search working end-to-end.
app.MapPost("/demo/seed", async (EmbeddingClient embedder, VectorStore store, CancellationToken ct) =>
{
    string[] sentences =
    [
        "Our refund policy allows returns within 30 days of purchase.",
        "Items must be unused and in their original packaging to qualify for a refund.",
        "Standard shipping takes 3 to 5 business days within the country.",
        "International shipping can take up to 14 business days to arrive.",
        "You can reset your password from the account settings page.",
        "Contact our support team at help@example.com for account problems.",
        "Premium members receive free next-day delivery on every order.",
        "Gift cards are non-refundable and cannot be exchanged for cash.",
    ];

    await store.EnsureCollectionAsync(DemoCollection, EmbeddingClient.Dimensions, ct);

    var points = new List<VectorPoint>();
    for (var i = 0; i < sentences.Length; i++)
    {
        var vector = await embedder.EmbedAsync(sentences[i], ct);
        points.Add(new VectorPoint(Id: i, Vector: vector, Text: sentences[i]));
    }

    await store.UpsertAsync(DemoCollection, points, ct);
    return Results.Ok(new { seeded = points.Count, collection = DemoCollection });
});

// DAY 2 — SEARCH: embed the incoming question, then ask Qdrant for the closest
// stored sentences by cosine similarity. This is the "retrieve" half of RAG.
app.MapGet("/demo/search", async (string? q, int? k, EmbeddingClient embedder, VectorStore store, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest("Pass a query, e.g. /demo/search?q=can+I+get+my+money+back");

    var queryVector = await embedder.EmbedAsync(q, ct);
    var hits = await store.SearchAsync(DemoCollection, queryVector, limit: k ?? 3, ct);

    return Results.Ok(new
    {
        query = q,
        results = hits.Select(h => new { score = Math.Round(h.Score, 4), text = h.Text }),
    });
});

// DAY 3 — INGEST: upload a document, extract its text page-by-page, chunk it,
// embed each chunk, and store it in Qdrant with metadata (filename, page).
// This is the pipeline that replaces the hard-coded demo sentences with real docs.
app.MapPost("/ingest", async (IFormFile file, bool? dryRun,
    EmbeddingClient embedder, VectorStore store, DocumentChunker chunker, CancellationToken ct) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest("Attach a file (form field 'file') — a .pdf, .txt, or .md.");

    // 1. EXTRACT — pull text out, one entry per page (PDFs) or one entry total (text).
    await using var upload = file.OpenReadStream();
    var pages = TextExtractor.Extract(upload, file.FileName);

    // 2. CHUNK — split each page into ~500-token overlapping pieces, remembering
    //    the page each chunk came from so we can cite it later.
    var chunks = new List<(string Text, int Page, int Index)>();
    var running = 0;
    foreach (var page in pages)
        foreach (var chunkText in chunker.Chunk(page.Text))
            chunks.Add((chunkText, page.Page, running++));

    if (chunks.Count == 0)
        return Results.BadRequest("No text could be extracted from that file.");

    // dryRun = see the chunking WITHOUT embedding/storing. Great for inspecting
    // chunk sizes and the overlap between consecutive chunks.
    if (dryRun == true)
        return Results.Ok(new
        {
            file = file.FileName,
            pages = pages.Count,
            chunks = chunks.Count,
            preview = chunks.Take(6).Select(c => new
            {
                c.Index,
                c.Page,
                words = c.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                text = c.Text,
            }),
        });

    // 3. EMBED + STORE — one vector per chunk, tagged with where it came from.
    await store.EnsureCollectionAsync(DocsCollection, EmbeddingClient.Dimensions, ct);
    var points = new List<VectorPoint>();
    foreach (var c in chunks)
    {
        var vector = await embedder.EmbedAsync(c.Text, ct);
        points.Add(new VectorPoint(
            Id: Guid.NewGuid(), Vector: vector, Text: c.Text,
            Filename: file.FileName, Page: c.Page, ChunkIndex: c.Index));
    }

    await store.UpsertAsync(DocsCollection, points, ct);
    return Results.Ok(new { file = file.FileName, pages = pages.Count, chunks = points.Count, collection = DocsCollection });
}).DisableAntiforgery();

// DAY 3 — SEARCH over ingested docs: same retrieval as /demo/search, but against
// real documents — and each result now carries a citation (filename + page).
app.MapGet("/search", async (string? q, int? k, EmbeddingClient embedder, VectorStore store, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest("Pass a query, e.g. /search?q=how+long+do+refunds+take");

    var queryVector = await embedder.EmbedAsync(q, ct);
    var hits = await store.SearchAsync(DocsCollection, queryVector, limit: k ?? 3, ct);

    return Results.Ok(new
    {
        query = q,
        results = hits.Select(h => new
        {
            score = Math.Round(h.Score, 4),
            source = $"{h.Filename} (p.{h.Page})",
            text = h.Text,
        }),
    });
});

app.Run();

// Write one SSE event ("data: ...\n\n") and flush it so the browser sees it immediately.
static async Task WriteEvent(HttpResponse response, string text, CancellationToken ct)
{
    await response.WriteAsync($"data: {text}\n\n", ct);
    await response.Body.FlushAsync(ct);
}

// Stream one Ollama chat completion to the response as SSE token events. Shared
// by the RAG /chat loop. It relays each token but does NOT emit [DONE] — the
// caller owns the tail (so it can send citations first).
static async Task StreamOllamaAnswer(
    IReadOnlyList<OllamaMessage> messages, string model,
    HttpResponse response, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var payload = new OllamaChatRequest(Model: model, Stream: true, Messages: messages);

    var http = httpFactory.CreateClient("ollama");
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    try
    {
        // ResponseHeadersRead = start reading as soon as headers arrive, instead
        // of buffering the whole (streamed) body first. Essential for streaming.
        using var ollamaResponse = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!ollamaResponse.IsSuccessStatusCode)
        {
            var body = await ollamaResponse.Content.ReadAsStringAsync(ct);
            await WriteEvent(response, $"[error] Ollama returned {(int)ollamaResponse.StatusCode}: {body}", ct);
            return;
        }

        await using var stream = await ollamaResponse.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Read the NDJSON stream line by line. Each line is a JSON object like:
        //   {"message":{"role":"assistant","content":"Hello"},"done":false}
        // We forward message.content and stop when "done" is true.
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var chunk = JsonSerializer.Deserialize<OllamaChatChunk>(line);
            var text = chunk?.Message?.Content;
            if (!string.IsNullOrEmpty(text))
            {
                await WriteEvent(response, text, ct);
            }

            if (chunk?.Done == true) break;
        }
    }
    catch (HttpRequestException ex)
    {
        // Most common cause: the Ollama server isn't running. Surface it cleanly
        // instead of crashing the request with an unhandled exception.
        await WriteEvent(response, $"[error] Could not reach Ollama — is it running? ({ex.Message})", ct);
    }
}

// --- Request/response shapes for Ollama's /api/chat (records go after top-level code). ---

record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OllamaMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream);

record OllamaMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

record OllamaChatChunk(
    [property: JsonPropertyName("message")] OllamaMessage? Message,
    [property: JsonPropertyName("done")] bool Done);
