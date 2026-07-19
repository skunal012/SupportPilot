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

var app = builder.Build();

// DAY 2 demo constants: the collection we store sample doc-vectors in.
const string DemoCollection = "supportpilot_demo";

// The local model that writes the answer. Llama 3.2 3B fits on the GPU (fast).
// Swapping models later is a one-line change — that's the whole point of the
// "generation is a swappable adapter" idea.
const string GenerationModel = "llama3.2:3b";

// The SYSTEM prompt sets the assistant's role and rules. It is NOT the user's
// question — it frames how the model should behave for every message.
const string SystemPrompt =
    "You are SupportPilot, a friendly and concise customer-support assistant. " +
    "Answer professionally. If you are unsure, say so rather than guessing.";

app.MapGet("/", () => "SupportPilot API is running. Try: GET /chat?q=your+question");

// DAY 1: stream the model's answer back token-by-token using Server-Sent Events (SSE).
// Ollama streams its own answer as NDJSON (one JSON object per line); we read those
// lines, pull out the incremental text, and relay each piece to the browser as SSE.
app.MapGet("/chat", async (string? q, HttpResponse response, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsync("Pass a question, e.g. /chat?q=What+is+your+refund+policy", ct);
        return;
    }

    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";

    // Build the request body for Ollama's native chat API. stream:true makes
    // Ollama emit the answer incrementally instead of all at once.
    var payload = new OllamaChatRequest(
        Model: GenerationModel,
        Stream: true,
        Messages:
        [
            new OllamaMessage("system", SystemPrompt),
            new OllamaMessage("user", q),
        ]);

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

app.Run();

// Write one SSE event ("data: ...\n\n") and flush it so the browser sees it immediately.
static async Task WriteEvent(HttpResponse response, string text, CancellationToken ct)
{
    await response.WriteAsync($"data: {text}\n\n", ct);
    await response.Body.FlushAsync(ct);
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
