using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SupportPilot.Api.Orders;
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

// DAY 8-9: the mock Orders "database" the get_order tool reads from.
builder.Services.AddSingleton<OrdersStore>();

// DAY 3: the chunker splits documents into ~500-token overlapping pieces.
// Registered with an instance so its default sizing is used (DI can't fill the
// int constructor params on its own).
builder.Services.AddSingleton(new DocumentChunker());

var app = builder.Build();

// DAY 2 demo constants: the collection we store sample doc-vectors in.
const string DemoCollection = "supportpilot_demo";

// DAY 3: real ingested document chunks live in their own collection.
const string DocsCollection = "supportpilot_docs";

// DAY 10 — ESCALATION threshold. If the best-matching chunk's cosine score is
// below this, we treat the knowledge base as unable to answer and hand off to a
// human instead of letting the model guess. Calibrated against nomic-embed-text:
// on-topic questions score ~0.6+, off-topic ones ~0.4 and below.
const double EscalationThreshold = 0.5;

// The local model that writes the answer. Llama 3.2 3B fits on the GPU (fast).
// Swapping models later is a one-line change — that's the whole point of the
// "generation is a swappable adapter" idea.
const string GenerationModel = "llama3.2:3b";

// The base persona. Day 4 assembles the FULL system prompt per request inside
// /chat by appending the retrieved context and the grounding rules (RAG loop).
const string Persona =
    "You are SupportPilot, a friendly and concise customer-support assistant.";

// DAY 8-9 — the TOOL the model may call. This is a JSON-Schema description of a
// function; the model never runs code, it just decides to emit a "call get_order
// with order_id=X" request, which WE execute (see RunToolAwareChat below). The
// description doubles as instruction: it tells the model when the tool applies.
IReadOnlyList<OllamaTool> orderTools =
[
    new OllamaTool("function", new OllamaFunctionDef(
        Name: "get_order",
        Description: "Look up the live status and details of a customer order by its numeric " +
                     "order ID. Only use this when the user provides a specific order number.",
        Parameters: new
        {
            type = "object",
            properties = new
            {
                order_id = new { type = "string", description = "The numeric order ID, e.g. 1042" },
            },
            required = new[] { "order_id" },
        })),
];

app.MapGet("/", () => "SupportPilot API is running. Try: GET /chat?q=your+question");

// DAY 4 — THE RAG LOOP, now DAY 10 — MULTI-TURN + ESCALATION.
// Two entry points share one handler (HandleChat below):
//   GET  /chat?q=...           single-shot question (backwards-compatible, easy to curl)
//   POST /chat { messages }    full conversation history, so follow-ups resolve
// The GET form just wraps the single question as a one-message conversation.
app.MapGet("/chat", async (string? q, int? k, HttpResponse response,
    EmbeddingClient embedder, VectorStore store, OrdersStore orders,
    IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsync("Pass a question, e.g. /chat?q=What+is+your+refund+policy", ct);
        return;
    }

    var turns = new List<ChatTurn> { new("user", q) };
    await HandleChat(turns, k, orderTools, response, embedder, store, orders, httpFactory, ct);
});

// DAY 10 — MULTI-TURN. The frontend sends the whole conversation so a follow-up
// like "what about international?" has the prior turns as context. An LLM has no
// memory of its own — "memory" is just replaying the conversation into the prompt.
app.MapPost("/chat", async (ChatRequest req, int? k, HttpResponse response,
    EmbeddingClient embedder, VectorStore store, OrdersStore orders,
    IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var turns = req.Messages?
        .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content))
        .ToList() ?? [];

    if (turns.Count == 0 || turns[^1].Role != "user")
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsync("Send { messages: [...] } ending with a user turn.", ct);
        return;
    }

    await HandleChat(turns, k, orderTools, response, embedder, store, orders, httpFactory, ct);
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

