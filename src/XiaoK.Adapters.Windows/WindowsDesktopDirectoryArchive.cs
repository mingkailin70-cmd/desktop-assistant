using System.ComponentModel;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public sealed partial class WindowsDesktopTools
{
    public Task<ToolResult> ArchiveDirectoryToExportAsync(ToolProposal proposal,
        CancellationToken cancellationToken) =>
        Task.Run(() => ArchiveDirectoryToExportAsyncCore(proposal, cancellationToken), cancellationToken);

    private async Task<ToolResult> ArchiveDirectoryToExportAsyncCore(ToolProposal proposal,
        CancellationToken cancellationToken)
    {
        if (_exportRoot is null)
            return new(false, "小K导出目录未配置；没有压缩文件夹。", "EXPORT_ROOT_UNAVAILABLE");
        if (!proposal.Arguments.TryGetValue("directory_path", out var requestedDirectory)
            || !LocalDirectoryArchivePolicy.IsValidSourcePath(requestedDirectory))
            return new(false, "请提供搜索范围内的本机完整文件夹路径。", "INVALID_ARCHIVE_DIRECTORY");

        var allowedRoots = new List<string>();
        foreach (var configuredRoot in _searchRoots.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryOpenOrdinaryPath(configuredRoot, expectedDirectory: true, enumerateDirectory: false,
                    out var rootHandle, out var canonicalRoot, out _)) continue;
            rootHandle.Dispose();
            if (allowedRoots.All(root => !root.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)))
                allowedRoots.Add(canonicalRoot);
        }
        if (allowedRoots.Count == 0)
            return new(false, "没有可用的已配置搜索目录；没有压缩文件夹。", "SEARCH_ROOT_UNAVAILABLE");

        if (!TryOpenOrdinaryPath(requestedDirectory, expectedDirectory: true, enumerateDirectory: true,
                out var sourceRootHandle, out var sourceRoot, out var sourceRootIdentity))
            return new(false, "源文件夹不存在、不可读，或属于链接/特殊目录；没有压缩。", "ARCHIVE_DIRECTORY_UNAVAILABLE");
        sourceRootHandle.Dispose();
        sourceRoot = Path.TrimEndingDirectorySeparator(sourceRoot);
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedDirectory))
                .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ToDisplayPath(sourceRoot))),
                StringComparison.OrdinalIgnoreCase))
            return new(false, "源文件夹路径包含链接或在解析期间发生变化；没有压缩。", "ARCHIVE_REPARSE_POINT_BLOCKED");
        var allowedRoot = allowedRoots.FirstOrDefault(root => IsWithinRoot(sourceRoot, root));
        if (allowedRoot is null)
            return new(false, "源文件夹不在设置中允许的搜索目录内；没有压缩。", "SOURCE_OUTSIDE_ALLOWED_ROOT");

        if (!TryPrepareExportDirectory(out var exportHandle, out var canonicalExportRoot, out var exportIdentity))
            return new(false, "小K导出目录不可用，或路径包含链接；没有压缩文件夹。", "EXPORT_ROOT_UNAVAILABLE");
        using (exportHandle)
        {
            if (IsWithinRoot(sourceRoot, canonicalExportRoot) || IsWithinRoot(canonicalExportRoot, sourceRoot))
                return new(false, "源文件夹与小K导出目录重叠；为避免把导出内容再次打包，没有压缩。", "ARCHIVE_SOURCE_EXPORT_OVERLAP");
            if (!IsCurrentDirectoryPath(sourceRoot, sourceRoot, sourceRootIdentity))
                return new(false, "源文件夹在检查期间发生变化；没有压缩。", "ARCHIVE_DIRECTORY_CHANGED");

            if (!TryCollectDirectoryArchiveInventory(sourceRoot, allowedRoot, cancellationToken,
                    out var files, out var directories, out var inventoryFailure))
                return inventoryFailure!;
            if (!directories.Any(directory => directory.FullPath.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)
                    && directory.Identity == sourceRootIdentity))
                return new(false, "源文件夹在压缩前发生变化；没有压缩。", "ARCHIVE_DIRECTORY_CHANGED");

            var directoryName = Path.GetFileName(sourceRoot);
            if (!LocalDirectoryArchivePolicy.IsSafeEntryName(directoryName))
                return new(false, "源文件夹名称不能安全地用于 ZIP 条目；没有压缩。", "INVALID_ARCHIVE_DIRECTORY_NAME");
            var archiveName = directoryName + ".zip";
            if (archiveName.Length > 240 || !IsSafeExportFileName(archiveName))
                return new(false, "源文件夹名称不能安全地用作压缩包名称；没有压缩。", "INVALID_ARCHIVE_FILE_NAME");

            var finalPath = Path.Combine(canonicalExportRoot, archiveName);
            var temporaryPath = Path.Combine(canonicalExportRoot, $".xiaok-directory-archive-{Guid.NewGuid():N}.partial");
            var published = false;
            try
            {
                if (File.Exists(finalPath) || Directory.Exists(finalPath))
                    return new(false, "小K导出目录中已有同名压缩包；为避免覆盖，没有压缩。", "EXPORT_NAME_CONFLICT");
                if (!IsCurrentDirectoryPath(_exportRoot!, canonicalExportRoot, exportIdentity))
                    return new(false, "小K导出目录在压缩前发生变化；没有处理源文件夹。", "EXPORT_ROOT_CHANGED");

                var entryHashes = new Dictionary<string, ArchiveEntryDigest>(StringComparer.Ordinal);
                long temporaryLength;
                using (var archiveFile = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                           FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    using (var archive = new ZipArchive(archiveFile, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        foreach (var source in files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var entryName = directoryName + "/" + source.RelativePath;
                            if (!LocalDirectoryArchivePolicy.IsSafeEntryName(entryName))
                                return new(false, "文件夹中存在不能安全表示为 ZIP 条目的文件名；没有发布压缩包。", "INVALID_ARCHIVE_ENTRY_NAME");
                            if (!TryOpenOrdinaryFileForRead(source.FullPath, out var sourceHandle, out var canonicalFile,
                                    out var fileIdentity, out var fileLength, out var fileWriteTime)
                                || fileIdentity != source.Identity
                                || !canonicalFile.Equals(source.FullPath, StringComparison.OrdinalIgnoreCase)
                                || fileLength != source.Length || fileWriteTime != source.WriteTime)
                            {
                                sourceHandle?.Dispose();
                                return new(false, "源文件在压缩前发生变化或替换；没有发布压缩包。", "SOURCE_CHANGED_DURING_ARCHIVE");
                            }

                            using var sourceStream = new FileStream(sourceHandle, FileAccess.Read, 64 * 1024, isAsync: false);
                            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            await using (var entryStream = entry.Open())
                            {
                                var buffer = new byte[64 * 1024];
                                long copiedBytes = 0;
                                while (true)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    var read = sourceStream.Read(buffer, 0, buffer.Length);
                                    if (read == 0) break;
                                    copiedBytes += read;
                                    if (copiedBytes > LocalDirectoryArchivePolicy.MaximumFileBytes
                                        || copiedBytes > source.Length)
                                        return new(false, "源文件在压缩期间增长或超过单文件100 MiB上限；没有发布压缩包。", "ARCHIVE_FILE_TOO_LARGE");
                                    hash.AppendData(buffer, 0, read);
                                    await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                                    if (archiveFile.Length > LocalDirectoryArchivePolicy.MaximumArchiveBytes)
                                        return new(false, "临时压缩包超过540 MiB上限；没有发布压缩包。", "ARCHIVE_OUTPUT_TOO_LARGE");
                                }
                                if (copiedBytes != source.Length
                                    || !TryGetFileIdentity(sourceStream.SafeFileHandle, out var finalFileIdentity)
                                    || finalFileIdentity != source.Identity
                                    || !TryGetFileSnapshot(sourceStream.SafeFileHandle, out var lengthAfterArchive,
                                        out var writeTimeAfterArchive)
                                    || lengthAfterArchive != source.Length || writeTimeAfterArchive != source.WriteTime)
                                    return new(false, "源文件在压缩期间发生变化；没有发布压缩包。", "SOURCE_CHANGED_DURING_ARCHIVE");
                            }
                            entryHashes.Add(entryName,
                                new ArchiveEntryDigest(source.Length, Convert.ToHexString(hash.GetHashAndReset())));
                        }
                    }
                    await archiveFile.FlushAsync(cancellationToken).ConfigureAwait(false);
                    archiveFile.Flush(flushToDisk: true);
                    temporaryLength = archiveFile.Length;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (temporaryLength <= 0 || temporaryLength > LocalDirectoryArchivePolicy.MaximumArchiveBytes)
                    return new(false, "临时压缩包大小超出限制；没有发布。", "ARCHIVE_OUTPUT_INVALID");
                if (!TryCollectDirectoryArchiveInventory(sourceRoot, allowedRoot, cancellationToken,
                        out var filesAfterArchive, out var directoriesAfterArchive, out inventoryFailure)
                    || !files.SequenceEqual(filesAfterArchive)
                    || !directories.SequenceEqual(directoriesAfterArchive))
                    return new(false, "源文件夹内容在压缩期间发生变化；没有发布压缩包。", "SOURCE_DIRECTORY_CHANGED_DURING_ARCHIVE");
                if (directories.Any(directory => !IsCurrentDirectoryPath(directory.FullPath,
                        directory.FullPath, directory.Identity)))
                    return new(false, "源文件夹结构在压缩期间发生变化；没有发布压缩包。", "SOURCE_DIRECTORY_CHANGED_DURING_ARCHIVE");

                if (!IsCurrentDirectoryPath(_exportRoot!, canonicalExportRoot, exportIdentity))
                    return new(false, "小K导出目录在压缩期间发生变化；没有发布压缩包。", "EXPORT_ROOT_CHANGED");
                if (!TryVerifyDirectoryArchive(temporaryPath, entryHashes, cancellationToken, out var verificationError))
                    return new(false, verificationError, "ARCHIVE_VERIFICATION_FAILED");

                if (!TryOpenOrdinaryFileForRead(temporaryPath, out var temporaryHandle, out _, out _,
                        out var savedLength, out _) || savedLength != temporaryLength)
                {
                    temporaryHandle?.Dispose();
                    return new(false, "临时压缩包大小或文件类型异常；没有发布。", "ARCHIVE_OUTPUT_INVALID");
                }
                using (temporaryHandle)
                {
                    if (!TryComputeSha256(temporaryHandle, out var archiveHash))
                        return new(false, "无法核验临时压缩包；没有发布。", "ARCHIVE_OUTPUT_UNVERIFIABLE");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentDirectoryPath(_exportRoot!, canonicalExportRoot, exportIdentity))
                        return new(false, "小K导出目录在发布压缩包前发生变化；没有发布。", "EXPORT_ROOT_CHANGED");
                    try { File.Move(temporaryPath, finalPath, overwrite: false); }
                    catch (IOException) when (File.Exists(finalPath) || Directory.Exists(finalPath))
                    { return new(false, "导出目录中出现同名压缩包；为避免覆盖，没有发布。", "EXPORT_NAME_CONFLICT"); }
                    published = true;

                    if (!TryOpenOrdinaryFileForRead(finalPath, out var savedHandle, out var savedPath,
                            out _, out var savedArchiveLength, out _))
                        return ArchiveOutcomeUncertain(archiveName);
                    using (savedHandle)
                    {
                        if (!savedPath.Equals(finalPath, StringComparison.OrdinalIgnoreCase)
                            || savedArchiveLength != temporaryLength
                            || !TryComputeSha256(savedHandle, out var savedHash)
                            || !string.Equals(archiveHash, savedHash, StringComparison.Ordinal)
                            || !IsCurrentDirectoryPath(_exportRoot!, canonicalExportRoot, exportIdentity))
                            return ArchiveOutcomeUncertain(archiveName);
                    }
                }

                var totalBytes = files.Sum(file => file.Length);
                return new(true,
                    $"已压缩文件夹到小K导出目录：{archiveName}（{files.Count:N0}个文件，{totalBytes:N0}字节；原文件夹保留）。",
                    Data: ToDisplayPath(finalPath));
            }
            catch (OperationCanceledException) when (!published) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                or ArgumentException or NotSupportedException or InvalidDataException or Win32Exception)
            {
                return published
                    ? ArchiveOutcomeUncertain(archiveName)
                    : new(false, "文件夹压缩失败；源文件未改动，且没有覆盖导出目录中的文件。", "FILE_ARCHIVE_FAILED");
            }
            finally
            {
                if (!published && File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
                }
            }
        }
    }

    private static bool TryCollectDirectoryArchiveInventory(string sourceRoot, string allowedRoot,
        CancellationToken cancellationToken, out IReadOnlyList<ArchiveDirectoryFile> files,
        out IReadOnlyList<ArchiveDirectorySnapshot> directories, out ToolResult? failure)
    {
        files = [];
        directories = [];
        failure = null;
        if (!TryOpenOrdinaryPath(sourceRoot, expectedDirectory: true, enumerateDirectory: true,
                out var rootHandle, out var canonicalRoot, out var rootIdentity)
            || !canonicalRoot.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)
            || !IsWithinRoot(canonicalRoot, allowedRoot))
        {
            rootHandle?.Dispose();
            failure = new(false, "源文件夹不再是已配置搜索范围内的普通本机目录；没有压缩。", "ARCHIVE_DIRECTORY_CHANGED");
            return false;
        }
        rootHandle.Dispose();

        var foundFiles = new List<ArchiveDirectoryFile>();
        var foundDirectories = new List<ArchiveDirectorySnapshot>();
        var queue = new Queue<(string Path, int Depth, FileIdentity Identity)>();
        queue.Enqueue((canonicalRoot, 0, rootIdentity));
        long totalBytes = 0;
        var scannedEntries = 0;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (currentPath, depth, expectedIdentity) = queue.Dequeue();
            if (!TryOpenOrdinaryPath(currentPath, expectedDirectory: true, enumerateDirectory: true,
                    out var directoryHandle, out var canonicalDirectory, out var directoryIdentity)
                || directoryIdentity != expectedIdentity
                || !IsWithinRoot(canonicalDirectory, canonicalRoot))
            {
                directoryHandle?.Dispose();
                failure = new(false, "目录结构在扫描期间发生变化或包含链接；没有压缩文件夹。", "ARCHIVE_DIRECTORY_CHANGED");
                return false;
            }

            using (directoryHandle)
            {
                foundDirectories.Add(new ArchiveDirectorySnapshot(canonicalDirectory, directoryIdentity));
                var remaining = LocalDirectoryArchivePolicy.MaximumScannedEntries - scannedEntries;
                if (remaining <= 0 || !TryEnumerateDirectoryEntries(directoryHandle, directoryIdentity,
                        remaining + 1, out var entries))
                {
                    failure = new(false, "无法完整安全地枚举源文件夹；没有压缩。", "ARCHIVE_DIRECTORY_ENUMERATION_FAILED");
                    return false;
                }
                if (entries.Count > remaining)
                {
                    failure = new(false, "源文件夹超过5,000个目录项上限；没有压缩。", "ARCHIVE_DIRECTORY_ENTRY_LIMIT");
                    return false;
                }
                scannedEntries += entries.Count;

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((entry.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    {
                        failure = new(false, "源文件夹中包含链接、重解析点或特殊设备项；为避免越界读取，已停止压缩。", "ARCHIVE_REPARSE_POINT_BLOCKED");
                        return false;
                    }

                    var entryPath = Path.Combine(canonicalDirectory, entry.Name);
                    if ((entry.Attributes & (uint)FileAttributes.Directory) != 0)
                    {
                        if (depth >= LocalDirectoryArchivePolicy.MaximumDepth)
                        {
                            failure = new(false, "源文件夹超过5层递归深度上限；没有压缩。", "ARCHIVE_DIRECTORY_DEPTH_LIMIT");
                            return false;
                        }
                        if (!TryOpenOrdinaryPath(entryPath, expectedDirectory: true, enumerateDirectory: true,
                                out var childHandle, out var childPath, out var childIdentity)
                            || childIdentity != entry.Identity || !IsWithinRoot(childPath, canonicalRoot))
                        {
                            childHandle?.Dispose();
                            failure = new(false, "源文件夹在扫描期间发生变化或包含链接；没有压缩。", "ARCHIVE_DIRECTORY_CHANGED");
                            return false;
                        }
                        childHandle.Dispose();
                        queue.Enqueue((childPath, depth + 1, childIdentity));
                        continue;
                    }

                    if (foundFiles.Count >= LocalDirectoryArchivePolicy.MaximumFiles)
                    {
                        failure = new(false, "源文件夹超过1,000个文件上限；没有压缩。", "ARCHIVE_FILE_COUNT_LIMIT");
                        return false;
                    }
                    if (!TryOpenOrdinaryFileForRead(entryPath, out var fileHandle, out var canonicalFile,
                            out var fileIdentity, out var fileLength, out var fileWriteTime)
                        || fileIdentity != entry.Identity || !IsWithinRoot(canonicalFile, canonicalRoot))
                    {
                        fileHandle?.Dispose();
                        failure = new(false, "源文件夹中包含不可读取或已变化的特殊文件；没有压缩。", "ARCHIVE_FILE_UNAVAILABLE");
                        return false;
                    }
                    using (fileHandle)
                    {
                        if (fileLength > LocalDirectoryArchivePolicy.MaximumFileBytes)
                        {
                            failure = new(false, "文件夹中有文件超过100 MiB单文件上限；没有压缩。", "ARCHIVE_FILE_TOO_LARGE");
                            return false;
                        }
                    }
                    if (fileLength > LocalDirectoryArchivePolicy.MaximumTotalBytes - totalBytes)
                    {
                        failure = new(false, "源文件夹总文件大小超过500 MiB上限；没有压缩。", "ARCHIVE_TOTAL_SIZE_LIMIT");
                        return false;
                    }
                    totalBytes += fileLength;
                    var relativePath = Path.GetRelativePath(canonicalRoot, canonicalFile).Replace('\\', '/');
                    if (!LocalDirectoryArchivePolicy.IsSafeEntryName(relativePath))
                    {
                        failure = new(false, "源文件夹中有不能安全表示为 ZIP 条目的路径；没有压缩。", "INVALID_ARCHIVE_ENTRY_NAME");
                        return false;
                    }
                    foundFiles.Add(new ArchiveDirectoryFile(canonicalFile, relativePath, fileIdentity, fileLength, fileWriteTime));
                }
            }
        }

        if (foundFiles.Count == 0)
        {
            failure = new(false, "源文件夹中没有可压缩的普通文件；没有创建空压缩包。", "ARCHIVE_DIRECTORY_EMPTY");
            return false;
        }
        files = foundFiles.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        directories = foundDirectories.OrderBy(directory => directory.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        return true;
    }

    private static bool TryVerifyDirectoryArchive(string archivePath,
        IReadOnlyDictionary<string, ArchiveEntryDigest> expectedEntries,
        CancellationToken cancellationToken, out string error)
    {
        error = "压缩包内容没有通过独立核验；没有发布。";
        try
        {
            using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count != expectedEntries.Count) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!LocalDirectoryArchivePolicy.IsSafeEntryName(entry.FullName)
                    || !seen.Add(entry.FullName)
                    || !expectedEntries.TryGetValue(entry.FullName, out var expected)
                    || entry.Length != expected.Length) return false;

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var content = entry.Open();
                var buffer = new byte[64 * 1024];
                long copiedBytes = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = content.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    copiedBytes += read;
                    if (copiedBytes > expected.Length) return false;
                    hash.AppendData(buffer, 0, read);
                }
                if (copiedBytes != expected.Length
                    || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expected.Sha256,
                        StringComparison.Ordinal)) return false;
            }
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed record ArchiveDirectoryFile(string FullPath, string RelativePath, FileIdentity Identity,
        long Length, long WriteTime);
    private sealed record ArchiveDirectorySnapshot(string FullPath, FileIdentity Identity);
    private sealed record ArchiveEntryDigest(long Length, string Sha256);
}
