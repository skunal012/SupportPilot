using Anthropic;
using Anthropic.Models.Messages;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// The Anthropic client reads the API key from configuration:
//   - the ANTHROPIC_API_KEY environment variable, or
//   - dotnet user-secrets (great for local dev — key never touches git).
// If neither is set here, `new AnthropicClient()` still falls back to the
// ANTHROPIC_API_KEY environment variable on its own.
var apiKey = builder.Configuration["ANTHROPIC_API_KEY"];
var client = string.IsNullOrWhiteSpace(apiKey)
    ? new AnthropicClient()
    : new AnthropicClient { ApiKey = apiKey };

// The SYSTEM prompt sets the assistant's role and rules. It is NOT the user's
// question — it frames how the model should behave for every message.
const string SystemPrompt =
    "You are SupportPilot, a friendly and concise customer-support assistant. " +
    "Answer professionally. If you are unsure, say so rather than guessing.";

app.MapGet("/", () => "SupportPilot API is running. Try: GET /chat?q=your+question");

// DAY 1: stream Claude's answer back token-by-token using Server-Sent Events (SSE).
// SSE is a one-way stream from server -> browser over a normal HTTP response;
// each chunk is a line beginning with "data: ". This is how the answer will
// appear to "type itself out" in the React UI we build on Day 5.
app.MapGet("/chat", async (string? q, HttpResponse response, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsync("Pass a question, e.g. /chat?q=What+is+your+refund+policy", ct);
        return;
    }

    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";

    var parameters = new MessageCreateParams
    {
        // Haiku 4.5 = cheapest Claude model; plenty for a support bot and keeps
        // dev cost near zero. Swap to a bigger model for the final demo.
        Model = "claude-haiku-4-5",
        MaxTokens = 1024,          // hard ceiling on the RESPONSE length (in tokens)
        System = SystemPrompt,
        Messages = [new() { Role = Role.User, Content = q }],
    };

    // CreateStreaming yields many small events as the model generates. We only
    // care about text deltas — the incremental pieces of the answer.
    await foreach (var streamEvent in client.Messages.CreateStreaming(parameters))
    {
        if (streamEvent.TryPickContentBlockDelta(out var delta) &&
            delta.Delta.TryPickText(out var text))
        {
            await response.WriteAsync($"data: {text.Text}\n\n", ct);
            await response.Body.FlushAsync(ct);   // push the chunk immediately
        }
    }

    await response.WriteAsync("data: [DONE]\n\n", ct);
    await response.Body.FlushAsync(ct);
});

app.Run();
