namespace SupportPilot.Api.Rag;

/// <summary>
/// Splits a piece of text into overlapping chunks sized for retrieval.
///
/// WHY: a whole document is too coarse to embed as one vector (imprecise
/// matches, blows the LLM context window, no page-level citations). We slice it
/// into ~500-token pieces so retrieval returns the *specific* relevant passage.
///
/// TOKENS vs WORDS: we approximate tokens with whitespace-delimited words
/// (1 token ≈ 0.75 words, so ~500 tokens ≈ ~375 words). This avoids a tokenizer
/// dependency; the embedding model tolerates the small size variance. The honest
/// interview answer: "I approximate tokens by word count — good enough because
/// chunk size is a soft target, not a hard limit."
///
/// OVERLAP: each chunk repeats the last ~40 words of the previous one, so a
/// sentence straddling a boundary still appears whole in at least one chunk.
/// </summary>
public sealed class DocumentChunker(int wordsPerChunk = 375, int overlapWords = 40)
{
    public IReadOnlyList<string> Chunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Split on any whitespace, dropping empty entries.
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        var chunks = new List<string>();
        var stride = Math.Max(1, wordsPerChunk - overlapWords); // how far the window advances

        for (var start = 0; start < words.Length; start += stride)
        {
            var end = Math.Min(start + wordsPerChunk, words.Length);
            chunks.Add(string.Join(' ', words[start..end]));
            if (end == words.Length) break; // last window reached the end
        }

        return chunks;
    }
}
