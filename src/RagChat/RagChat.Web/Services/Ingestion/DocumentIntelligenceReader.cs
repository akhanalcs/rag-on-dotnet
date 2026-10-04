using Azure;
using Azure.AI.DocumentIntelligence;

namespace RagChat.Web.Services.Ingestion;

// Reads PDF/Word files with Azure AI Document Intelligence ("prebuilt-layout") into paragraph blocks.
// Layout gives us, per paragraph: the text (with OCR for scanned pages), its page number, and its role
// (title, section heading, page header/footer...). We use the role to drop noise and to track the section.
public sealed class DocumentIntelligenceReader(DocumentIntelligenceClient client)
{
    // Repeated on every page, so not useful content for search
    private static readonly ParagraphRole?[] Noise = [ParagraphRole.PageHeader, ParagraphRole.PageFooter, ParagraphRole.PageNumber];

    public async Task<List<DocumentBlock>> ReadAsync(string path)
    {
        var operation = await client.AnalyzeDocumentAsync(
            WaitUntil.Completed, "prebuilt-layout", BinaryData.FromBytes(await File.ReadAllBytesAsync(path)));

        var blocks = new List<DocumentBlock>();
        string? section = null;

        foreach (var paragraph in operation.Value.Paragraphs)
        {
            if (Noise.Contains(paragraph.Role))
            {
                continue;
            }

            // A section heading starts a new section; the paragraphs after it belong to it
            if (paragraph.Role == ParagraphRole.SectionHeading)
            {
                section = paragraph.Content;
                continue;
            }

            // BoundingRegion is a struct, so check the list instead of using ?.
            int? page = paragraph.BoundingRegions.Count > 0 ? paragraph.BoundingRegions[0].PageNumber : null;
            blocks.Add(new DocumentBlock(paragraph.Content, page, section));
        }

        return blocks;
    }
}
