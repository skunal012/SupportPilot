using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SupportPilot.Api.Rag;

/// <summary>
/// Turns text into an embedding vector using a local Ollama model.
/// One job, one method: give it text, get back the list of numbers that
/// represents that text's *meaning*. The HTTP/JSON details stay hidden in here.
/// </summary>
public sealed class EmbeddingClient(IHttpClientFactory httpFactory, IConfiguration config)
{
    private readonly string _model = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";

    /// <summary>nomic-embed-text produces 768-dimensional vectors.</summary>
    public const int Dimensions = 768;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient("ollama");

        // Ollama's embedding endpoint: POST /api/embeddings {"model","prompt"}
        // returns {"embedding":[...768 floats...]}.
        using var resp = await http.PostAsJsonAsync(
            "/api/embeddings", new EmbeddingRequest(_model, text), ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
        return body?.Embedding
            ?? throw new InvalidOperationException("Ollama returned no embedding.");
    }
}

file record EmbeddingRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt")] string Prompt);

file record EmbeddingResponse(
    [property: JsonPropertyName("embedding")] float[] Embedding);
