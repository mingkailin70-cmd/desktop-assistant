using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public sealed partial class WindowsDesktopTools
{
    public Task<ToolResult> CreateTextFileInExportAsync(ToolProposal proposal, CancellationToken cancellationToken) =>
        Task.Run(() => CreateTextFileInExport(proposal, cancellationToken));

    private async Task<ToolResult> CreateTextFileInExport(ToolProposal proposal, CancellationToken cancellationToken)
    {
        if (_exportRoot is null)
            return new(false, "小K导出目录未配置；没有创建文件。", "EXPORT_ROOT_UNAVAILABLE");
        if (!proposal.Arguments.TryGetValue("file_name", out var fileName)
            || !LocalTextFileCreatePolicy.IsValidFileName(fileName)
            || !proposal.Arguments.TryGetValue("content", out var content)
            || !LocalTextFileCreatePolicy.IsValidContent(content))
            return new(false, "仅支持新建 .txt 或 .md 文件；文件名不能包含路径，正文上限为64 KiB。", "INVALID_TEXT_FILE_REQUEST");

        byte[] contentBytes;
        try { contentBytes = new UTF8Encoding(false, true).GetBytes(content); }
        catch (EncoderFallbackException)
        { return new(false, "正文包含无效的Unicode字符；没有创建文件。", "INVALID_TEXT_ENCODING"); }
        var expectedHash = Convert.ToHexString(SHA256.HashData(contentBytes));

        if (!TryPrepareExportDirectory(out var exportHandle, out var canonicalExportRoot, out var exportIdentity))
            return new(false, "小K导出目录不可用，或路径包含链接；没有创建文件。", "EXPORT_ROOT_UNAVAILABLE");

        using (exportHandle)
        {
            if (!IsSafeExportFileName(fileName))
                return new(false, "文件名不能安全地用于导出目录；没有创建文件。", "INVALID_TEXT_FILE_NAME");

            var finalPath = Path.Combine(canonicalExportRoot, fileName);
            var temporaryPath = Path.Combine(canonicalExportRoot, $".xiaok-text-create-{Guid.NewGuid():N}.partial");
            var published = false;
            try
            {
                if (File.Exists(finalPath) || Directory.Exists(finalPath))
                    return new(false, "导出目录中已有同名文件；为避免覆盖，没有创建文件。", "EXPORT_NAME_CONFLICT");
                if (!IsCurrentDirectoryPath(_exportRoot, canonicalExportRoot, exportIdentity))
                    return new(false, "小K导出目录在写入前发生变化；没有创建文件。", "EXPORT_ROOT_CHANGED");

                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           16 * 1024, FileOptions.SequentialScan))
                {
                    await output.WriteAsync(contentBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!TryOpenOrdinaryFileForRead(temporaryPath, out var tempHandle, out var tempCanonicalPath,
                        out var tempIdentity, out var tempLength, out _))
                    return new(false, "临时文本文件无法独立核验；没有发布到导出目录。", "TEXT_FILE_TEMP_UNVERIFIABLE");
                using (tempHandle)
                {
                    if (!ToDisplayPath(tempCanonicalPath).Equals(ToDisplayPath(temporaryPath), StringComparison.OrdinalIgnoreCase)
                        || tempLength != contentBytes.Length
                        || !TryComputeSha256(tempHandle, out var tempHash)
                        || !string.Equals(expectedHash, tempHash, StringComparison.Ordinal))
                        return new(false, "临时文本文件的路径、长度或散列核验失败；没有发布。", "TEXT_FILE_TEMP_UNVERIFIABLE");

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentDirectoryPath(_exportRoot, canonicalExportRoot, exportIdentity))
                        return new(false, "小K导出目录在发布前发生变化；没有创建文件。", "EXPORT_ROOT_CHANGED");
                    if (File.Exists(finalPath) || Directory.Exists(finalPath))
                        return new(false, "导出目录中出现同名文件；为避免覆盖，没有创建文件。", "EXPORT_NAME_CONFLICT");

                    try { File.Move(temporaryPath, finalPath, overwrite: false); }
                    catch (IOException)
                    {
                        var destinationExists = File.Exists(finalPath) || Directory.Exists(finalPath);
                        if (!File.Exists(temporaryPath) && destinationExists)
                        {
                            published = true;
                            return TextFileCreateOutcomeUncertain(fileName);
                        }
                        if (destinationExists)
                            return new(false, "导出目录中出现同名文件；为避免覆盖，没有创建文件。", "EXPORT_NAME_CONFLICT");
                        throw;
                    }
                    published = true;

                    if (!TryOpenOrdinaryFileForRead(finalPath, out var finalHandle, out var finalCanonicalPath,
                            out var finalIdentity, out var finalLength, out _))
                        return TextFileCreateOutcomeUncertain(fileName);
                    using (finalHandle)
                    {
                        if (!ToDisplayPath(finalCanonicalPath).Equals(ToDisplayPath(finalPath), StringComparison.OrdinalIgnoreCase)
                            || finalIdentity != tempIdentity || finalLength != contentBytes.Length
                            || !TryComputeSha256(finalHandle, out var finalHash)
                            || !string.Equals(expectedHash, finalHash, StringComparison.Ordinal)
                            || !IsCurrentDirectoryPath(_exportRoot, canonicalExportRoot, exportIdentity))
                            return TextFileCreateOutcomeUncertain(fileName);
                    }
                }

                return new(true, $"已在小K导出目录新建文本文件：{fileName}（{contentBytes.Length:N0} 字节，SHA-256 {expectedHash}）。",
                    Data: ToDisplayPath(finalPath));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return published
                    ? TextFileCreateOutcomeUncertain(fileName)
                    : new(false, "创建文本文件前任务已取消；没有发布文件。", "TEXT_FILE_CREATE_CANCELLED",
                        FinalState: TaskLifecycleState.Cancelled);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                or ArgumentException or NotSupportedException or Win32Exception)
            {
                return published
                    ? TextFileCreateOutcomeUncertain(fileName)
                    : new(false, "创建文本文件失败；没有覆盖任何现有文件。", "TEXT_FILE_CREATE_FAILED");
            }
            finally
            {
                if (!published)
                {
                    try
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                        or ArgumentException or NotSupportedException) { }
                }
            }
        }
    }

    private static ToolResult TextFileCreateOutcomeUncertain(string fileName) =>
        new(false, $"文本文件“{fileName}”可能已写入，但独立核验未完成；请人工检查导出目录，不会自动重试。",
            "TEXT_FILE_CREATE_OUTCOME_UNCERTAIN", fileName, TaskLifecycleState.OutcomeUncertain);
}
