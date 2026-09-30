using System.ComponentModel;
using System.Diagnostics;
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
    private const int MaximumCapturedCharactersPerStream = 8_000;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);
    private readonly string? _repositoryRoot;

    public DotNetTestRunner(string? repositoryRoot) => _repositoryRoot = repositoryRoot;

    public string? ExecutablePath => ResolveExecutablePath(_repositoryRoot);

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
        var restore = await RunCommandAsync(approvedExecutablePath, workingDirectory,
            ["restore", targetFile, "--configfile", nugetConfig, "--source", "https://api.nuget.org/v3/index.json",
                "--packages", packageCache],
            dotNetHome, packageCache, tempRoot, userProfileRoot, roamingRoot, localRoot, cancellationToken);
        if (!restore.Started || restore.TimedOut || restore.ExitCode != 0)
            return new(restore.Started, restore.ExitCode, false, null,
                restore.TimedOut ? "dotnet restore" : null, "依赖还原输出：\n" + restore.Output);

        var test = await RunCommandAsync(approvedExecutablePath, workingDirectory,
            ["test", targetFile, "--no-restore"], dotNetHome, packageCache, tempRoot,
            userProfileRoot, roamingRoot, localRoot, cancellationToken);
        return new(true, restore.ExitCode, test.Started, test.ExitCode,
            test.TimedOut ? "dotnet test" : null,
            "依赖还原输出：\n" + restore.Output + "\n测试输出：\n" + test.Output);
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
            + $"2. \"{executablePath}\" test \"{targetName}\" --no-restore\n工作目录：{targetDirectory}\nNuGet 源：仅 nuget.org；包缓存：隔离工作区内。";
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

    private static async Task<SingleCommandResult> RunCommandAsync(string executablePath, string workingDirectory,
        IReadOnlyList<string> arguments, string dotNetHome, string packageCache, string tempRoot,
        string userProfileRoot, string roamingRoot, string localRoot,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        SetRestrictedEnvironment(startInfo, executablePath, dotNetHome, packageCache, tempRoot,
            userProfileRoot, roamingRoot, localRoot);
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_HOME"] = dotNetHome;
        startInfo.Environment["NUGET_PACKAGES"] = packageCache;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) return new(false, false, null, "dotnet.exe 未启动。");
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(false, false, null, "无法启动本机 dotnet.exe。");
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaximumCapturedCharactersPerStream);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaximumCapturedCharactersPerStream);
        using var timeout = new CancellationTokenSource(CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var timedOut = false;
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception) { }
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            timedOut = true;
        }

        var output = await CombineOutputAsync(stdoutTask, stderrTask);
        return new(true, timedOut, process.HasExited ? process.ExitCode : null, output);
    }

    private sealed record SingleCommandResult(bool Started, bool TimedOut, int? ExitCode, string Output);

    private static void SetRestrictedEnvironment(ProcessStartInfo startInfo, string executablePath,
        string dotNetHome, string packageCache, string tempRoot, string userProfileRoot,
        string roamingRoot, string localRoot)
    {
        startInfo.Environment.Clear();
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Path.Combine(systemRoot, "System32");
        var dotNetRoot = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        startInfo.Environment["SystemRoot"] = systemRoot;
        startInfo.Environment["WINDIR"] = systemRoot;
        startInfo.Environment["PATH"] = dotNetRoot + Path.PathSeparator + system32;
        startInfo.Environment["DOTNET_ROOT"] = dotNetRoot;
        startInfo.Environment["DOTNET_ROOT_X64"] = dotNetRoot;
        startInfo.Environment["DOTNET_CLI_HOME"] = dotNetHome;
        startInfo.Environment["NUGET_PACKAGES"] = packageCache;
        startInfo.Environment["TEMP"] = tempRoot;
        startInfo.Environment["TMP"] = tempRoot;
        startInfo.Environment["USERPROFILE"] = userProfileRoot;
        startInfo.Environment["APPDATA"] = roamingRoot;
        startInfo.Environment["LOCALAPPDATA"] = localRoot;
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 2_048));
        var buffer = new char[2_048];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) break;
            var remaining = maximumCharacters - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        if (truncated) output.Append("\n[输出已截断]");
        return output.ToString();
    }

    private static async Task<string> CombineOutputAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        var output = await stdoutTask;
        var error = await stderrTask;
        if (string.IsNullOrWhiteSpace(error)) return output;
        return string.IsNullOrWhiteSpace(output) ? "标准错误：\n" + error : output + "\n标准错误：\n" + error;
    }

    private static void TryKillProcessTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}
