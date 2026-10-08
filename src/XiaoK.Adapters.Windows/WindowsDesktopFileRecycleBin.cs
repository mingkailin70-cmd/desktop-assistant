using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

/// <summary>在内存中绑定一个已检查文件，审批期间不把路径或文件状态写入历史。</summary>
public sealed class PreparedRecycleBinItem
{
    internal PreparedRecycleBinItem(string canonicalTargetPath, object snapshot)
    {
        CanonicalTargetPath = canonicalTargetPath;
        Snapshot = snapshot;
    }

    public string CanonicalTargetPath { get; }
    internal object Snapshot { get; }
}

public sealed partial class WindowsDesktopTools
{
    private const uint RecycleOperationDelete = 3;
    private const ushort RecycleFlagSilent = 0x0004;
    private const ushort RecycleFlagNoConfirmation = 0x0010;
    private const ushort RecycleFlagAllowUndo = 0x0040;
    private const ushort RecycleFlagNoErrorUi = 0x0400;
    private const ushort RecycleFlagNoConfirmMkdir = 0x0200;
    private const ushort RecycleFlagNoRecursion = 0x1000;
    private const ushort RecycleFlagNoConnectedElements = 0x2000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr WindowHandle;
        public uint Operation;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    private sealed record RecycleSnapshot(string SourcePath, FileIdentity FileIdentity, long Length,
        long LastWriteTime, string ParentPath, FileIdentity ParentIdentity, string SearchRoot,
        FileIdentity SearchRootIdentity);

