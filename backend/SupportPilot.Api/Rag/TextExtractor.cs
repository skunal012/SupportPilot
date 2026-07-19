using UglyToad.PdfPig;

namespace SupportPilot.Api.Rag;

/// <summary>One page of extracted text (page numbers are 1-based, matching how a human reads).</summary>
public record PageText(int Page, string Text);

/// <summary>
/// Pulls raw text out of an uploaded file. PDFs are extracted page-by-page so we
/// can cite the exact page later; plain text / markdown is treated as a single page.
/// </summary>
public static class TextExtractor
{
    public static IReadOnlyList<PageText> Extract(Stream fileStream, string fileName)
    {
        var isPdf = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        return isPdf ? ExtractPdf(fileStream) : ExtractPlainText(fileStream);
    }

    private static IReadOnlyList<PageText> ExtractPdf(Stream fileStream)
    {
        // PdfPig needs a seekable stream; copy to memory if the upload stream isn't.
        using var buffer = new MemoryStream();
        fileStream.CopyTo(buffer);
        buffer.Position = 0;

        var pages = new List<PageText>();
        using var pdf = PdfDocument.Open(buffer);
        foreach (var page in pdf.GetPages())
        {
            pages.Add(new PageText(page.Number, page.Text));
        }

        return pages;
    }

    private static IReadOnlyList<PageText> ExtractPlainText(Stream fileStream)
    {
        using var reader = new StreamReader(fileStream);
        var text = reader.ReadToEnd();
        return [new PageText(1, text)];
    }
}
