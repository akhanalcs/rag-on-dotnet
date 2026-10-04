using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Microsoft.Extensions.AI;
using RagChat.Web.Services.Ingestion;
using RagChat.Web.Services.Security;

namespace RagChat.Web.Services;

public static class RagChatServiceExtensions
{
    // Registers everything the RAG pipeline needs. Used by Program.cs and by the evaluation tests.
    public static IHostApplicationBuilder AddRagChat(this IHostApplicationBuilder builder, string ingestionDirectory)
    {
        var openai = builder.AddAzureOpenAIClient("openai");
        openai.AddChatClient("chat")
            .UseFunctionInvocation()
            .UseOpenTelemetry(configure: c =>
                c.EnableSensitiveData = builder.Environment.IsDevelopment());
        openai.AddEmbeddingGenerator("text-embedding-3-small");

        // Azure AI Search (hybrid + semantic ranker). Managed identity, no keys.
        builder.AddAzureSearchClient("search");
        // Document Intelligence (no Aspire client integration, so registered by hand). Entra ID, no keys.
        builder.Services.AddSingleton(_ => new DocumentIntelligenceClient(
            new Uri(builder.Configuration.GetConnectionString("docintel") ?? throw new InvalidOperationException("Missing ConnectionStrings:docintel")),
            new DefaultAzureCredential()));
        builder.Services.AddSingleton<DocumentIntelligenceReader>();

        builder.Services.AddSingleton<DocumentAccessPolicy>();
        builder.Services.AddSingleton<DataIngestor>();
        builder.Services.AddSingleton<SemanticSearch>();
        builder.Services.AddKeyedSingleton("ingestion_directory", new DirectoryInfo(ingestionDirectory));
        builder.Services.AddSingleton<RagAssistant>();

        return builder;
    }
}