    public bool TryPrepareFileForRecycleBin(string requestedSourcePath, out PreparedRecycleBinItem? prepared,
        out ToolResult? failure)
    {
        prepared = null;
        failure = new(false, "请指定一个设置中搜索目录里的本机普通文件；没有移动文件。", "INVALID_RECYCLE_TARGET");
        if (!LocalFileRecycleBinPolicy.IsValidSourcePath(requestedSourcePath)) return false;

        var roots = new List<(string Path, FileIdentity Identity)>();
        foreach (var configuredRoot in _searchRoots.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryOpenOrdinaryPath(configuredRoot, expectedDirectory: true, enumerateDirectory: false,
                    out var rootHandle, out var canonicalRoot, out var rootIdentity)) continue;
            rootHandle.Dispose();
            if (roots.All(root => !root.Path.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)))
                roots.Add((canonicalRoot, rootIdentity));
        }

        if (!TryOpenOrdinaryFileForRename(requestedSourcePath, out var sourceHandle, out var sourcePath,
                out var sourceIdentity, out var length, out var writeTime))
        {
            failure = new(false, "文件不存在、被占用，或属于链接/特殊文件；没有移动文件。", "RECYCLE_SOURCE_UNAVAILABLE");
            return false;
        }

        using (sourceHandle)
        {
            var root = roots.FirstOrDefault(candidate => IsWithinRoot(sourcePath, candidate.Path));
            var parentPath = Path.GetDirectoryName(sourcePath);
            if (root.Path is null || string.IsNullOrWhiteSpace(parentPath)
                || !IsWithinRoot(parentPath, root.Path)
                || !TryOpenOrdinaryPath(parentPath, expectedDirectory: true, enumerateDirectory: false,
                    out var parentHandle, out var canonicalParent, out var parentIdentity))
            {
                failure = new(false, "文件不在可用的设置搜索目录内，或其父目录不安全；没有移动文件。", "RECYCLE_SOURCE_OUTSIDE_ALLOWED_ROOT");
                return false;
            }

            using (parentHandle)
            {
                if (!canonicalParent.Equals(parentPath, StringComparison.OrdinalIgnoreCase))
                {
                    failure = new(false, "文件父目录无法稳定核验；没有移动文件。", "RECYCLE_PARENT_UNAVAILABLE");
                    return false;
                }

                var displayPath = ToDisplayPath(sourcePath);
                if (displayPath.Length >= 260)
                {
                    failure = new(false, "Windows 回收站接口不支持此路径长度；没有移动文件。", "RECYCLE_PATH_TOO_LONG");
                    return false;
                }

                var snapshot = new RecycleSnapshot(sourcePath, sourceIdentity, length, writeTime,
                    parentPath, parentIdentity, root.Path, root.Identity);
                prepared = new PreparedRecycleBinItem(displayPath, snapshot);
                failure = null;
                return true;
            }
        }
    }

    public async Task<ToolResult> RecyclePreparedFileAsync(PreparedRecycleBinItem prepared,
        CancellationToken cancellationToken)
    {
        if (prepared?.Snapshot is not RecycleSnapshot snapshot)
            return new(false, "回收站操作计划无效；没有移动文件。", "INVALID_RECYCLE_PLAN");
        if (cancellationToken.IsCancellationRequested)
            return new(false, "任务已取消；Windows 回收站操作尚未提交。", "FILE_RECYCLE_CANCELLED_BEFORE_OPERATION",
                FinalState: TaskLifecycleState.Cancelled);

        if (!IsCurrentDirectoryPath(snapshot.SearchRoot, snapshot.SearchRoot, snapshot.SearchRootIdentity)
            || !IsCurrentDirectoryPath(snapshot.ParentPath, snapshot.ParentPath, snapshot.ParentIdentity)
            || !TryOpenOrdinaryFileForRename(snapshot.SourcePath, out var sourceHandle, out var currentPath,
                out var currentIdentity, out var currentLength, out var currentWriteTime))
            return new(false, "确认期间搜索目录或文件发生变化；为保护目标，本次没有移动文件。", "RECYCLE_TARGET_CHANGED");

        using (sourceHandle)
        {
            if (!currentPath.Equals(snapshot.SourcePath, StringComparison.OrdinalIgnoreCase)
                || currentIdentity != snapshot.FileIdentity || currentLength != snapshot.Length
                || currentWriteTime != snapshot.LastWriteTime
                || !IsWithinRoot(currentPath, snapshot.SearchRoot)
                || !IsCurrentDirectoryPath(snapshot.ParentPath, snapshot.ParentPath, snapshot.ParentIdentity))
                return new(false, "确认期间文件身份、内容元数据或父目录发生变化；本次没有移动文件。", "RECYCLE_TARGET_CHANGED");
        }

        if (cancellationToken.IsCancellationRequested)
            return new(false, "任务已取消；Windows 回收站操作尚未提交。", "FILE_RECYCLE_CANCELLED_BEFORE_OPERATION",
                FinalState: TaskLifecycleState.Cancelled);
        var operationPath = ToDisplayPath(snapshot.SourcePath);
        ShellOperationResult operation;
        try
        {
            // Shell file operations are synchronous; do not abandon a submitted operation if the user cancels.
            operation = await Task.Run(() => SendToRecycleBin(operationPath), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or Win32Exception or ExternalException)
        {
            return RecycleOutcomeUncertain();
        }

        if (!TryOpenOrdinaryFileForRead(snapshot.SourcePath, out var remainingHandle, out _,
                out var remainingIdentity, out _, out _))
        {
            if (TryGetAttributes(snapshot.SourcePath, out _) || !IsMissingPathError(Marshal.GetLastWin32Error()))
                return RecycleOutcomeUncertain();
            if (operation.ReturnCode == 0 && !operation.WasAborted
                && IsCurrentDirectoryPath(snapshot.ParentPath, snapshot.ParentPath, snapshot.ParentIdentity))
                return new(true, "Windows 已将指定文件移入回收站，并核验原位置已不存在。", Data: "已移入回收站");
            return RecycleOutcomeUncertain();
        }

        using (remainingHandle)
        {
            if (remainingIdentity != snapshot.FileIdentity) return RecycleOutcomeUncertain();
        }

        return operation.ReturnCode != 0 || operation.WasAborted
            ? new(false, "Windows 未能把文件移入回收站；已核验原文件仍在原位置，没有自动重试。", "FILE_RECYCLE_FAILED")
            : new(false, "Windows 未确认回收站操作；已核验原文件仍在原位置，没有自动重试。", "FILE_RECYCLE_NOT_CONFIRMED");
    }

    private static ShellOperationResult SendToRecycleBin(string fullyQualifiedPath)
    {
        var normalizedPath = ToDisplayPath(fullyQualifiedPath);
        if (normalizedPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(normalizedPath))
            return new(87, false);

        var source = Marshal.StringToHGlobalUni(normalizedPath + "\0");
        var operation = new ShellFileOperation
        {
            WindowHandle = IntPtr.Zero,
            Operation = RecycleOperationDelete,
            From = source,
            To = IntPtr.Zero,
            Flags = RecycleFlagAllowUndo | RecycleFlagSilent | RecycleFlagNoConfirmation
                | RecycleFlagNoErrorUi | RecycleFlagNoConfirmMkdir | RecycleFlagNoRecursion
                | RecycleFlagNoConnectedElements
        };
        try
        {
            var code = SHFileOperationW(ref operation);
            return new(code, operation.AnyOperationsAborted);
        }
        finally { Marshal.FreeHGlobal(source); }
    }

    private static bool TryGetAttributes(string path, out uint attributes)
    {
        attributes = GetFileAttributesW(path);
        return attributes != uint.MaxValue;
    }

    private static bool IsMissingPathError(int error) => error is 2 or 3;

    private static ToolResult RecycleOutcomeUncertain() =>
        new(false, "Windows 已收到回收站请求，但最终状态无法确认。请手动检查原文件和回收站；小K不会自动重试。",
            "FILE_RECYCLE_OUTCOME_UNCERTAIN", FinalState: TaskLifecycleState.OutcomeUncertain);

    private readonly record struct ShellOperationResult(int ReturnCode, bool WasAborted);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref ShellFileOperation operation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesW")]
    private static extern uint GetFileAttributesW(string path);
}
