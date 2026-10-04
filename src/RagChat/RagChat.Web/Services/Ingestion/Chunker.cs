using Microsoft.ML.Tokenizers;

namespace RagChat.Web.Services.Ingestion;

// One chunk ready to embed: its text plus provenance (page and heading path of its first block).
public sealed record Chunk(string Text, int? Page, string? Section);

// Packs whole blocks (paragraphs) into chunks of up to MaxTokens.
//   - A block is never split, so a chunk never cuts a sentence or paragraph in half.
//   - A new chunk starts when the next block would go over budget, or when the section (heading) changes,
//     so one chunk = one topic, which keeps its embedding focused.
//   - Pages are metadata, not boundaries: a chunk can run over a page break and records the page it starts on.
//   - Overlap: when a chunk fills up mid-section, the next one starts with the previous chunk's last paragraph
//     (if it's short), so a thought that continues across the boundary is still whole in one chunk.
public static class Chunker
{
    public const int MaxTokens = 500; // ~375 words: precise retrieval, still enough context to answer
    public const int MaxOverlapTokens = 75; // ~15% of a chunk (Azure AI Search guidance: 10-15% overlap)

    // Tokens are counted with the embedding model's tokenizer, since chunks are what get embedded
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("text-embedding-3-small");

    public static IEnumerable<Chunk> Split(IEnumerable<DocumentBlock> blocks)
    {
        var current = new List<DocumentBlock>();
        var tokens = 0;

        foreach (var block in blocks)
        {
            var blockTokens = Tokenizer.CountTokens(block.Text);
            var sectionChanged = current.Count > 0 && block.Section != current[0].Section;

            if (current.Count > 0 && (tokens + blockTokens > MaxTokens || sectionChanged))
            {
                yield return ToChunk(current);

                // Carry the last paragraph over as overlap, unless the topic (section) changed or it's too long
                var last = current[^1];
                var lastTokens = Tokenizer.CountTokens(last.Text);
                current.Clear();
                tokens = 0;
                if (!sectionChanged && lastTokens <= MaxOverlapTokens && lastTokens + blockTokens <= MaxTokens)
                {
                    current.Add(last);
                    tokens = lastTokens;
                }
            }

            current.Add(block);
            tokens += blockTokens;
        }

        if (current.Count > 0)
        {
            yield return ToChunk(current);
        }
    }

    private static Chunk ToChunk(List<DocumentBlock> blocks) =>
        new(string.Join("\n\n", blocks.Select(b => b.Text)), blocks[0].Page, blocks[0].Section);
}
