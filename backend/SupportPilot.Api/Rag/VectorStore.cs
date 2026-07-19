using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportPilot.Api.Rag;

/// <summary>
/// A point to store: an id, its embedding vector, the original text, and the
/// metadata we want back on a match (which file / page / chunk it came from).
/// Id is an object so it can be an int (demo points) or a Guid (ingested chunks,
/// serialized as a UUID) — both are valid Qdrant point ids.
/// </summary>
public record VectorPoint(
    object Id,
    float[] Vector,
    string Text,
    string Filename = "demo",
    int Page = 0,
    int ChunkIndex = 0);

/// <summary>A search result: cosine score, the text, and where it came from.</summary>
public record SearchHit(float Score, string Text, string Filename, int Page);

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
    /// searchable before returning — handy so a seed/ingest-then-search is reliable.
    /// </summary>
    public async Task UpsertAsync(string collection, IEnumerable<VectorPoint> points, CancellationToken ct = default)
    {
        var body = new
        {
            points = points.Select(p => new
            {
                id = p.Id,
                vector = p.Vector,
                payload = new
                {
                    text = p.Text,
                    filename = p.Filename,
                    page = p.Page,
                    chunk_index = p.ChunkIndex,
                },
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
                       PayloadString(r.Payload, "text"),
                       PayloadString(r.Payload, "filename"),
                       PayloadInt(r.Payload, "page")))
                   .ToList()
               ?? [];
    }

    private static string PayloadString(Dictionary<string, JsonElement> payload, string key) =>
        payload.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int PayloadInt(Dictionary<string, JsonElement> payload, string key) =>
        payload.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}

file record SearchResponse(
    [property: JsonPropertyName("result")] List<SearchResult> Result);

file record SearchResult(
    [property: JsonPropertyName("score")] float Score,
    [property: JsonPropertyName("payload")] Dictionary<string, JsonElement> Payload);
