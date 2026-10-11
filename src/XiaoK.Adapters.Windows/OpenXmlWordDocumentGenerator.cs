using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using System.Xml;
using XiaoK.Core;
using Word = DocumentFormat.OpenXml.Wordprocessing;

namespace XiaoK.Adapters.Windows;

internal static class OpenXmlWordDocumentGenerator
{
    public static bool TryCreate(string content, CancellationToken cancellationToken, out byte[] documentBytes)
    {
        documentBytes = [];
        if (!LocalDocxCreatePolicy.IsValidContent(content)) return false;

        using var buffer = new MemoryStream();
        try
        {
            using (var document = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document))
            {
                var mainPart = document.AddMainDocumentPart();
                var body = new Word.Body();
                foreach (var paragraphText in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var paragraph = new Word.Paragraph();
                    if (paragraphText.Length > 0)
                    {
                        paragraph.Append(new Word.Run(new Word.Text(paragraphText)
                        {
                            Space = SpaceProcessingModeValues.Preserve
                        }));
                    }
                    body.Append(paragraph);
                }
                mainPart.Document = new Word.Document(body);
                mainPart.Document.Save();

                cancellationToken.ThrowIfCancellationRequested();
                if (new OpenXmlValidator().Validate(document).Any()) return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            documentBytes = buffer.ToArray();
            return documentBytes.Length > 0;
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or ArgumentException or InvalidOperationException
            or IOException or XmlException)
        {
            documentBytes = [];
            return false;
        }
    }
}