// DAY 8-9 — MOCK ORDERS API. The "live data" the get_order tool ultimately
// reads. It's also directly curl-able (GET /orders/1042) so you can see the raw
// data the tool returns. In production this would be a real Orders service.
app.MapGet("/orders/{id}", (string id, OrdersStore orders) =>
    orders.TryGet(id, out var order)
        ? Results.Ok(order)
        : Results.NotFound(new { error = "not_found", orderId = id }));

app.Run();

// Write one SSE event ("data: ...\n\n") and flush it so the browser sees it immediately.
static async Task WriteEvent(HttpResponse response, string text, CancellationToken ct)
{
    await response.WriteAsync($"data: {text}\n\n", ct);
    await response.Body.FlushAsync(ct);
}

// DAY 10 — the shared chat pipeline used by both GET (single-shot) and POST
// (multi-turn) /chat. Steps: retrieve for the LATEST user turn, decide whether to
// escalate, otherwise ground the model in the context + the WHOLE conversation
// and stream the answer (with the get_order tool when the question looks order-y).
static async Task HandleChat(
    IReadOnlyList<ChatTurn> turns, int? k, IReadOnlyList<OllamaTool> orderTools,
    HttpResponse response, EmbeddingClient embedder, VectorStore store, OrdersStore orders,
    IHttpClientFactory httpFactory, CancellationToken ct)
{
    // The latest user turn drives retrieval (and the order-related check). Earlier
    // turns are memory: they go to the model but don't re-run retrieval. NOTE:
    // a bare follow-up ("what about Canada?") retrieves poorly on its own — that's
    // the Day-16 query-rewriting problem; here memory lets the MODEL interpret it.
    var lastUser = turns[^1].Content;

    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";

    // 1. RETRIEVE — embed the question, pull the top-K chunks from the ingested docs.
    await store.EnsureCollectionAsync(DocsCollection, EmbeddingClient.Dimensions, ct);
    var queryVector = await embedder.EmbedAsync(lastUser, ct);
    var hits = await store.SearchAsync(DocsCollection, queryVector, limit: k ?? 5, ct);

    var orderRelated = LooksOrderRelated(lastUser);
    var topScore = hits.Count > 0 ? hits[0].Score : 0f;

    // 2. ESCALATE — DAY 10. For a NON-order question where even the best-matching
    //    chunk is below our confidence threshold, the knowledge base can't answer.
    //    Rather than let the model guess, hand off to a human with a structured
    //    summary. (Order questions are answered by the live tool, not the docs, so
    //    they never escalate on retrieval score.) This is a FAIL-SAFE: when unsure,
    //    defer to a person instead of hallucinating.
    if (!orderRelated && topScore < EscalationThreshold)
    {
        await EscalateAsync(lastUser, turns, hits, response, ct);
        await WriteEvent(response, "[DONE]", ct);
        return;
    }

    // 3. GROUND — assemble a context block the model must answer from. Numbering
    //    each chunk lets the model cite it as [1], [2], ...
    var context = new StringBuilder();
    for (var i = 0; i < hits.Count; i++)
        context.AppendLine($"[{i + 1}] (source: {hits[i].Filename} p.{hits[i].Page})\n{hits[i].Text}\n");
    var contextBlock = hits.Count > 0 ? context.ToString() : "(no documents were retrieved)";

    var systemPrompt = orderRelated
        ? // Tool-aware prompt: answer from the CONTEXT and/or a get_order lookup.
          // NOTE: citations are added by the backend, so we tell the model to write
          // PLAIN PROSE with no brackets/JSON — otherwise a 3B model tries to "cite"
          // by emitting a document as a function call, which leaks as garbage text.
          Persona + "\n" +
          "Answer customer questions using the CONTEXT below (company policy and product documents) " +
          "and a single tool named get_order for LIVE order status.\n\n" +
          "Rules:\n" +
          "- Call get_order ONLY when the user gives a specific numeric order number (e.g. \"order " +
          "1042\"). It is the ONLY tool; never invent an order number.\n" +
          "- If get_order reports the order was not found, tell the user that order number was not " +
          "found and ask them to double-check it. Never answer an order lookup with \"I don't know " +
          "based on the available documents.\"\n" +
          "- Answer policy, shipping, warranty, and product questions from the CONTEXT.\n" +
          "- Write your answer in plain, natural prose. Do NOT output JSON, function-call syntax, " +
          "file names, or bracketed citation numbers — just answer the question directly.\n" +
          "- If the user did not give an order number, do NOT ask for one unless they clearly asked " +
          "about a specific order.\n" +
          "- For a non-order question whose answer is not in the context, reply exactly: " +
          "\"I don't know based on the available documents.\" Do not use outside knowledge.\n\n" +
          "Context:\n" + contextBlock
        : // RAG-only prompt (Day 4 behaviour — grounded, reliably cites, no tools).
          Persona + " Answer the user's question using ONLY the context below. " +
          "If the answer is not in the context, reply exactly: " +
          "\"I don't know based on the available documents.\" Do not use any outside knowledge. " +
          "When you state a fact, cite the source number it came from, like [1].\n\n" +
          "Context:\n" + contextBlock;

    // 4. GENERATE (agentic) — seed the model with the system prompt and then the
    //    WHOLE conversation (memory), so follow-ups resolve. When the tool is
    //    offered, RunToolAwareChat lets the model decide whether to call get_order.
    var messages = new List<OllamaMessage> { new("system", systemPrompt) };
    foreach (var turn in turns)
        messages.Add(new OllamaMessage(turn.Role, turn.Content));

    await RunToolAwareChat(messages, orderRelated ? orderTools : null, orders, GenerationModel, response, httpFactory, ct);

    // 5. CITATIONS — after the answer, emit the sources as one structured event
    //    (sentinel-prefixed, same style as [DONE]) so the frontend can render them.
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
}

