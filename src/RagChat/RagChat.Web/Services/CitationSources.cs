namespace RagChat.Web.Services;

// Everything the Search tool returned in one conversation, numbered 1, 2, 3...
// The model cites these numbers ([3]); the UI looks them up to show the exact file, page and passage.
// So citations point at stored source text, not at a quote the model wrote (which it could get wrong).
public sealed class CitationSources
{
    private readonly List<SearchHit> _hits = [];

    public IReadOnlyList<SearchHit> All => _hits;

    // Returns the citation number for this hit (the same chunk keeps its number if it's found again)
    public int Add(SearchHit hit)
    {
        var index = _hits.FindIndex(h => h.Chunk.Key == hit.Chunk.Key);
        if (index < 0)
        {
            _hits.Add(hit);
            index = _hits.Count - 1;
        }
        return index + 1;
    }

    public SearchHit? Get(int number) => number >= 1 && number <= _hits.Count ? _hits[number - 1] : null;

    public void Clear() => _hits.Clear();
}
