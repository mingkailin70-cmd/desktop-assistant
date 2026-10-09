using System.IO.Compression;
using System.Text;
using System.Xml;
using Microsoft.Win32.SafeHandles;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public sealed partial class WindowsDesktopTools
{
    private const string WordprocessingNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static bool TryReadStableDocxText(SafeFileHandle handle, long expectedLength, long expectedWriteTime,
        CancellationToken cancellationToken, out string text)
    {
        text = string.Empty;
        if (expectedLength < 0 || expectedLength > LocalDocumentSummaryPolicy.MaximumDocxFileBytes) return false;

        try
        {
            using var stream = new FileStream(handle, FileAccess.Read, 32 * 1024, isAsync: false);
            using var output = new MemoryStream((int)expectedLength);
            var buffer = new byte[32 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var allowed = LocalDocumentSummaryPolicy.MaximumDocxFileBytes - (int)output.Length;
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, allowed + 1));
                if (read == 0) break;
                if (output.Length + read > LocalDocumentSummaryPolicy.MaximumDocxFileBytes) return false;
                output.Write(buffer, 0, read);
            }

            if (output.Length != expectedLength
                || !TryGetFileSnapshot(stream.SafeFileHandle, out var lengthAfterRead, out var writeTimeAfterRead)
                || lengthAfterRead != expectedLength || writeTimeAfterRead != expectedWriteTime)
                return false;

            return TryExtractDocxText(output.ToArray(), cancellationToken, out text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException
            or System.ComponentModel.Win32Exception or OverflowException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool TryExtractDocxText(byte[] archiveBytes, CancellationToken cancellationToken, out string text)
    {
        text = string.Empty;
        try
        {
            using var archiveStream = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count is 0 or > LocalDocumentSummaryPolicy.MaximumDocxEntries) return false;

            ZipArchiveEntry? documentEntry = null;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(entry.FullName, "word/document.xml", StringComparison.Ordinal)) continue;
                if (documentEntry is not null) return false;
                documentEntry = entry;
            }

            if (documentEntry is null || documentEntry.Length < 0
                || documentEntry.Length > LocalDocumentSummaryPolicy.MaximumDocxXmlBytes)
                return false;

            byte[] xmlBytes;
            using (var entryStream = documentEntry.Open())
            using (var xmlOutput = new MemoryStream((int)documentEntry.Length))
            {
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var allowed = LocalDocumentSummaryPolicy.MaximumDocxXmlBytes - (int)xmlOutput.Length;
                    var read = entryStream.Read(buffer, 0, Math.Min(buffer.Length, allowed + 1));
                    if (read == 0) break;
                    if (xmlOutput.Length + read > LocalDocumentSummaryPolicy.MaximumDocxXmlBytes) return false;
                    xmlOutput.Write(buffer, 0, read);
                }

                if (xmlOutput.Length != documentEntry.Length) return false;
                xmlBytes = xmlOutput.ToArray();
            }

            return TryParseWordDocument(xmlBytes, cancellationToken, out text);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or XmlException
            or ArgumentException or NotSupportedException or InvalidOperationException or OverflowException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool TryParseWordDocument(byte[] xmlBytes, CancellationToken cancellationToken, out string text)
    {
        text = string.Empty;
        var builder = new StringBuilder(Math.Min(xmlBytes.Length, LocalDocumentSummaryPolicy.MaximumDocxTextCharacters));
        var sawDocumentRoot = false;
        var textElementDepth = -1;
        try
        {
            using var xmlStream = new MemoryStream(xmlBytes, writable: false);
            using var reader = XmlReader.Create(xmlStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = LocalDocumentSummaryPolicy.MaximumDocxXmlBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CheckCharacters = true,
                CloseInput = false
            });

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0
                    && reader.LocalName == "document"
                    && string.Equals(reader.NamespaceURI, WordprocessingNamespace, StringComparison.Ordinal))
                    sawDocumentRoot = true;

                if (textElementDepth >= 0)
                {
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == textElementDepth
                        && reader.LocalName == "t"
                        && string.Equals(reader.NamespaceURI, WordprocessingNamespace, StringComparison.Ordinal))
                        textElementDepth = -1;
                    else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                        or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                    {
                        if (!TryAppendBounded(builder, reader.Value)) return false;
                    }
                    else if (reader.NodeType == XmlNodeType.Element)
                        return false;
                    continue;
                }

                if (!string.Equals(reader.NamespaceURI, WordprocessingNamespace, StringComparison.Ordinal)) continue;

                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "t")
                    {
                        if (!reader.IsEmptyElement) textElementDepth = reader.Depth;
                    }
                    else if (reader.LocalName == "tab" && !TryAppendBounded(builder, "\t")) return false;
                    else if ((reader.LocalName is "br" or "cr") && !TryAppendBounded(builder, "\n")) return false;
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p"
                    && !TryAppendBounded(builder, "\n"))
                {
                    return false;
                }
            }

            if (!sawDocumentRoot || textElementDepth >= 0) return false;
            text = builder.ToString();
            return true;
        }
        catch (XmlException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool TryAppendBounded(StringBuilder builder, string value)
    {
        if (value.Length > LocalDocumentSummaryPolicy.MaximumDocxTextCharacters - builder.Length) return false;
        builder.Append(value);
        return true;
    }
}
