using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportPilot.Api.Rag;

/// <summary>
/// A point to store: a stable id, its embedding vector, and the original text
/// (plus any metadata) we want back when it matches a search.
/// </summary>
public record VectorPoint(int Id, float[] Vector, string Text);

/// <summary>A search result: how close it matched (cosine score) and the text.</summary>
public record SearchHit(float Score, string Text);

/// <summary>
/// Thin wrapper over Qdrant's REST API. Qdrant is the vector database — it stores
/// embeddings and answers "give me the N closest vectors to this one" fast.
/// We talk raw REST (not the gRPC client) so every request is visible.
/// </summary>
public sealed class VectorStore(IHttpClientFactory httpFactory)
{
    private HttpClient Http => httpFactory.CreateClient("qdrant");

    /// <summary>
    /// Create the collection if it doesn't exist. A collection is like a table,
    /// but it's configured with the vector size and the distance metric (Cosine)
    /// used to compare vectors. Both must match the embedding model (768 dims).
    /// </summary>
    public async Task EnsureCollectionAsync(string name, int vectorSize, CancellationToken ct = default)
    {
        var existing = await Http.GetAsync($"/collections/{name}", ct);
        if (existing.StatusCode == HttpStatusCode.OK) return;

        var body = new { vectors = new { size = vectorSize, distance = "Cosine" } };
        using var resp = await Http.PutAsJsonAsync($"/collections/{name}", body, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Insert or update points. wait=true makes Qdrant confirm the write is
    /// searchable before returning — handy so a seed-then-search demo is reliable.
    /// </summary>
    public async Task UpsertAsync(string collection, IEnumerable<VectorPoint> points, CancellationToken ct = default)
    {
        var body = new
        {
            points = points.Select(p => new
            {
                id = p.Id,
                vector = p.Vector,
                payload = new { text = p.Text },
            }),
        };

        using var resp = await Http.PutAsJsonAsync($"/collections/{collection}/points?wait=true", body, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Embed the query first, then pass its vector here. Qdrant returns the
    /// top-N stored vectors by cosine similarity, each with its score and payload.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string collection, float[] queryVector, int limit, CancellationToken ct = default)
    {
        var body = new { vector = queryVector, limit, with_payload = true };
        using var resp = await Http.PostAsJsonAsync($"/collections/{collection}/points/search", body, ct);
        resp.EnsureSuccessStatusCode();

        var parsed = await resp.Content.ReadFromJsonAsync<SearchResponse>(ct);
        return parsed?.Result
                   .Select(r => new SearchHit(
                       r.Score,
                       r.Payload.TryGetValue("text", out var t) ? t.GetString() ?? "" : ""))
                   .ToList()
               ?? [];
    }
}

file record SearchResponse(
    [property: JsonPropertyName("result")] List<SearchResult> Result);

file record SearchResult(
    [property: JsonPropertyName("score")] float Score,
    [property: JsonPropertyName("payload")] Dictionary<string, JsonElement> Payload);
