using System.Text;

namespace XiaoK.Tools;

public sealed record DotNetTestExecutionResult(bool RestoreStarted, int? RestoreExitCode, bool TestStarted,
    int? TestExitCode, string? TimedOutCommand, string Output);

public interface IDotNetTestRunner
{
    string? ExecutablePath { get; }
    Task<DotNetTestExecutionResult> RunAsync(string workspacePath, string verificationRoot,
        string targetRelativePath, string approvedExecutablePath, CancellationToken cancellationToken);
}

/// <summary>Runs fixed restore and test argument lists for one captured target; it has no shell or user-supplied arguments.</summary>
public sealed class DotNetTestRunner : IDotNetTestRunner
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);
    private readonly string? _repositoryRoot;
    private readonly string? _recoveryJournalRoot;

    public DotNetTestRunner(string? repositoryRoot, string? recoveryJournalRoot = null)
    {
        _repositoryRoot = repositoryRoot;
        _recoveryJournalRoot = recoveryJournalRoot;
        StartupIsolationRecovery = AppContainerCommandRunner.RecoverAbandonedRuns(recoveryJournalRoot);
    }

    public string? ExecutablePath => ResolveExecutablePath(_repositoryRoot);
    public AppContainerRecoverySummary StartupIsolationRecovery { get; }

    public async Task<DotNetTestExecutionResult> RunAsync(string workspacePath, string verificationRoot,
        string targetRelativePath, string approvedExecutablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(approvedExecutablePath) || !File.Exists(approvedExecutablePath)
            || !string.Equals(Path.GetFullPath(approvedExecutablePath), ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return new(false, null, false, null, null, "dotnet.exe 路径已变化；没有运行命令。");

        if (!TryResolveTarget(workspacePath, targetRelativePath, out var workingDirectory, out var targetFile)
            || !TryResolveVerificationRoot(workspacePath, verificationRoot, out var resolvedVerificationRoot))
            return new(false, null, false, null, null, "隔离工作区中的固定测试目标失效或越界；没有运行命令。");

        string nugetConfig;
        string packageCache;
        string tempRoot;
        string userProfileRoot;
        string roamingRoot;
        string localRoot;
        try
        {
            Directory.CreateDirectory(resolvedVerificationRoot);
            if ((File.GetAttributes(resolvedVerificationRoot) & FileAttributes.ReparsePoint) != 0)
                return new(false, null, false, null, null, "验证缓存目录是重解析点；没有运行命令。");
            nugetConfig = Path.Combine(resolvedVerificationRoot, "NuGet.Config");
            packageCache = Path.Combine(resolvedVerificationRoot, "packages");
            tempRoot = Path.Combine(resolvedVerificationRoot, "temp");
            userProfileRoot = Path.Combine(resolvedVerificationRoot, "user-profile");
            roamingRoot = Path.Combine(resolvedVerificationRoot, "appdata-roaming");
            localRoot = Path.Combine(resolvedVerificationRoot, "appdata-local");
            Directory.CreateDirectory(packageCache);
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(userProfileRoot);
            Directory.CreateDirectory(roamingRoot);
            Directory.CreateDirectory(localRoot);
            File.WriteAllText(nugetConfig,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" protocolVersion=\"3\"/></packageSources></configuration>",
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(false, null, false, null, null, "无法在隔离工作区准备 NuGet 缓存和固定源配置；没有运行命令。");
        }

        var dotNetHome = Path.Combine(resolvedVerificationRoot, "dotnet-home");
        var restore = await RunCommandAsync(approvedExecutablePath, workingDirectory, workspacePath,
            ["restore", targetFile, "--configfile", nugetConfig, "--source", "https://api.nuget.org/v3/index.json",
                "--packages", packageCache],
            dotNetHome, packageCache, tempRoot, userProfileRoot, roamingRoot, localRoot, allowInternet: true, cancellationToken);
        if (!restore.Started || restore.TimedOut || restore.ExitCode != 0)
            return new(restore.Started, restore.ExitCode, false, null,
                restore.TimedOut ? "dotnet restore" : null, "依赖还原输出：\n" + restore.Output);

        var test = await RunCommandAsync(approvedExecutablePath, workingDirectory, workspacePath,
            ["test", targetFile, "--no-restore"], dotNetHome, packageCache, tempRoot,
            userProfileRoot, roamingRoot, localRoot, allowInternet: false, cancellationToken);
        return new(true, restore.ExitCode, test.Started, test.ExitCode,
            test.TimedOut ? "dotnet test" : null,
            "依赖还原输出：\n" + restore.Output + "\n测试输出：\n" + test.Output);
    }

    /// <summary>
    /// Runs the fixed synthetic benchmark fixture without any network capability. Both restore
    /// and execution happen in the same AppContainer profile used by ordinary code verification.
    /// </summary>
    public async Task<DotNetTestExecutionResult> RunOfflineRepairFixtureAsync(string workspacePath,
        string verificationRoot, string approvedExecutablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(approvedExecutablePath) || !File.Exists(approvedExecutablePath)
            || !string.Equals(Path.GetFullPath(approvedExecutablePath), ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return new(false, null, false, null, null, "dotnet.exe 路径已变化；没有运行命令。");

        const string targetRelativePath = "repair-fixture/RepairFixture.csproj";
        if (!TryResolveTarget(workspacePath, targetRelativePath, out var workingDirectory, out var targetFile)
            || !TryResolveVerificationRoot(workspacePath, verificationRoot, out var resolvedVerificationRoot))
            return new(false, null, false, null, null, "隔离工作区中的固定修复夹具目标失效或越界；没有运行命令。");

        string emptyFeed;
        string packageCache;
        string tempRoot;
        string dotNetHome;
        string userProfileRoot;
        string roamingRoot;
        string localRoot;
        string nugetConfig;
        try
        {
            Directory.CreateDirectory(resolvedVerificationRoot);
            if ((File.GetAttributes(resolvedVerificationRoot) & FileAttributes.ReparsePoint) != 0)
                return new(false, null, false, null, null, "验证缓存目录是重解析点；没有运行命令。");
            emptyFeed = Path.Combine(resolvedVerificationRoot, "empty-feed");
            packageCache = Path.Combine(resolvedVerificationRoot, "packages");
            tempRoot = Path.Combine(resolvedVerificationRoot, "temp");
            dotNetHome = Path.Combine(resolvedVerificationRoot, "dotnet-home");
            userProfileRoot = Path.Combine(resolvedVerificationRoot, "user-profile");
            roamingRoot = Path.Combine(resolvedVerificationRoot, "appdata-roaming");
            localRoot = Path.Combine(resolvedVerificationRoot, "appdata-local");
            nugetConfig = Path.Combine(resolvedVerificationRoot, "NuGet.Config");
            Directory.CreateDirectory(emptyFeed);
            Directory.CreateDirectory(packageCache);
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(dotNetHome);
            Directory.CreateDirectory(userProfileRoot);
            Directory.CreateDirectory(roamingRoot);
            Directory.CreateDirectory(localRoot);
            File.WriteAllText(nugetConfig,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear/><add key=\"offline-empty\" value=\""
                    + System.Security.SecurityElement.Escape(emptyFeed)
                    + "\"/></packageSources></configuration>", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(false, null, false, null, null, "无法准备离线修复夹具；没有运行命令。");
        }

        var restore = await RunCommandAsync(approvedExecutablePath, workingDirectory, workspacePath,
            ["restore", targetFile, "--configfile", nugetConfig, "--source", emptyFeed, "--packages", packageCache,
                "-p:NuGetAudit=false"],
            dotNetHome, packageCache, tempRoot, userProfileRoot, roamingRoot, localRoot,
            allowInternet: false, cancellationToken);
        if (!restore.Started || restore.TimedOut || restore.ExitCode != 0)
            return new(restore.Started, restore.ExitCode, false, null,
                restore.TimedOut ? "dotnet restore (offline fixture)" : null, "离线依赖还原输出：\n" + restore.Output);

        var run = await RunCommandAsync(approvedExecutablePath, workingDirectory, workspacePath,
            ["run", "--project", targetFile, "--no-restore"],
            dotNetHome, packageCache, tempRoot, userProfileRoot, roamingRoot, localRoot,
            allowInternet: false, cancellationToken);
        return new(true, restore.ExitCode, run.Started, run.ExitCode,
            run.TimedOut ? "dotnet run (offline fixture)" : null,
            "离线依赖还原输出：\n" + restore.Output + "\n夹具执行输出：\n" + run.Output);
    }

    public static string? ResolveExecutablePath(string? repositoryRoot)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
            candidates.Add(Path.Combine(repositoryRoot, ".tools", "dotnet", "dotnet.exe"));
        foreach (var variable in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT(x86)" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root)) candidates.Add(Path.Combine(root, "dotnet.exe"));
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) candidates.Add(Path.Combine(programFiles, "dotnet", "dotnet.exe"));
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim().Trim('"'), "dotnet.exe")));

        return candidates.Select(candidate =>
            {
                try { return Path.GetFullPath(candidate); }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
            })
            .FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));
    }

    public static string CreateCommandPreview(string workspacePath, string verificationRoot,
        string targetRelativePath, string executablePath)
    {
        var targetName = Path.GetFileName(targetRelativePath);
        var targetDirectory = Path.GetDirectoryName(Path.Combine(workspacePath,
            targetRelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)))!;
        var config = Path.Combine(verificationRoot, "NuGet.Config");
        var packages = Path.Combine(verificationRoot, "packages");
        return $"1. \"{executablePath}\" restore \"{targetName}\" --configfile \"{config}\" --source https://api.nuget.org/v3/index.json --packages \"{packages}\"\n"
            + $"2. \"{executablePath}\" test \"{targetName}\" --no-restore\n工作目录：{targetDirectory}\nWindows AppContainer：只允许写入此隔离工作区，dotnet 运行时只读；还原步骤需要互联网能力且 NuGet 源限定为 nuget.org，测试步骤不授予网络能力。还原期间项目目标仍可能使用已批准的联网权限。";
    }

    private static bool TryResolveTarget(string workspacePath, string targetRelativePath,
        out string workingDirectory, out string targetFile)
    {
        workingDirectory = string.Empty;
        targetFile = string.Empty;
        if (string.IsNullOrWhiteSpace(workspacePath) || !Path.IsPathFullyQualified(workspacePath)
            || workspacePath.StartsWith("\\\\", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(targetRelativePath) || Path.IsPathRooted(targetRelativePath)) return false;

        var root = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = targetRelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "" or "." or "..")) return false;
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(target), ".sln", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetExtension(target), ".slnx", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetExtension(target), ".csproj", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(target) || !Directory.Exists(root)) return false;

        var current = target;
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            current = Path.GetDirectoryName(current)!;
            if (string.IsNullOrEmpty(current) || !current.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return false;

        workingDirectory = Path.GetDirectoryName(target)!;
        targetFile = Path.GetFileName(target);
        return true;
    }

    private static bool TryResolveVerificationRoot(string workspacePath, string verificationRoot, out string resolved)
    {
        resolved = string.Empty;
        if (!Path.IsPathFullyQualified(verificationRoot) || verificationRoot.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        var workspace = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(verificationRoot);
        if (!candidate.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(candidate) || File.Exists(candidate)) return false;
        resolved = candidate;
        return true;
    }

    private async Task<SingleCommandResult> RunCommandAsync(string executablePath, string workingDirectory,
        string workspaceRoot, IReadOnlyList<string> arguments, string dotNetHome, string packageCache, string tempRoot,
        string userProfileRoot, string roamingRoot, string localRoot,
        bool allowInternet, CancellationToken cancellationToken)
    {
        try
        {
            var environment = CreateRestrictedEnvironment(executablePath, dotNetHome, packageCache, tempRoot,
                userProfileRoot, roamingRoot, localRoot);
            var runtimeRoot = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
            var result = await AppContainerCommandRunner.RunAsync(executablePath, arguments, workingDirectory,
                workspaceRoot, runtimeRoot, [], environment, allowInternet, CommandTimeout, cancellationToken,
                _recoveryJournalRoot).ConfigureAwait(false);
            return new(result.Started, result.TimedOut, result.ExitCode, result.Output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new(false, false, null, "无法准备 Windows AppContainer 隔离；没有以普通用户权限回退。");
        }
    }

    private sealed record SingleCommandResult(bool Started, bool TimedOut, int? ExitCode, string Output);

    private static Dictionary<string, string> CreateRestrictedEnvironment(string executablePath,
        string dotNetHome, string packageCache, string tempRoot, string userProfileRoot,
        string roamingRoot, string localRoot)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Path.Combine(systemRoot, "System32");
        var dotNetRoot = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        environment["SystemRoot"] = systemRoot;
        environment["WINDIR"] = systemRoot;
        environment["PATH"] = dotNetRoot + Path.PathSeparator + system32;
        // NuGet computes the machine-wide config root from PROGRAMFILES(X86), then
        // PROGRAMFILES as a fallback. This restricted environment deliberately omits
        // host-wide paths, so point both variables at the already-read-only SDK root.
        // NuGet config discovery then stays inside the command's existing read boundary.
        environment["PROGRAMFILES(X86)"] = dotNetRoot;
        environment["PROGRAMFILES"] = dotNetRoot;
        environment["DOTNET_ROOT"] = dotNetRoot;
        environment["DOTNET_ROOT_X64"] = dotNetRoot;
        environment["DOTNET_CLI_HOME"] = dotNetHome;
        environment["NUGET_PACKAGES"] = packageCache;
        environment["TEMP"] = tempRoot;
        environment["TMP"] = tempRoot;
        environment["USERPROFILE"] = userProfileRoot;
        environment["APPDATA"] = roamingRoot;
        environment["LOCALAPPDATA"] = localRoot;
        environment["DOTNET_NOLOGO"] = "1";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["MSBUILDDISABLENODEREUSE"] = "1";
        return environment;
    }
}
