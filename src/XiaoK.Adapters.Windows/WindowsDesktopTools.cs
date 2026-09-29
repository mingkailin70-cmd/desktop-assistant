using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
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

        var safeRoots = existingRoots.Where(IsOrdinaryDirectory).ToArray();
        if (safeRoots.Length == 0)
            return new(false, "搜索目录是链接或重解析点，已拒绝遍历。", "SEARCH_ROOT_NOT_LOCAL_DIRECTORY");

        var matches = new List<string>(10);
        var scanned = 0;
        var queue = new Queue<(string Path, string Root, int Depth)>();
        foreach (var root in safeRoots) queue.Enqueue((root, root, 0));
        while (queue.Count > 0 && scanned < 5000 && matches.Count < 10)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, root, depth) = queue.Dequeue();
            if (!IsWithinRoot(current, root) || !IsOrdinaryDirectory(current)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    if (++scanned > 5000) break;
                    if (IsOrdinaryFile(file) && Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase)) matches.Add(file);
                    if (matches.Count == 10) break;
                }
                if (depth >= 5 || scanned >= 5000) continue;
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    if (IsWithinRoot(child, root) && IsOrdinaryDirectory(child)) queue.Enqueue((child, root, depth + 1));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
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
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOrdinaryDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsOrdinaryFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == 0 && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool FindVisibleWindow(DesktopApp app)
    {
        var processNames = app.Id.ToLowerInvariant() switch
        {
            "vscode" => new[] { "Code" }, "edge" => new[] { "msedge" }, "explorer" => new[] { "explorer" },
            "wechat" => new[] { "Weixin" }, "qq" => new[] { "QQ" }, _ => Array.Empty<string>()
        };
        var expectedProjectName = string.IsNullOrWhiteSpace(app.WorkingDirectory)
            ? null : new DirectoryInfo(Path.TrimEndingDirectorySeparator(app.WorkingDirectory)).Name;
        foreach (var name in processNames)
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero || !IsWindowVisible(handle)) continue;
                if (app.Id.Equals("vscode", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(expectedProjectName))
                    return process.MainWindowTitle.Contains(expectedProjectName, StringComparison.OrdinalIgnoreCase);
                return true;
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