// DAY 10 — ESCALATION. When retrieval confidence is too low to answer safely, we
// DON'T call the model. We stream a short, honest hand-off message to the customer
// and emit a structured summary (sentinel [ESCALATE]<json>) that a human agent
// would pick up from a queue — the customer's question, the recent conversation,
// what retrieval found (and how weakly), and a suggested team to route it to.
static async Task EscalateAsync(
    string question, IReadOnlyList<ChatTurn> turns, IReadOnlyList<SearchHit> hits,
    HttpResponse response, CancellationToken ct)
{
    const string message =
        "I'm not confident I can answer that accurately from our current help documents, " +
        "so I've passed this to a member of our support team — they'll follow up with you shortly. " +
        "If this is about a specific order, share your order number and I can check its status right away.";
    await WriteEvent(response, message, ct);

    var summary = new
    {
        reason = "low_retrieval_confidence",
        customerQuestion = question,
        suggestedTeam = SuggestTeam(question),
        conversation = turns.Select(t => new { role = t.Role, content = t.Content }),
        retrieval = new
        {
            topScore = hits.Count > 0 ? Math.Round(hits[0].Score, 4) : 0,
            threshold = EscalationThreshold,
            closestSources = hits.Take(3).Select(h => $"{h.Filename} (p.{h.Page})"),
        },
        createdAtUtc = DateTime.UtcNow.ToString("o"),
    };
    await WriteEvent(response, "[ESCALATE]" + JsonSerializer.Serialize(summary), ct);
}

// A tiny keyword router: which human team should pick up this escalation? A real
// system might use a classifier model (that's Day 18's structured-triage idea),
// but a cheap heuristic is honest and explainable for the demo.
static string SuggestTeam(string question)
{
    var q = question.ToLowerInvariant();
    if (q.Contains("refund") || q.Contains("charge") || q.Contains("billing") ||
        q.Contains("payment") || q.Contains("invoice") || q.Contains("price"))
        return "Billing";
    if (q.Contains("ship") || q.Contains("deliver") || q.Contains("track") ||
        q.Contains("order") || q.Contains("return"))
        return "Orders & Shipping";
    if (q.Contains("pair") || q.Contains("connect") || q.Contains("reset") ||
        q.Contains("firmware") || q.Contains("charge") || q.Contains("broken") ||
        q.Contains("not working"))
        return "Technical Support";
    return "General Support";
}

