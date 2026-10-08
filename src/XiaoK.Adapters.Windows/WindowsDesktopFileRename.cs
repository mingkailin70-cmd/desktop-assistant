using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public sealed partial class WindowsDesktopTools
{
    private const uint RenameFileReadAttributes = 0x00000080;
    private const uint RenameFileAddFile = 0x00000002;
    private const uint RenameDeleteAccess = 0x00010000;
    private const uint RenameShareRead = 0x00000001;
    private const uint RenameShareWrite = 0x00000002;
    private const uint RenameOpenExisting = 3;
    private const uint RenameOpenReparsePoint = 0x00200000;
    private const uint RenameBackupSemantics = 0x02000000;
    private const int RenameFileInformationClass = 3;
    private const int RenameErrorFileExists = 80;
    private const int RenameErrorAlreadyExists = 183;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeFileRenameInfo
    {
        [MarshalAs(UnmanagedType.U1)] public bool ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    public Task<ToolResult> RenameFileAsync(ToolProposal proposal, CancellationToken cancellationToken) =>
        Task.Run(() => RenameFile(proposal, cancellationToken), cancellationToken);

    private ToolResult RenameFile(ToolProposal proposal, CancellationToken cancellationToken)
    {
        if (!proposal.Arguments.TryGetValue("source_path", out var requestedSource)
            || !LocalFileRenamePolicy.IsValidSourcePath(requestedSource)
            || !proposal.Arguments.TryGetValue("new_name", out var newName)
            || !LocalFileRenamePolicy.IsValidFileName(newName))
            return new(false, "请提供搜索范围内的本机完整源路径和一个有效的新文件名。", "INVALID_RENAME_ARGUMENTS");

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
            return new(false, "没有可用的已配置搜索目录；没有重命名文件。", "SEARCH_ROOT_UNAVAILABLE");

        if (!TryOpenOrdinaryFileForRename(requestedSource, out var sourceHandle, out var sourcePath,
                out var sourceIdentity, out var sourceLength, out var sourceWriteTime))
            return new(false, "源文件不存在、正在被占用，或属于链接/特殊文件；没有重命名。", "SOURCE_FILE_UNAVAILABLE");

        using (sourceHandle)
        {
            if (!roots.Any(root => IsWithinRoot(sourcePath, root)))
                return new(false, "源文件不在设置中允许的搜索目录内；没有重命名。", "SOURCE_OUTSIDE_ALLOWED_ROOT");

            var parentPath = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(parentPath) || !roots.Any(root => IsWithinRoot(parentPath, root))
                || !TryOpenOrdinaryDirectoryForRename(parentPath, out var parentHandle,
                    out var canonicalParent, out var parentIdentity))
                return new(false, "源文件父目录不安全或已离开允许范围；没有重命名。", "RENAME_PARENT_UNAVAILABLE");

            using (parentHandle)
            {
                if (!canonicalParent.Equals(parentPath, StringComparison.OrdinalIgnoreCase))
                    return new(false, "源文件父目录路径无法稳定核验；没有重命名。", "RENAME_PARENT_UNAVAILABLE");

                var oldName = Path.GetFileName(sourcePath);
                if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
                    return new(false, "新文件名与当前名称相同；没有执行重命名。", "RENAME_NAME_UNCHANGED");

                var destinationPath = Path.Combine(canonicalParent, newName);
                if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                    return new(false, "同一目录中已存在该名称；为避免覆盖，没有重命名。", "RENAME_NAME_CONFLICT");

                if (!IsCurrentDirectoryPath(parentPath, canonicalParent, parentIdentity)
                    || !TryOpenOrdinaryFileForRead(sourcePath, out var currentSourceHandle,
                        out var currentSourcePath, out var currentSourceIdentity, out _, out _))
                    return new(false, "源文件或所在目录在执行前发生变化；没有重命名。", "RENAME_SOURCE_CHANGED");
                using (currentSourceHandle)
                {
                    if (!currentSourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
                        || currentSourceIdentity != sourceIdentity)
                        return new(false, "源路径已指向其他文件；没有重命名。", "RENAME_SOURCE_CHANGED");
                }

                cancellationToken.ThrowIfCancellationRequested();
                var nativeTargetPath = ToDisplayPath(destinationPath);
                var nameBytes = Encoding.Unicode.GetBytes(nativeTargetPath);
                var nameOffset = Marshal.OffsetOf<NativeFileRenameInfo>(nameof(NativeFileRenameInfo.FileName)).ToInt32();
                // Include the native struct tail padding in addition to the flexible filename tail.
                var informationSize = checked(Marshal.SizeOf<NativeFileRenameInfo>() + nameBytes.Length - sizeof(char));
                var allocationSize = Math.Max(Marshal.SizeOf<NativeFileRenameInfo>(), informationSize);
                var buffer = Marshal.AllocHGlobal(allocationSize);
                try
                {
                    Marshal.Copy(new byte[allocationSize], 0, buffer, allocationSize);
                    var info = new NativeFileRenameInfo
                    {
                        ReplaceIfExists = false,
                        RootDirectory = IntPtr.Zero,
                        FileNameLength = checked((uint)nameBytes.Length),
                        FileName = '\0'
                    };
                    Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
                    Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, nameOffset), nameBytes.Length);
                    if (!SetFileInformationByHandle(sourceHandle, RenameFileInformationClass, buffer,
                            checked((uint)informationSize)))
                    {
                        var error = Marshal.GetLastWin32Error();
                        return error is RenameErrorFileExists or RenameErrorAlreadyExists
                            ? new(false, "同一目录中已出现该名称；为避免覆盖，没有重命名。", "RENAME_NAME_CONFLICT")
                            : new(false, "Windows 未能在原目录中安全重命名该文件；源文件未被小K覆盖。", "FILE_RENAME_FAILED");
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }

                // Once the atomic rename request succeeds, finish verification even if the caller cancels.
                // Never issue a second rename attempt.
                if (!TryOpenOrdinaryFileForRead(destinationPath, out var renamedHandle,
                        out var verifiedPath, out var renamedIdentity, out var renamedLength, out _))
                    return RenameOutcomeUncertain(newName);
                using (renamedHandle)
                {
                    if (!verifiedPath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase)
                        || renamedIdentity != sourceIdentity || renamedLength != sourceLength
                        || File.Exists(sourcePath) || Directory.Exists(sourcePath)
                        || !IsCurrentDirectoryPath(parentPath, canonicalParent, parentIdentity)
                        || !TryGetFileSnapshot(renamedHandle, out var lengthAfterRename, out var writeTimeAfterRename)
                        || lengthAfterRename != sourceLength || writeTimeAfterRename != sourceWriteTime)
                        return RenameOutcomeUncertain(newName);
                }

                return new(true, $"已在原目录中将“{oldName}”重命名为“{newName}”，并核验文件身份与内容元数据未变。",
                    Data: ToDisplayPath(destinationPath));
            }
        }
    }

    private static bool TryOpenOrdinaryFileForRename(string path, out SafeFileHandle handle, out string canonicalPath,
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
            candidate = CreateFileW(path, RenameDeleteAccess | RenameFileReadAttributes,
                RenameShareRead | RenameShareWrite, IntPtr.Zero, RenameOpenExisting,
                RenameOpenReparsePoint, IntPtr.Zero);
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

    private static bool TryOpenOrdinaryDirectoryForRename(string path, out SafeFileHandle handle,
        out string canonicalPath, out FileIdentity identity)
    {
        handle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        canonicalPath = "";
        identity = default;
        SafeFileHandle? candidate = null;
        try
        {
            candidate = CreateFileW(path, RenameFileReadAttributes | RenameFileAddFile,
                RenameShareRead | RenameShareWrite, IntPtr.Zero, RenameOpenExisting,
                RenameOpenReparsePoint | RenameBackupSemantics, IntPtr.Zero);
            if (candidate.IsInvalid || !GetFileInformationByHandle(candidate, out var information)
                || (((FileAttributes)information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint))
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

    private static ToolResult RenameOutcomeUncertain(string newName) =>
        new(false, $"Windows 已执行重命名请求，但无法独立确认“{newName}”的最终状态；请人工核对当前目录，小K不会自动重试。",
            "FILE_RENAME_OUTCOME_UNCERTAIN", newName, TaskLifecycleState.OutcomeUncertain);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
        IntPtr fileInformation, uint bufferSize);
}
