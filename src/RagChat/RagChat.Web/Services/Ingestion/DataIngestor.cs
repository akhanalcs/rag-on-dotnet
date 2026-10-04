using System.Security.Cryptography;
using System.Text;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.AI;
using RagChat.Web.Services.Security;

namespace RagChat.Web.Services.Ingestion;

// Ingests every file in a folder: read -> split into chunks -> embed -> store in Azure AI Search ("push" model).
// Idempotent: re-running never duplicates, skips unchanged files, and removes chunks that no longer exist.
public class DataIngestor(
    ILogger<DataIngestor> logger,
    SearchIndexClient indexClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    DocumentIntelligenceReader documentReader,
    DocumentAccessPolicy accessPolicy)
{
    private readonly SearchClient _searchClient = indexClient.GetSearchClient(IngestedChunk.IndexName);

    public async Task IngestDataAsync(DirectoryInfo directory)
    {
        // Creates the index, or updates it when IngestedChunk's schema changes
        await indexClient.CreateOrUpdateIndexAsync(IngestedChunk.CreateIndex());

        foreach (var file in directory.EnumerateFiles())
        {
            await IngestFileAsync(file);
        }
    }

    private async Task IngestFileAsync(FileInfo file)
    {
        var documentId = file.Name;
        var allowedGroups = accessPolicy.GroupsFor(documentId);

        // Fingerprint of content + access groups: a permission change must re-stamp the chunks too
        var hash = Hash(await File.ReadAllBytesAsync(file.FullName), allowedGroups);

        var existing = await ExistingChunksAsync(documentId);
        if (existing.Count > 0 && existing.All(c => c.ContentHash == hash))
        {
            logger.LogInformation("Skipping '{id}': unchanged.", documentId);
            return;
        }

        IEnumerable<DocumentBlock>? blocks = file.Extension.ToLowerInvariant() switch
        {
            ".pdf" or ".docx" => await documentReader.ReadAsync(file.FullName),
            ".md" => MarkdownReader.Read(await File.ReadAllTextAsync(file.FullName)),
            _ => null
        };
        if (blocks is null)
        {
            logger.LogWarning("Skipping '{id}': unsupported file type.", documentId);
            return;
        }

        var chunks = Chunker.Split(blocks)
            .Select((chunk, index) => new IngestedChunk
            {
                Key = StableKey(documentId, index),
                DocumentId = documentId,
                Text = chunk.Text,
                Context = chunk.Section,
                PageNumber = chunk.Page,
                AllowedGroups = allowedGroups,
                ContentHash = hash
            })
            .ToList();

        // Embed all chunks of the document in one batched call
        var embeddings = await embeddingGenerator.GenerateAsync(chunks.Select(c => c.Text));
        for (var i = 0; i < chunks.Count; i++)
        {
            chunks[i].Embedding = embeddings[i].Vector;
        }

        // Upload first, then delete leftovers (e.g. the document got shorter), so search never sees a half-empty document
        await _searchClient.MergeOrUploadDocumentsAsync(chunks);
        var newKeys = chunks.Select(c => c.Key).ToHashSet();
        var leftovers = existing.Select(c => c.Key).Where(key => !newKeys.Contains(key)).ToList();
        if (leftovers.Count > 0)
        {
            await _searchClient.DeleteDocumentsAsync("key", leftovers);
        }

        logger.LogInformation("Ingested '{id}': {count} chunks.", documentId, chunks.Count);
    }

    // Keys and hashes of the chunks already indexed for this document (no text or vectors, so it's cheap)
    private async Task<List<IngestedChunk>> ExistingChunksAsync(string documentId)
    {
        var options = new SearchOptions
        {
            Filter = $"documentid eq '{SemanticSearch.Escape(documentId)}'",
            Select = { "key", "content_hash" },
            Size = 1000
        };
        var response = await _searchClient.SearchAsync<IngestedChunk>("*", options);

        var chunks = new List<IngestedChunk>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            chunks.Add(result.Document);
        }
        return chunks;
    }

    // A chunk's key is a hash of document + chunk position, so re-ingesting overwrites the same points instead of adding new ones.
    // Same document + same position = same key, so re-ingesting overwrites instead of duplicating.
    // Keys may only contain letters, digits, '_', '-', '=' -> hex.
    private static string StableKey(string documentId, int index) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{documentId}#{index}")));

    private static string Hash(byte[] content, string[] allowedGroups) =>
        Convert.ToHexString(SHA256.HashData([.. content, .. Encoding.UTF8.GetBytes(string.Join(',', allowedGroups))]));
}