// DAY 8-9 — THE AGENTIC LOOP. Give the model a tool and let it decide when to
// use it. Each round we stream a model turn: if the model just answers, we're
// done; if it asks for get_order, we run it, feed the result back, and loop so
// the model can answer with real data. This is the whole of "function calling":
// model DECIDES -> we EXECUTE -> we INJECT the result -> model ANSWERS.
static async Task RunToolAwareChat(
    List<OllamaMessage> messages, IReadOnlyList<OllamaTool>? tools, OrdersStore orders,
    string model, HttpResponse response, IHttpClientFactory httpFactory, CancellationToken ct)
{
    const int maxRounds = 4;
    for (var round = 0; round < maxRounds; round++)
    {
        // On the final allowed round, withhold the tools so the model is forced to
        // answer in words instead of looping on tool calls forever (a safety net).
        var toolsThisRound = round < maxRounds - 1 ? tools : null;
        var (content, toolCalls) = await StreamOneRound(
            messages, toolsThisRound, model, response, httpFactory, ct);

        // No tool requested => the model streamed its final answer. We're done.
        if (toolCalls.Count == 0) return;

        // Otherwise record the assistant's tool-call turn, run each call, and add
        // the results as "tool" messages so the next round sees the live data.
        messages.Add(new OllamaMessage("assistant", content, ToolCalls: toolCalls));
        foreach (var call in toolCalls)
        {
            var result = ExecuteTool(call, orders);
            messages.Add(new OllamaMessage("tool", result, ToolName: call.Function.Name));
        }
    }
}

// Stream ONE Ollama chat turn. Forwards answer tokens to the browser as SSE and
// returns (the full text, any tool calls the model asked for). Does NOT emit
// [DONE] — the /chat handler owns the tail so it can send citations first.
static async Task<(string Content, List<OllamaToolCall> ToolCalls)> StreamOneRound(
    IReadOnlyList<OllamaMessage> messages, IReadOnlyList<OllamaTool>? tools,
    string model, HttpResponse response, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var payload = new OllamaChatRequest(Model: model, Messages: messages, Stream: true, Tools: tools);

    var http = httpFactory.CreateClient("ollama");
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    var contentBuilder = new StringBuilder();
    var toolCalls = new List<OllamaToolCall>();
    var braceDepth = 0;          // state for StripToolCallLeak across streamed tokens
    var startedContent = false;

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
            return ("", toolCalls);
        }

        await using var stream = await ollamaResponse.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Read the NDJSON stream line by line. A turn that calls a tool arrives as
        // a chunk whose message has tool_calls (and empty content); a normal turn
        // streams content tokens. We forward tokens and collect any tool calls.
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var chunk = JsonSerializer.Deserialize<OllamaChatChunk>(line);
            var message = chunk?.Message;
            if (message is not null)
            {
                if (!string.IsNullOrEmpty(message.Content))
                {
                    // Strip any leaked tool-call JSON (a 3B model sometimes emits a
                    // document as a bogus function call, which comes through as text).
                    var clean = StripToolCallLeak(message.Content, ref braceDepth, ref startedContent);
                    if (clean.Length > 0)
                    {
                        contentBuilder.Append(clean);
                        await WriteEvent(response, clean, ct);
                    }
                }
                if (message.ToolCalls is { Count: > 0 })
                    toolCalls.AddRange(message.ToolCalls);
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

    return (contentBuilder.ToString(), toolCalls);
}

