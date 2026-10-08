using System.Text;
using Microsoft.Win32.SafeHandles;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public sealed partial class WindowsDesktopTools
{
    public Task<ToolResult> SearchFileContentsAsync(ToolProposal proposal, CancellationToken cancellationToken) =>
        Task.Run(() => SearchFileContents(proposal, cancellationToken), cancellationToken);

    private ToolResult SearchFileContents(ToolProposal proposal, CancellationToken cancellationToken)
    {
        if (!proposal.Arguments.TryGetValue("query", out var query)
            || !LocalFileContentSearchPolicy.IsValidQuery(query))
            return new(false, "内容搜索词必须为1–120个无控制字符的普通文本。", "INVALID_CONTENT_QUERY");
        if (!proposal.Arguments.TryGetValue("root_id", out var rootId) || rootId != "user-files")
            return new(false, "内容搜索只使用设置中配置的文件搜索目录。", "INVALID_SEARCH_ROOT");

        var safeRoots = new List<(string Path, FileIdentity Identity)>();
        foreach (var configuredRoot in _searchRoots.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryOpenOrdinaryPath(configuredRoot, expectedDirectory: true, enumerateDirectory: false,
                    out var rootHandle, out var canonicalRoot, out var rootIdentity)) continue;
            rootHandle.Dispose();
            if (safeRoots.All(root => !root.Path.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)))
                safeRoots.Add((canonicalRoot, rootIdentity));
        }
        if (safeRoots.Count == 0)
            return new(false, "没有可用的已配置搜索目录；没有读取文件内容。", "SEARCH_ROOT_UNAVAILABLE");

        var matches = new List<LocalFileContentMatch>(LocalFileContentSearchPolicy.MaximumResults);
        var queue = new Queue<(string Path, string Root, int Depth, FileIdentity? ExpectedIdentity)>();
        foreach (var root in safeRoots) queue.Enqueue((root.Path, root.Path, 0, root.Identity));
        var scanned = 0;
        var skippedEntries = 0;
        long bytesRead = 0;
        var depthLimitReached = false;
        var scanLimitReached = false;
        var byteLimitReached = false;

        while (queue.Count > 0 && scanned < LocalFileContentSearchPolicy.MaximumScannedEntries
            && matches.Count < LocalFileContentSearchPolicy.MaximumResults && !byteLimitReached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, root, depth, expectedIdentity) = queue.Dequeue();
            if (!TryOpenOrdinaryPath(current, expectedDirectory: true, enumerateDirectory: true,
                    out var directoryHandle, out var verifiedDirectory, out var directoryIdentity))
            {
                skippedEntries++;
                continue;
            }

            using (directoryHandle)
            {
                if ((expectedIdentity.HasValue && expectedIdentity.Value != directoryIdentity)
                    || !IsWithinRoot(verifiedDirectory, root))
                {
                    skippedEntries++;
                    continue;
                }
                var remaining = LocalFileContentSearchPolicy.MaximumScannedEntries - scanned;
                if (!TryEnumerateDirectoryEntries(directoryHandle, directoryIdentity, remaining, out var entries))
                {
                    skippedEntries++;
                    continue;
                }

                foreach (var entry in entries.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(item => item.Name, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanned++;
                    if ((entry.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Device)) != 0)
                    {
                        skippedEntries++;
                        continue;
                    }

                    var entryPath = Path.Combine(verifiedDirectory, entry.Name);
                    var isDirectory = (entry.Attributes & (uint)FileAttributes.Directory) != 0;
                    if (isDirectory)
                    {
                        if (depth >= LocalFileContentSearchPolicy.MaximumDepth)
                        {
                            depthLimitReached = true;
                            continue;
                        }
                        if (!TryOpenOrdinaryPath(entryPath, expectedDirectory: true, enumerateDirectory: false,
                                out var childHandle, out var childPath, out var childIdentity))
                        {
                            skippedEntries++;
                            continue;
                        }
                        childHandle.Dispose();
                        if (childIdentity == entry.Identity && IsWithinRoot(childPath, root))
                            queue.Enqueue((childPath, root, depth + 1, childIdentity));
                        else
                            skippedEntries++;
                        continue;
                    }

                    if (!LocalFileContentSearchPolicy.IsSearchableExtension(Path.GetExtension(entry.Name))) continue;
                    if (!TryOpenOrdinaryFileForRead(entryPath, out var fileHandle, out var filePath,
                            out var fileIdentity, out var fileLength, out var fileWriteTime))
                    {
                        skippedEntries++;
                        continue;
                    }

                    using (fileHandle)
                    {
                        if (fileIdentity != entry.Identity || !IsWithinRoot(filePath, root))
                        {
                            skippedEntries++;
                            continue;
                        }
                        if (fileLength > LocalFileContentSearchPolicy.MaximumFileBytes)
                        {
                            skippedEntries++;
                            continue;
                        }

                        var remainingBytes = LocalFileContentSearchPolicy.MaximumTotalBytes - bytesRead;
                        if (fileLength > remainingBytes)
                        {
                            byteLimitReached = true;
                            break;
                        }

                        var permittedBytes = (int)Math.Min(LocalFileContentSearchPolicy.MaximumFileBytes, remainingBytes);
                        if (!TryReadStableTextFile(fileHandle, fileLength, fileWriteTime, permittedBytes,
                                cancellationToken, out var content, out var bytesReadForFile, out var exceededBudget))
                        {
                            bytesRead += Math.Min(bytesReadForFile, permittedBytes);
                            if (exceededBudget) byteLimitReached = true;
                            else skippedEntries++;
                            if (byteLimitReached) break;
                            continue;
                        }
                        bytesRead += bytesReadForFile;
                        if (FindLineNumbers(content, query, out var lineNumbers, out var moreLocations))
                        {
                            matches.Add(new(ToDisplayPath(filePath), lineNumbers, moreLocations));
                            if (matches.Count >= LocalFileContentSearchPolicy.MaximumResults) break;
                        }
                    }

                    if (scanned >= LocalFileContentSearchPolicy.MaximumScannedEntries) break;
                }
            }
        }

        scanLimitReached = scanned >= LocalFileContentSearchPolicy.MaximumScannedEntries;
        var incomplete = scanLimitReached || depthLimitReached || skippedEntries > 0 || queue.Count > 0
            || byteLimitReached || matches.Count == LocalFileContentSearchPolicy.MaximumResults;
        var summary = LocalFileContentSearchPolicy.CreateSummary(matches.Count, scanned, bytesRead, skippedEntries,
            incomplete, scanLimitReached, byteLimitReached);
        var response = LocalFileContentSearchPolicy.CreateResponse(matches, scanned, bytesRead, skippedEntries,
            incomplete, scanLimitReached, byteLimitReached);
        return new(true, summary, Data: response);
    }

    private static bool TryReadStableTextFile(SafeFileHandle handle, long expectedLength, long expectedWriteTime,
        int maximumBytes, CancellationToken cancellationToken, out string text, out long bytesRead,
        out bool exceededBudget)
    {
        text = string.Empty;
        bytesRead = 0;
        exceededBudget = false;
        try
        {
            using var stream = new FileStream(handle, FileAccess.Read, 32 * 1024, isAsync: false);
            using var output = new MemoryStream((int)Math.Min(expectedLength, maximumBytes));
            var buffer = new byte[32 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var allowed = maximumBytes - (int)output.Length;
                var request = Math.Min(buffer.Length, allowed + 1);
                var read = stream.Read(buffer, 0, request);
                if (read == 0) break;
                bytesRead += read;
                if (bytesRead > maximumBytes)
                {
                    exceededBudget = true;
                    return false;
                }
                output.Write(buffer, 0, read);
            }

            if (bytesRead != expectedLength
                || !TryGetFileSnapshot(stream.SafeFileHandle, out var lengthAfterRead, out var writeTimeAfterRead)
                || lengthAfterRead != expectedLength || writeTimeAfterRead != expectedWriteTime)
                return false;

            if (!TryDecodeStrictText(output.ToArray(), out text))
                return false;
            if (text.Contains('\0'))
            {
                text = string.Empty;
                return false;
            }
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool TryDecodeStrictText(byte[] bytes, out string text)
    {
        text = string.Empty;
        try
        {
            Encoding encoding;
            var offset = 0;
            if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })
                || bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
                return false;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                offset = 3;
            }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            {
                encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
                offset = 2;
            }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            {
                encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
                offset = 2;
            }
            else
            {
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            }

            text = encoding.GetString(bytes, offset, bytes.Length - offset);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool FindLineNumbers(string content, string query, out IReadOnlyList<int> lineNumbers,
        out bool moreLocationsOmitted)
    {
        var found = new List<int>(LocalFileContentSearchPolicy.MaximumLineNumbersPerFile);
        moreLocationsOmitted = false;
        using var reader = new StringReader(content);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (found.Count < LocalFileContentSearchPolicy.MaximumLineNumbersPerFile)
                found.Add(lineNumber);
            else
            {
                moreLocationsOmitted = true;
                break;
            }
        }
        lineNumbers = found;
        return found.Count > 0;
    }
}
