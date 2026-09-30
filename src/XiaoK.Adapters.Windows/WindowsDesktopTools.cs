using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public sealed record DesktopApp(string Id, string Executable, string? WorkingDirectory = null);

public sealed class WindowsDesktopTools
{
    private readonly IReadOnlyDictionary<string, DesktopApp> _apps;
    private readonly IReadOnlyDictionary<string, string> _searchRoots;

    public WindowsDesktopTools(IEnumerable<DesktopApp> apps, IEnumerable<KeyValuePair<string, string>> searchRoots)
    {
        var allowedApps = new Dictionary<string, DesktopApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredApp in apps ?? [])
        {
            if (configuredApp is null || string.IsNullOrWhiteSpace(configuredApp.Id) || configuredApp.Id.Length > 64)
                continue;

            var executable = configuredApp.Id.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                && string.Equals(configuredApp.Executable, "explorer.exe", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Environment.SystemDirectory, "explorer.exe")
                : NormalizeLocalPath(configuredApp.Executable);
            var workingDirectory = configuredApp.WorkingDirectory is null
                ? null
                : NormalizeLocalPath(configuredApp.WorkingDirectory);
            if (executable is null || (configuredApp.WorkingDirectory is not null && workingDirectory is null)) continue;

            allowedApps.TryAdd(configuredApp.Id, configuredApp with
            {
                Executable = executable,
                WorkingDirectory = workingDirectory
            });
        }
        _apps = allowedApps;

