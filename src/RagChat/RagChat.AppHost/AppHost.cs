using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;

var builder = DistributedApplication.CreateBuilder(args);

// See https://learn.microsoft.com/dotnet/aspire/azure/local-provisioning#configuration
// for instructions providing configuration values
var openai = builder.AddAzureOpenAI("openai");

// The template's gpt-4o-mini (2024-07-18) Standard deployment was retired on 2026-03-31.
openai.AddDeployment(
    name: "chat",
    modelName: "gpt-5-mini",
    modelVersion: "2025-08-07")
    .WithProperties(d => d.SkuName = "GlobalStandard"); // Cheapest for learning; use DataZoneStandard/Standard when data residency matters

openai.AddDeployment(
    name: "text-embedding-3-small",
    modelName: "text-embedding-3-small",
    modelVersion: "1");

// Azure AI Search: hybrid (keyword + vector) search + semantic ranker. The web app gets index read/write roles.
var search = builder.AddAzureSearch("search");

// Azure AI Document Intelligence: reads PDFs/Word (incl. OCR for scanned pages) into paragraphs with page numbers.
// Aspire has no built-in integration, so we describe the resource ourselves (Azure.Provisioning -> Bicep).
var docIntel = builder.AddAzureInfrastructure("docintel", infra =>
{
    var account = new CognitiveServicesAccount("docintel")
    {
        Kind = "FormRecognizer", // the resource kind behind Document Intelligence
        Sku = new CognitiveServicesSku { Name = "S0" }, // the free tier (F0) only reads the first 2 pages
        Properties = new CognitiveServicesAccountProperties
        {
            CustomSubDomainName = BicepFunction.Interpolate($"docintel-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"),
            DisableLocalAuth = true, // no keys: Entra ID only, same as OpenAI
            PublicNetworkAccess = ServiceAccountPublicNetworkAccess.Enabled
        }
    };
    infra.Add(account);

    // Aspire fills these in with whoever runs the app: you locally, the app's managed identity in Azure
    var principalId = new ProvisioningParameter(AzureBicepResource.KnownParameters.PrincipalId, typeof(string));
    var principalType = new ProvisioningParameter(AzureBicepResource.KnownParameters.PrincipalType, typeof(string));
    infra.Add(principalId);
    infra.Add(principalType);
    infra.Add(account.CreateRoleAssignment(CognitiveServicesBuiltInRole.CognitiveServicesUser, principalType, principalId));

    infra.Add(new ProvisioningOutput("endpoint", typeof(string)) { Value = account.Properties.Endpoint });
});

var webApp = builder.AddProject<Projects.RagChat_Web>("aichatweb-app");
webApp
    .WithReference(openai)
    .WaitFor(openai);
webApp
    .WithReference(search)
    .WaitFor(search);
webApp
    .WithEnvironment("ConnectionStrings__docintel", docIntel.GetOutput("endpoint"))
    .WaitFor(docIntel);

builder.Build().Run();
