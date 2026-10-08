using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public sealed partial class WindowsDesktopTools
{
    private const uint MoveFileReadAttributes = 0x00000080;
    private const uint MoveFileAddFile = 0x00000002;
    private const uint MoveDeleteAccess = 0x00010000;
    private const uint MoveShareRead = 0x00000001;
    private const uint MoveShareWrite = 0x00000002;
    private const uint MoveOpenExisting = 3;
    private const uint MoveOpenReparsePoint = 0x00200000;
    private const uint MoveBackupSemantics = 0x02000000;
    private const int MoveFileInformationClass = 3;
    private const int MoveErrorFileExists = 80;
    private const int MoveErrorAlreadyExists = 183;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMoveFileRenameInfo
    {
        [MarshalAs(UnmanagedType.U1)] public bool ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    public Task<ToolResult> MoveFileWithinSearchRootsAsync(ToolProposal proposal,
        CancellationToken cancellationToken) =>
        Task.Run(() => MoveFileWithinSearchRoots(proposal, cancellationToken), cancellationToken);

    private ToolResult MoveFileWithinSearchRoots(ToolProposal proposal, CancellationToken cancellationToken)
    {
        if (!proposal.Arguments.TryGetValue("source_path", out var requestedSource)
            || !LocalFileMovePolicy.IsValidSourcePath(requestedSource)
            || !proposal.Arguments.TryGetValue("destination_directory", out var requestedDestinationDirectory)
            || !LocalFileMovePolicy.IsValidDestinationDirectoryPath(requestedDestinationDirectory))
            return new(false, "请提供搜索范围内的本机源文件路径和目标目录完整路径。", "INVALID_MOVE_ARGUMENTS");

        var roots = new List<string>();
        foreach (var configuredRoot in _searchRoots.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryOpenOrdinaryPath(configuredRoot, expectedDirectory: true, enumerateDirectory: false,
                    out var rootHandle, out var canonicalRoot, out _)) continue;
            rootHandle.Dispose();
            if (roots.All(root => !root.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))) roots.Add(canonicalRoot);
        }
        if (roots.Count == 0)
            return new(false, "没有可用的已配置搜索目录；没有移动文件。", "SEARCH_ROOT_UNAVAILABLE");

        if (!TryOpenOrdinaryFileForMove(requestedSource, out var sourceHandle, out var sourcePath,
                out var sourceIdentity, out var sourceLength, out var sourceWriteTime))
            return new(false, "源文件不存在、正在被占用，或属于链接/特殊文件；没有移动。", "SOURCE_FILE_UNAVAILABLE");

        using (sourceHandle)
        {
            if (!roots.Any(root => IsWithinRoot(sourcePath, root)))
                return new(false, "源文件不在设置中允许的搜索目录内；没有移动。", "SOURCE_OUTSIDE_ALLOWED_ROOT");

            if (!TryOpenOrdinaryDirectoryForMove(requestedDestinationDirectory, out var destinationDirectoryHandle,
                    out var destinationDirectory, out var destinationDirectoryIdentity))
                return new(false, "目标目录不存在、不可写，或属于链接/特殊目录；没有移动。", "MOVE_DESTINATION_UNAVAILABLE");

            using (destinationDirectoryHandle)
            {
                if (!roots.Any(root => IsWithinRoot(destinationDirectory, root)))
                    return new(false, "目标目录不在设置中允许的搜索范围内；没有移动。", "DESTINATION_OUTSIDE_ALLOWED_ROOT");
                if (sourceIdentity.VolumeSerialNumber != destinationDirectoryIdentity.VolumeSerialNumber)
                    return new(false, "源文件与目标目录不在同一卷；为保证移动具有原子性，没有移动。", "MOVE_CROSS_VOLUME_UNSUPPORTED");

                var fileName = Path.GetFileName(sourcePath);
                var destinationPath = Path.Combine(destinationDirectory, fileName);
                var sourceParent = Path.GetDirectoryName(sourcePath);
                if (string.IsNullOrWhiteSpace(sourceParent)
                    || destinationPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                    return new(false, "目标目录与源文件所在目录相同；没有移动。", "MOVE_DESTINATION_UNCHANGED");
                if (!roots.Any(root => IsWithinRoot(sourceParent, root)))
                    return new(false, "源文件父目录无法稳定核验；没有移动。", "MOVE_SOURCE_PARENT_UNAVAILABLE");

                if (!TryOpenOrdinaryDirectoryForMove(sourceParent, out var sourceParentHandle,
                        out var canonicalSourceParent, out var sourceParentIdentity))
                    return new(false, "源文件父目录无法稳定核验；没有移动。", "MOVE_SOURCE_PARENT_UNAVAILABLE");

                using (sourceParentHandle)
                {
                    if (!canonicalSourceParent.Equals(sourceParent, StringComparison.OrdinalIgnoreCase))
                        return new(false, "源文件父目录路径无法稳定核验；没有移动。", "MOVE_SOURCE_PARENT_UNAVAILABLE");

                    if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                        return new(false, "目标目录中已存在同名项目；为避免覆盖，没有移动。", "MOVE_DESTINATION_CONFLICT");

                    if (!IsCurrentDirectoryPath(sourceParent, canonicalSourceParent, sourceParentIdentity)
                        || !IsCurrentDirectoryPath(requestedDestinationDirectory, destinationDirectory,
                            destinationDirectoryIdentity)
                        || !TryOpenOrdinaryFileForRead(sourcePath, out var currentSourceHandle,
                            out var currentSourcePath, out var currentSourceIdentity, out _, out _))
                        return new(false, "源文件或目标目录在执行前发生变化；没有移动。", "MOVE_TARGET_CHANGED");
                    using (currentSourceHandle)
                    {
                        if (!currentSourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
                            || currentSourceIdentity != sourceIdentity)
                            return new(false, "源路径已指向其他文件；没有移动。", "MOVE_TARGET_CHANGED");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    // The relative RootDirectory form returned ERROR_INVALID_PARAMETER in the
                    // local Windows probe. Keep the verified destination handle open without
                    // FILE_SHARE_DELETE and use the documented fully qualified path form.
                    var nameBytes = Encoding.Unicode.GetBytes(destinationPath);
                    var nameOffset = Marshal.OffsetOf<NativeMoveFileRenameInfo>(nameof(NativeMoveFileRenameInfo.FileName)).ToInt32();
                    // Include native structure tail padding in addition to the flexible filename tail.
                    var informationSize = checked(Marshal.SizeOf<NativeMoveFileRenameInfo>() + nameBytes.Length - sizeof(char));
                    var allocationSize = Math.Max(Marshal.SizeOf<NativeMoveFileRenameInfo>(), informationSize);
                    var buffer = Marshal.AllocHGlobal(allocationSize);
                    try
                    {
                        Marshal.Copy(new byte[allocationSize], 0, buffer, allocationSize);
                        var info = new NativeMoveFileRenameInfo
                        {
                            ReplaceIfExists = false,
                            RootDirectory = IntPtr.Zero,
                            FileNameLength = checked((uint)nameBytes.Length),
                            FileName = '\0'
                        };
                        Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
                        Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, nameOffset), nameBytes.Length);
                        if (!SetFileInformationByHandle(sourceHandle, MoveFileInformationClass, buffer,
                                checked((uint)informationSize)))
                        {
                            var error = Marshal.GetLastWin32Error();
                            return error is MoveErrorFileExists or MoveErrorAlreadyExists
                                ? new(false, "目标目录中刚出现同名项目；为避免覆盖，没有移动。", "MOVE_DESTINATION_CONFLICT")
                                : new(false, $"Windows 未能在已核验的搜索范围内安全移动该文件（错误码 {error}）；源文件未被小K覆盖。", "FILE_MOVE_FAILED");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    // Once the atomic move request succeeds, complete verification even if cancellation arrives.
                    // Never issue a second move attempt.
                    if (!TryOpenOrdinaryFileForRead(destinationPath, out var movedHandle,
                            out var verifiedPath, out var movedIdentity, out var movedLength, out _))
                        return MoveOutcomeUncertain(fileName, destinationDirectory);
                    using (movedHandle)
                    {
                        if (!verifiedPath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase)
                            || movedIdentity != sourceIdentity || movedLength != sourceLength
                            || File.Exists(sourcePath) || Directory.Exists(sourcePath)
                            || !IsCurrentDirectoryPath(sourceParent, canonicalSourceParent, sourceParentIdentity)
                            || !IsCurrentDirectoryPath(requestedDestinationDirectory, destinationDirectory,
                                destinationDirectoryIdentity)
                            || !TryGetFileSnapshot(movedHandle, out var lengthAfterMove, out var writeTimeAfterMove)
                            || lengthAfterMove != sourceLength || writeTimeAfterMove != sourceWriteTime)
                            return MoveOutcomeUncertain(fileName, destinationDirectory);
                    }

                    var displaySource = SanitizeMovePath(ToDisplayPath(sourcePath));
                    var displayDestination = SanitizeMovePath(ToDisplayPath(destinationPath));
                    return new(true, $"已将文件移入目标目录并核验文件身份及内容元数据：{displaySource} → {displayDestination}",
                        Data: displayDestination);
                }
            }
        }
    }

    private static bool TryOpenOrdinaryFileForMove(string path, out SafeFileHandle handle, out string canonicalPath,
        out FileIdentity identity, out long length, out long writeTime)
    {
        handle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        canonicalPath = "";
        identity = default;
        length = 0;
        writeTime = 0;
        SafeFileHandle? candidate = null;
        try
        {
            // Do not share write access: an existing writer makes this open fail, and new
            // writers cannot start while the move is being committed and independently checked.
            candidate = CreateFileW(path, MoveDeleteAccess | MoveFileReadAttributes,
                MoveShareRead, IntPtr.Zero, MoveOpenExisting, MoveOpenReparsePoint, IntPtr.Zero);
            if (candidate.IsInvalid || !GetFileInformationByHandle(candidate, out var information)) return false;
            var attributes = (FileAttributes)information.FileAttributes;
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || information.NumberOfLinks != 1 || !TryGetFileIdentity(candidate, out identity)) return false;
            canonicalPath = GetFinalPath(candidate);
            length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
            writeTime = ((long)(uint)information.LastWriteTime.dwHighDateTime << 32)
                | (uint)information.LastWriteTime.dwLowDateTime;
            handle = candidate;
            candidate = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException or Win32Exception)
        { return false; }
        finally { candidate?.Dispose(); }
    }

    private static bool TryOpenOrdinaryDirectoryForMove(string path, out SafeFileHandle handle,
        out string canonicalPath, out FileIdentity identity)
    {
        handle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        canonicalPath = "";
        identity = default;
        SafeFileHandle? candidate = null;
        try
        {
            candidate = CreateFileW(path, MoveFileReadAttributes | MoveFileAddFile,
                MoveShareRead | MoveShareWrite, IntPtr.Zero, MoveOpenExisting,
                MoveOpenReparsePoint | MoveBackupSemantics, IntPtr.Zero);
            if (candidate.IsInvalid || !GetFileInformationByHandle(candidate, out var information)
                || (((FileAttributes)information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint
                    | FileAttributes.Device))
                    != FileAttributes.Directory)
                || !TryGetFileIdentity(candidate, out identity)) return false;
            canonicalPath = GetFinalPath(candidate);
            handle = candidate;
            candidate = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException or Win32Exception)
        { return false; }
        finally { candidate?.Dispose(); }
    }

    private static ToolResult MoveOutcomeUncertain(string fileName, string destinationDirectory) =>
        new(false, $"Windows 已执行移动请求，但无法独立确认“{SanitizeMovePath(fileName)}”的最终状态；请人工核对目标目录，小K不会自动重试。",
            "FILE_MOVE_OUTCOME_UNCERTAIN", SanitizeMovePath(ToDisplayPath(destinationDirectory)),
            TaskLifecycleState.OutcomeUncertain);

    private static string SanitizeMovePath(string path)
    {
        var safe = new string(path.Select(character =>
        {
            var category = char.GetUnicodeCategory(character);
            return char.IsControl(character) || category is UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? '\uFFFD' : character;
        }).ToArray());
        return safe.Length <= 1_024 ? safe : safe[..1_023] + "…";
    }
}
