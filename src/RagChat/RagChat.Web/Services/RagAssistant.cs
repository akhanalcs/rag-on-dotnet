using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace RagChat.Web.Services;

// The RAG "brain": system prompt + the tools the model can call.
// Shared by the chat UI and the evaluation tests so both exercise the exact same behavior.
public sealed class RagAssistant(SemanticSearch search)
{
    public const string SystemPrompt = @"
        You are an assistant who answers questions about information you retrieve.
        Do not answer questions about anything else.
        Use only simple markdown to format your responses.
        Use the LoadDocuments tool to prepare for searches before answering any questions.
        Use the Search tool to find relevant information. Each result has a numeric id.
        Answer only from search results. After each sentence that uses a result, cite its id in square brackets, like [3] or [1][4].
        Cite only ids you received. Don't add a separate list of sources.
        If the results don't answer the question, say so instead of guessing.
        ";

    // Tools are created per user: the user's groups are captured here, in code.
    // They are NOT a tool parameter, so the model (or a prompt-injection) can't choose or widen them.
    // Every result is registered in `sources`, which turns the model's [n] citations back into exact chunks.
    public ChatOptions CreateChatOptions(IReadOnlyCollection<string> userGroups, CitationSources sources) => new()
    {
        Tools =
        [
            AIFunctionFactory.Create(
                search.LoadDocumentsAsync,
                name: "LoadDocuments",
                description: "Loads the documents needed for performing searches. Must be completed before a search can be executed, but only needs to be completed once."),
            AIFunctionFactory.Create(
                async (
                    [Description("The phrase to search for.")] string searchPhrase,
                    [Description("If possible, specify the filename to search that file only. If not provided or empty, the search includes all files.")] string? filenameFilter = null) =>
                {
                    var results = await search.SearchAsync(searchPhrase, filenameFilter, userGroups, maxResults: 5);
                    // Provenance travels with each result; the id is what the model cites
                    return results.Select(hit =>
                        $"<result id=\"{sources.Add(hit)}\" filename=\"{hit.Chunk.DocumentId}\" page=\"{hit.Chunk.PageNumber}\" section=\"{hit.Chunk.Context}\">{hit.Chunk.Text}</result>").ToList();
                }, 
                name: "Search",
                description: "Searches for information using a phrase or keyword. Relies on documents already being loaded.")
        ]
    };
}
