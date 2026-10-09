using System.Text;
using Microsoft.Win32.SafeHandles;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public sealed partial class WindowsDesktopTools
{
    public Task<ToolResult> ReadTextFileForLocalSummaryAsync(ToolProposal proposal,
        CancellationToken cancellationToken) =>
        Task.Run(() => ReadTextFileForLocalSummaryCoreAsync(proposal, cancellationToken), cancellationToken);

    private async Task<ToolResult> ReadTextFileForLocalSummaryCoreAsync(ToolProposal proposal,
        CancellationToken cancellationToken)
    {
        if (proposal.Arguments.Count != 1 || !proposal.Arguments.TryGetValue("path", out var requestedPath)
            || !LocalDocumentSummaryPolicy.IsValidPath(requestedPath))
            return new(false, "请指定搜索范围内、扩展名受支持的本机文本文件。", "INVALID_TEXT_FILE_PATH");
        if (proposal.Target != LocalDocumentSummaryPolicy.UserSearchRootId)
            return new(false, "文本文件读取只使用设置中配置的搜索目录。", "INVALID_SEARCH_ROOT");

        var safeRoots = new List<string>();
        foreach (var configuredRoot in _searchRoots.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryOpenOrdinaryPath(configuredRoot, expectedDirectory: true, enumerateDirectory: false,
                    out var rootHandle, out var canonicalRoot, out _)) continue;
            rootHandle.Dispose();
            if (safeRoots.All(root => !root.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)))
                safeRoots.Add(canonicalRoot);
        }
        if (safeRoots.Count == 0)
            return new(false, "没有可用的已配置搜索目录；没有读取文件内容。", "SEARCH_ROOT_UNAVAILABLE");

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryOpenOrdinaryFileForRead(requestedPath, out var fileHandle, out var canonicalFile,
                out _, out var fileLength, out var writeTime))
            return new(false, "文本文件不存在、不可读，或属于链接/特殊文件；没有读取内容。", "TEXT_FILE_UNAVAILABLE");

        using (fileHandle)
        {
            if (!safeRoots.Any(root => IsWithinRoot(canonicalFile, root)))
                return new(false, "文本文件不在设置中允许的搜索目录内；没有读取内容。", "TEXT_FILE_OUTSIDE_ALLOWED_ROOT");
            var isDocx = LocalDocumentSummaryPolicy.IsDocxPath(canonicalFile);
            var isPdf = LocalDocumentSummaryPolicy.IsPdfPath(canonicalFile);
            var maximumBytes = isDocx ? LocalDocumentSummaryPolicy.MaximumDocxFileBytes
                : isPdf ? LocalDocumentSummaryPolicy.MaximumPdfFileBytes
                : LocalDocumentSummaryPolicy.MaximumFileBytes;
            if (fileLength > maximumBytes)
                return new(false, isDocx ? "DOCX文件超过8 MiB读取上限；没有把正文交给模型。"
                    : isPdf ? "PDF文件超过8 MiB读取上限；没有把正文交给模型。"
                    : "文本文件超过64 KiB本机摘要上限；没有把正文交给模型。",
                    isPdf ? "PDF_TOO_LARGE" : "TEXT_FILE_TOO_LARGE");

            if (isDocx)
            {
                if (!TryReadStableDocxText(fileHandle, fileLength, writeTime, cancellationToken, out var docxText))
                    return new(false,
                        "DOCX结构、主文档XML或读取状态不受支持；正文未提交给模型。",
                        "DOCX_UNSTABLE_OR_UNSUPPORTED");

                return new(true,
                    "已从设置中允许的搜索目录安全提取DOCX主文档文本；正文只在当前任务内存中交给本机模型，不写入本地历史或日志。",
                    Data: docxText);
            }

            if (isPdf)
            {
                var pdfResult = await TryReadStablePdfTextAsync(fileHandle, fileLength, writeTime,
                    cancellationToken).ConfigureAwait(false);
                if (!pdfResult.Success)
                {
                    var message = pdfResult.ErrorCode switch
                    {
                        "PDF_TOO_MANY_PAGES" => "PDF超过100页读取上限；没有把正文交给模型。",
                        "PDF_TEXT_TOO_LARGE" => "PDF提取文字超过64 Ki字符上限；没有把正文交给模型。",
                        "PDF_WORKER_TIMEOUT" => "PDF解析超过20秒时限；已结束隔离解析进程，正文未提交给模型。",
                        "PDF_WORKER_UNAVAILABLE" => "无法启动隔离PDF解析进程；正文未提交给模型。",
                        "PDF_WORKER_FAILED" => "隔离PDF解析进程异常退出；正文未提交给模型。",
                        _ => "PDF结构不受支持、加密、读取期间发生变化，或无法提取文字；正文未提交给模型。"
                    };
                    return new(false, message, pdfResult.ErrorCode);
                }

                return new(true,
                    "已从设置中允许的搜索目录提取PDF可选择文字；解析在低优先级子进程中运行，内存上限512 MiB、时限20秒；不执行PDF脚本、不读表单/附件、不做OCR，正文只在当前任务内存中交给本机模型。",
                    Data: pdfResult.Text);
            }

            if (!TryReadStableTextFile(fileHandle, fileLength, writeTime,
                    LocalDocumentSummaryPolicy.MaximumFileBytes, cancellationToken,
                    out var text, out _, out var exceededBudget))
                return new(false,
                    exceededBudget ? "文本文件读取超过64 KiB上限；正文未提交给模型。"
                        : "文本文件在读取期间发生变化、编码不受支持，或内容不是可读文本；没有提交给模型。",
                    exceededBudget ? "TEXT_FILE_TOO_LARGE" : "TEXT_FILE_UNSTABLE_OR_UNSUPPORTED");

            return new(true, "已从设置中允许的搜索目录读取文本；正文只在当前任务内存中交给本机模型，不写入本地历史或日志。",
                Data: text);
        }
    }

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