        var allowedRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredRoot in searchRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(configuredRoot.Key) || configuredRoot.Key.Length > 64) continue;
            var root = NormalizeLocalPath(configuredRoot.Value);
            if (root is null || IsDriveRoot(root)) continue;
            allowedRoots.TryAdd(configuredRoot.Key, root);
        }
        _searchRoots = allowedRoots;
    }

    public async Task<ToolResult> LaunchAsync(ToolProposal proposal, CancellationToken cancellationToken)
    {
        if (!proposal.Arguments.TryGetValue("app_id", out var appId) || !_apps.TryGetValue(appId, out var app))
            return new(false, "这个应用未在本机允许列表中。", "APP_NOT_ALLOWLISTED");
        try
        {
            var start = new ProcessStartInfo(app.Executable) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(app.WorkingDirectory)) start.WorkingDirectory = app.WorkingDirectory;
            if (app.Id.Equals("vscode", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(app.WorkingDirectory))
            {
                start.ArgumentList.Add("--new-window");
                start.ArgumentList.Add(app.WorkingDirectory);
            }
            else if (proposal.Arguments.TryGetValue("workspace_id", out var workspace) && workspace == "xiaok" &&
                     !string.IsNullOrWhiteSpace(app.WorkingDirectory))
            {
                start.ArgumentList.Add(app.WorkingDirectory);
            }

            using var launched = Process.Start(start);
            if (launched is null) return new(false, "Windows 未返回启动进程。", "APP_LAUNCH_FAILED");

            var until = DateTimeOffset.UtcNow.AddSeconds(8);
            while (DateTimeOffset.UtcNow < until)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FindVisibleWindow(app))
                    return new(true, $"已启动并检测到 {app.Id} 的窗口。", Data: app.WorkingDirectory);
                await Task.Delay(250, cancellationToken);
            }
            return new(false, "已发出启动请求，但没有检测到目标窗口。", "WINDOW_NOT_VERIFIED");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        { return new(false, "应用启动失败；请检查本地路径设置。", "APP_LAUNCH_FAILED"); }
    }

    public Task<ToolResult> SearchFilesAsync(ToolProposal proposal, CancellationToken cancellationToken) =>
        Task.Run(() => SearchFiles(proposal, cancellationToken), cancellationToken);

    private ToolResult SearchFiles(ToolProposal proposal, CancellationToken cancellationToken)
    {
        if (!proposal.Arguments.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query) || query.Length > 120)
            return new(false, "请输入 1–120 个字符的文件名或关键词。", "INVALID_QUERY");

        var rootId = proposal.Arguments.GetValueOrDefault("root_id", "user-files");
        var roots = rootId.Equals("user-files", StringComparison.OrdinalIgnoreCase)
            ? _searchRoots.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : _searchRoots.TryGetValue(rootId, out var configuredRoot) ? [configuredRoot] : Array.Empty<string>();
        var existingRoots = roots.Where(Directory.Exists).ToArray();
        if (existingRoots.Length == 0)
            return new(false, "搜索目录未配置或不可用。", "SEARCH_ROOT_UNAVAILABLE");

        var safeRoots = new List<(string Path, FileIdentity Identity)>();
        foreach (var path in existingRoots)
        {
            if (!TryOpenOrdinaryPath(path, expectedDirectory: true, enumerateDirectory: false,
                    out var rootHandle, out var canonicalPath, out var rootIdentity)) continue;
            rootHandle.Dispose();
            if (safeRoots.All(root => !root.Path.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase)))
                safeRoots.Add((canonicalPath, rootIdentity));
        }
        if (safeRoots.Count == 0)
            return new(false, "搜索目录是链接或重解析点，已拒绝遍历。", "SEARCH_ROOT_NOT_LOCAL_DIRECTORY");

        var matches = new List<string>(10);
        var scanned = 0;
        var queue = new Queue<(string Path, string Root, int Depth, FileIdentity? ExpectedIdentity)>();
        foreach (var root in safeRoots) queue.Enqueue((root.Path, root.Path, 0, root.Identity));
        while (queue.Count > 0 && scanned < 5000 && matches.Count < 10)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, root, depth, expectedIdentity) = queue.Dequeue();
            if (!TryOpenOrdinaryPath(current, expectedDirectory: true, enumerateDirectory: true,
                    out var directoryHandle, out var verifiedDirectory, out var directoryIdentity)) continue;
            using (directoryHandle)
            {
                if ((expectedIdentity.HasValue && expectedIdentity.Value != directoryIdentity)
                    || !IsWithinRoot(verifiedDirectory, root)) continue;
                if (!TryEnumerateDirectoryEntries(directoryHandle, directoryIdentity, 5000 - scanned, out var entries)) continue;

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanned++;
                    if ((entry.Attributes & (uint)FileAttributes.ReparsePoint) != 0) continue;

                    var entryPath = Path.Combine(verifiedDirectory, entry.Name);
                    var isDirectory = (entry.Attributes & (uint)FileAttributes.Directory) != 0;
                    if (isDirectory)
                    {
                        if (depth >= 5 || !TryOpenOrdinaryPath(entryPath, expectedDirectory: true,
                                enumerateDirectory: false, out var childHandle, out var childPath, out var childIdentity)) continue;
                        childHandle.Dispose();
                        if (childIdentity == entry.Identity && IsWithinRoot(childPath, root))
                            queue.Enqueue((childPath, root, depth + 1, childIdentity));
                        continue;
                    }

                    if ((entry.Attributes & (uint)FileAttributes.Device) != 0
                        || !Path.GetFileName(entry.Name).Contains(query, StringComparison.OrdinalIgnoreCase)
                        || !TryOpenOrdinaryPath(entryPath, expectedDirectory: false, enumerateDirectory: false,
                            out var fileHandle, out var filePath, out var fileIdentity)) continue;
                    using (fileHandle)
                    {
                        if (fileIdentity == entry.Identity && IsWithinRoot(filePath, root))
                            matches.Add(ToDisplayPath(filePath));
                    }

                    if (matches.Count == 10 || scanned >= 5000) break;
                }
            }
        }

        var result = matches.Count == 0 ? "没有找到匹配文件。" : string.Join(Environment.NewLine, matches);
        return new(true, matches.Count == 0 ? result : $"找到 {matches.Count} 个结果：", Data: result);
    }

    private static string? NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return root is not null && root.Length >= 3 && root[1] == ':'
                ? Path.TrimEndingDirectorySeparator(fullPath)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsDriveRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var fullPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryOpenOrdinaryPath(string path, bool expectedDirectory, bool enumerateDirectory,
        out SafeFileHandle handle, out string canonicalPath, out FileIdentity identity)
    {
        handle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        canonicalPath = "";
        identity = default;
        SafeFileHandle? candidate = null;
        try
        {
            var desiredAccess = FileReadAttributes | (enumerateDirectory ? FileListDirectory : 0u);
            candidate = CreateFileW(path, desiredAccess, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero,
                OpenExisting, OpenReparsePoint | (expectedDirectory ? BackupSemantics : 0u), IntPtr.Zero);
            if (candidate.IsInvalid || !GetFileInformationByHandle(candidate, out var information)) return false;

            var attributes = (FileAttributes)information.FileAttributes;
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isDirectory != expectedDirectory || (attributes & FileAttributes.ReparsePoint) != 0) return false;

            canonicalPath = GetFinalPath(candidate);
            if (canonicalPath.Length == 0 || !TryGetFileIdentity(candidate, out identity)) return false;
            handle = candidate;
            candidate = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException or Win32Exception)
        {
            return false;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    private static bool TryEnumerateDirectoryEntries(SafeFileHandle directoryHandle, FileIdentity parentIdentity, int maximumEntries,
        out IReadOnlyList<EnumeratedEntry> entries)
    {
        const int BufferSize = 64 * 1024;
        const int FileInformationClassIdExtendedDirectoryInfo = 19;
        const int FileNameOffset = 88;
        const int ErrorNoMoreFiles = 18;
        const int ErrorHandleEof = 38;
        var result = new List<EnumeratedEntry>(Math.Min(maximumEntries, 128));
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            while (result.Count < maximumEntries)
            {
                if (!GetFileInformationByHandleEx(directoryHandle, FileInformationClassIdExtendedDirectoryInfo,
                        buffer, (uint)BufferSize))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is ErrorNoMoreFiles or ErrorHandleEof)
                    {
                        entries = result;
                        return true;
                    }
                    entries = [];
                    return false;
                }

                var offset = 0;
                var foundEntry = false;
                while (true)
                {
                    if (offset < 0 || offset > BufferSize - FileNameOffset)
                    {
                        entries = [];
                        return false;
                    }
                    var header = Marshal.PtrToStructure<FileIdExtendedDirectoryInfoPrefix>(IntPtr.Add(buffer, offset));
                    if (header.FileNameLength > (uint)(BufferSize - FileNameOffset - offset))
                    {
                        entries = [];
                        return false;
                    }
                    var nameLength = (int)header.FileNameLength;
                    if ((nameLength & 1) != 0)
                    {
                        entries = [];
                        return false;
                    }

                    var namePointer = IntPtr.Add(buffer, offset + FileNameOffset);
                    var name = Marshal.PtrToStringUni(namePointer, nameLength / sizeof(char));
                    if (name is null)
                    {
                        entries = [];
                        return false;
                    }
                    if (name is not ("." or ".."))
                    {
                        result.Add(new EnumeratedEntry(name, header.FileAttributes,
                            new FileIdentity(parentIdentity.VolumeSerialNumber, header.FileIdLow, header.FileIdHigh)));
                        foundEntry = true;
                        if (result.Count >= maximumEntries) break;
                    }

                    if (header.NextEntryOffset == 0) break;
                    if (header.NextEntryOffset > (uint)(BufferSize - offset))
                    {
                        entries = [];
                        return false;
                    }
                    var nextOffset = (int)header.NextEntryOffset;
                    if (nextOffset < FileNameOffset + nameLength || (nextOffset & 7) != 0
                        || nextOffset > BufferSize - offset - FileNameOffset)
                    {
                        entries = [];
                        return false;
                    }
                    offset += nextOffset;
                }

                if (result.Count >= maximumEntries) break;
                if (!foundEntry)
                {
                    entries = [];
                    return false;
                }
            }

            entries = result;
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TryGetFileIdentity(SafeFileHandle handle, out FileIdentity identity)
    {
        const int FileIdInfoClass = 18;
        identity = default;
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out var nativeIdentity,
                (uint)Marshal.SizeOf<NativeFileIdInfo>())) return false;
        if (nativeIdentity.FileIdLow == 0 && nativeIdentity.FileIdHigh == 0) return false;
        identity = new FileIdentity(nativeIdentity.VolumeSerialNumber, nativeIdentity.FileIdLow, nativeIdentity.FileIdHigh);
        return true;
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)capacity, 0);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length < capacity) return buffer.ToString();
            capacity = checked((int)length + 1);
        }
        throw new IOException("Windows 返回的最终路径超出安全上限。");
    }

    private static string ToDisplayPath(string path) => path.StartsWith("\\\\?\\", StringComparison.Ordinal)
        && path.Length >= 7 && char.IsAsciiLetter(path[4]) && path[5] == ':'
            ? path[4..]
            : path;

    private static bool FindVisibleWindow(DesktopApp app)
    {
        var processName = Path.GetFileNameWithoutExtension(app.Executable);
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var expectedProjectName = string.IsNullOrWhiteSpace(app.WorkingDirectory)
            ? null : new DirectoryInfo(Path.TrimEndingDirectorySeparator(app.WorkingDirectory)).Name;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var imagePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(imagePath)
                    || !string.Equals(Path.GetFullPath(imagePath), app.Executable, StringComparison.OrdinalIgnoreCase))
                    continue;

                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero || !IsWindowVisible(handle)) continue;
                if (app.Id.Equals("vscode", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(expectedProjectName))
                    return process.MainWindowTitle.Contains(expectedProjectName, StringComparison.OrdinalIgnoreCase);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException or NotSupportedException or ArgumentException) { }
            finally { process.Dispose(); }
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const uint FileReadAttributes = 0x00000080;
    private const uint FileListDirectory = 0x00000001;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    // Native FILE_ID_EXTD_DIR_INFO prefix from winbase.h; FileName starts at byte 88.
    [StructLayout(LayoutKind.Explicit, Size = 88)]
    private struct FileIdExtendedDirectoryInfoPrefix
    {
        [FieldOffset(0)] public uint NextEntryOffset;
        [FieldOffset(56)] public uint FileAttributes;
        [FieldOffset(60)] public uint FileNameLength;
        [FieldOffset(72)] public ulong FileIdLow;
        [FieldOffset(80)] public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    private readonly record struct FileIdentity(ulong VolumeSerialNumber, ulong FileIdLow, ulong FileIdHigh);
    private sealed record EnumeratedEntry(string Name, uint Attributes, FileIdentity Identity);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        IntPtr information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out NativeFileIdInfo information, uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);
}
