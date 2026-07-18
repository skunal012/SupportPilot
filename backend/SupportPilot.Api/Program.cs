using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

var app = builder.Build();

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