// Safety net for a small model: strip leaked tool-call JSON out of streamed
// answer text. Small models occasionally emit a bogus function call (e.g. a
// document title as {"name": ...}) as CONTENT instead of a real tool call, and
// Ollama passes it through as text. Support answers never contain braces, so we
// suppress any {...} object and trim the "}; " junk that precedes it. Bracketed
// citations like [1] and everything else stream through untouched. The braceDepth
// and started flags persist across streamed tokens (a JSON object spans several).
static string StripToolCallLeak(string chunk, ref int braceDepth, ref bool started)
{
    var sb = new StringBuilder(chunk.Length);
    foreach (var c in chunk)
    {
        if (braceDepth > 0)                       // inside a leaked JSON object
        {
            if (c == '{') braceDepth++;
            else if (c == '}') braceDepth--;
            continue;                             // suppress the whole object
        }
        if (c == '{') { braceDepth = 1; continue; }
        if (!started)                             // trim leading "}; " style junk
        {
            if (c is '}' or ';' or ',' || char.IsWhiteSpace(c)) continue;
            started = true;
        }
        sb.Append(c);
    }
    return sb.ToString();
}

// Cheap gate: does the question plausibly concern an order? Used to decide
// whether to even OFFER the get_order tool, so a small over-eager model doesn't
// call it on pure policy/product questions. Keywords + any 3-plus-digit number.
static bool LooksOrderRelated(string q)
{
    var lower = q.ToLowerInvariant();
    string[] cues = ["order", "tracking", "track my", "shipment", "where is my", "my package"];
    if (cues.Any(lower.Contains)) return true;
    return System.Text.RegularExpressions.Regex.IsMatch(q, @"\d{3,}");
}

// Run a tool the model asked for and return its result as a JSON string (which
// becomes the "tool" message content). This is where we DEFEND against a small
// model's over-eager calling: a missing or non-numeric order_id (e.g. the model
// hallucinating "<ORDER_NUMBER>") returns a clean error the model can recover from.
static string ExecuteTool(OllamaToolCall call, OrdersStore orders)
{
    if (!string.Equals(call.Function.Name, "get_order", StringComparison.OrdinalIgnoreCase))
        return JsonSerializer.Serialize(new { error = "unknown_tool", name = call.Function.Name });

    // Pull order_id out of the arguments object; tolerate a string or a number.
    string? orderId = null;
    if (call.Function.Arguments.ValueKind == JsonValueKind.Object &&
        call.Function.Arguments.TryGetProperty("order_id", out var idProp))
    {
        orderId = idProp.ValueKind switch
        {
            JsonValueKind.String => idProp.GetString(),
            JsonValueKind.Number => idProp.GetRawText(),
            _ => null,
        };
    }

    if (string.IsNullOrWhiteSpace(orderId) || !orderId.All(char.IsDigit))
        return JsonSerializer.Serialize(new
        {
            error = "invalid_order_id",
            message = "No valid numeric order number was provided. Ask the customer for their order number.",
        });

    if (!orders.TryGet(orderId, out var order))
        return JsonSerializer.Serialize(new
        {
            error = "not_found",
            orderId,
            message = $"No order found with number {orderId}.",
        });

    return JsonSerializer.Serialize(order);
}

// DAY 10 — the POST /chat body: the full conversation so far. The frontend sends
// this so follow-ups have context; the backend replays it into the model prompt.
record ChatRequest(
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatTurn>? Messages);

record ChatTurn(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

// --- Request/response shapes for Ollama's /api/chat (records go after top-level code). ---

record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OllamaMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OllamaTool>? Tools = null);

record OllamaMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("tool_calls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OllamaToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolName = null);

record OllamaChatChunk(
    [property: JsonPropertyName("message")] OllamaMessage? Message,
    [property: JsonPropertyName("done")] bool Done);

// A tool we advertise to the model (JSON-Schema function definition).
record OllamaTool(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OllamaFunctionDef Function);

record OllamaFunctionDef(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] object Parameters);

// A tool call the model asked for (arguments arrive as a JSON object).
record OllamaToolCall(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("function")] OllamaToolCallFunction Function);

record OllamaToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] JsonElement Arguments);
