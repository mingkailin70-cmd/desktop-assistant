using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Security;

[assembly: InternalsVisibleTo("XiaoK.Tools.SafetyChecks")]

namespace XiaoK.Tools;

internal sealed record SandboxedCommandResult(bool Started, bool TimedOut, int? ExitCode, string Output);

/// <summary>
/// Launches approved commands in a Windows AppContainer. The process receives write access only to
/// the task workspace, read access to the fixed runtime and optional verified inputs, and network
/// capability only when the caller explicitly selects the restore step.
/// </summary>
internal static class AppContainerCommandRunner
{
    private const int MaximumCapturedCharactersPerStream = 8_000;
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const uint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint WaitObject0 = 0;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint TerminatedExitCode = 0xE0000001;
    private const int ErrorAlreadyExistsHResult = unchecked((int)0x800700B7);

    public static async Task<SandboxedCommandResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
        string workingDirectory, string writableRoot, string runtimeRoot, IEnumerable<string> additionalReadOnlyRoots,
        IReadOnlyDictionary<string, string> environment, bool allowInternet, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return new(false, false, null, "Windows AppContainer 不可用；没有以普通用户权限回退。");

        SafeFileHandle? stdoutRead = null;
        SafeFileHandle? stdoutWrite = null;
        SafeFileHandle? stderrRead = null;
        SafeFileHandle? stderrWrite = null;
        SafeFileHandle? stdin = null;
        SafeJobHandle? job = null;
        IntPtr profileSid = IntPtr.Zero;
        IntPtr capabilityGroupSid = IntPtr.Zero;
        IntPtr internetCapabilitySid = IntPtr.Zero;
        IntPtr capabilitiesBuffer = IntPtr.Zero;
        IntPtr securityCapabilitiesBuffer = IntPtr.Zero;
        IntPtr handleListBuffer = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr environmentBuffer = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;
        var attributeListInitialized = false;
        var processStarted = false;
        SecurityIdentifier? appContainerSecurityId = null;
        var profileName = "XiaoK.CodeTask." + Guid.NewGuid().ToString("N");
        var profileCreated = false;
        var permissionRoots = new List<string>();
        var phase = "参数和路径检查";

        try
        {
            var executable = Path.GetFullPath(executablePath);
            var runtime = Path.GetFullPath(runtimeRoot);
            var writable = Path.GetFullPath(writableRoot);
            var working = Path.GetFullPath(workingDirectory);
            var readOnlyRoots = additionalReadOnlyRoots.Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!File.Exists(executable) || !Directory.Exists(runtime) || !Directory.Exists(writable)
                || !Directory.Exists(working) || !IsSameOrChild(working, writable)
                || IsSameOrChild(writable, runtime) || IsSameOrChild(runtime, writable))
                return new(false, false, null, "隔离执行路径无效或运行时与可写工作区重叠；没有运行命令。");
            foreach (var root in readOnlyRoots)
                if (!Directory.Exists(root) || IsSameOrChild(root, writable) || IsSameOrChild(writable, root)
                    || IsSameOrChild(root, runtime) || IsSameOrChild(runtime, root))
                    return new(false, false, null, "隔离执行的附加只读目录无效或与其他权限范围重叠；没有运行命令。");
            for (var index = 0; index < readOnlyRoots.Length; index++)
                for (var other = index + 1; other < readOnlyRoots.Length; other++)
                    if (IsSameOrChild(readOnlyRoots[index], readOnlyRoots[other])
                        || IsSameOrChild(readOnlyRoots[other], readOnlyRoots[index]))
                        return new(false, false, null, "隔离执行的附加只读目录彼此重叠；没有运行命令。");

            phase = "创建临时 AppContainer 身份";
            profileSid = GetOrCreateProfileSid(profileName);
            profileCreated = true;
            phase = "转换 AppContainer SID";
            var subAuthorityCount = Marshal.ReadByte(profileSid, 1);
            var sidBytes = new byte[checked(8 + (subAuthorityCount * sizeof(uint)))];
            Marshal.Copy(profileSid, sidBytes, 0, sidBytes.Length);
            appContainerSecurityId = new SecurityIdentifier(sidBytes, 0);

            // Apply explicit ACEs to existing descendants as well as inheritable ACEs to new files.
            permissionRoots.Add(writable);
            phase = "授予任务工作区权限";
            GrantTreeAccess(writable, appContainerSecurityId, FileSystemRights.Modify);
            permissionRoots.Add(runtime);
            phase = "授予固定 .NET 运行时只读权限";
            GrantTreeAccess(runtime, appContainerSecurityId, FileSystemRights.ReadAndExecute);
            foreach (var root in readOnlyRoots)
            {
                permissionRoots.Add(root);
                phase = "授予验证程序集只读权限";
                GrantTreeAccess(root, appContainerSecurityId, FileSystemRights.ReadAndExecute);
            }

            phase = "创建隔离进程管道";
            var pipeSecurity = CreateInheritableSecurityAttributes();
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref pipeSecurity, 0)
                || !CreatePipe(out stderrRead, out stderrWrite, ref pipeSecurity, 0))
                throw LastWin32("无法创建隔离命令输出管道。");
            if (!SetHandleInformation(stdoutRead, HandleFlagInherit, 0)
                || !SetHandleInformation(stderrRead, HandleFlagInherit, 0))
                throw LastWin32("无法限制隔离命令的继承句柄。");
            stdin = CreateFileW("NUL", GenericRead, FileShareRead | FileShareWrite,
                CreateInheritableSecurityAttributes(), OpenExisting, 0, IntPtr.Zero);
            if (stdin.IsInvalid) throw LastWin32("无法准备隔离命令的标准输入。");

            phase = "准备进程能力和回收作业";
            job = CreateKillOnCloseJob();
            var capabilityCount = allowInternet ? 1U : 0U;
            if (allowInternet)
            {
                CheckHResult(DeriveCapabilitySidsFromName("internetClient", out capabilityGroupSid, out internetCapabilitySid),
                    "无法解析 Windows internetClient 能力。");
                var capability = new SidAndAttributes { Sid = internetCapabilitySid, Attributes = 0x00000004 };
                capabilitiesBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>());
                Marshal.StructureToPtr(capability, capabilitiesBuffer, false);
            }
            var securityCapabilities = new SecurityCapabilities
            {
                AppContainerSid = profileSid,
                Capabilities = capabilitiesBuffer,
                CapabilityCount = capabilityCount,
                Reserved = 0
            };
            securityCapabilitiesBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(securityCapabilities, securityCapabilitiesBuffer, false);

            var handleList = new[] { stdin.DangerousGetHandle(), stdoutWrite.DangerousGetHandle(), stderrWrite.DangerousGetHandle() };
            handleListBuffer = Marshal.AllocHGlobal(IntPtr.Size * handleList.Length);
            for (var index = 0; index < handleList.Length; index++)
                Marshal.WriteIntPtr(handleListBuffer, index * IntPtr.Size, handleList[index]);

            var requiredAttributeBytes = IntPtr.Zero;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref requiredAttributeBytes);
            var attributeError = Marshal.GetLastWin32Error();
            if (requiredAttributeBytes == IntPtr.Zero || attributeError != 122)
                throw new Win32Exception(attributeError, "无法测量 AppContainer 进程属性空间。");
            attributeList = Marshal.AllocHGlobal(requiredAttributeBytes);
            if (!InitializeProcThreadAttributeList(attributeList, 2, 0, ref requiredAttributeBytes))
                throw LastWin32("无法初始化 AppContainer 进程属性。");
            attributeListInitialized = true;
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeSecurityCapabilities,
                    securityCapabilitiesBuffer, (IntPtr)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero)
                || !UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList,
                    handleListBuffer, (IntPtr)(IntPtr.Size * handleList.Length), IntPtr.Zero, IntPtr.Zero))
                throw LastWin32("无法设置 AppContainer 权限或句柄白名单。");

            environmentBuffer = CreateEnvironmentBlock(environment);
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = stdin.DangerousGetHandle(),
                    StandardOutput = stdoutWrite.DangerousGetHandle(),
                    StandardError = stderrWrite.DangerousGetHandle()
                },
                AttributeList = attributeList
            };
            var commandLine = new StringBuilder(BuildCommandLine(executable, arguments));
            phase = "创建受限进程";
            if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    ExtendedStartupInfoPresent | CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                    environmentBuffer, working, ref startup, out var processInformation))
                throw LastWin32("Windows 拒绝在 AppContainer 中启动固定验证命令。");

            processHandle = processInformation.Process;
            threadHandle = processInformation.Thread;
            stdoutWrite.Dispose(); stdoutWrite = null;
            stderrWrite.Dispose(); stderrWrite = null;
            stdin.Dispose(); stdin = null;

            phase = "将子进程加入回收作业";
            if (!AssignProcessToJobObject(job, processHandle))
            {
                var error = Marshal.GetLastWin32Error();
                _ = TerminateProcess(processHandle, TerminatedExitCode);
                throw new Win32Exception(error, "无法将 AppContainer 进程放入可回收作业；没有执行命令。");
            }
            phase = "启动受限进程";
            if (ResumeThread(threadHandle) == 0xFFFFFFFF)
            {
                var error = Marshal.GetLastWin32Error();
                _ = TerminateJobObject(job, TerminatedExitCode);
                throw new Win32Exception(error, "无法恢复已隔离的验证进程；没有执行命令。");
            }
            CloseHandle(threadHandle);
            threadHandle = IntPtr.Zero;
            processStarted = true;

            using var stdoutStream = new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false);
            stdoutRead = null;
            using var stderrStream = new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false);
            stderrRead = null;
            using var stdoutReader = new StreamReader(stdoutStream, Encoding.UTF8, true, 4096, leaveOpen: true);
            using var stderrReader = new StreamReader(stderrStream, Encoding.UTF8, true, 4096, leaveOpen: true);
            var stdoutTask = ReadBoundedAsync(stdoutReader);
            var stderrTask = ReadBoundedAsync(stderrReader);
            var waitTask = Task.Run(() => WaitForProcess(processHandle));
            var timedOut = false;
            try { await waitTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                timedOut = true;
                _ = TerminateJobObject(job, TerminatedExitCode);
                await waitTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = TerminateJobObject(job, TerminatedExitCode);
                try { await waitTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                throw;
            }

            uint exitCode;
            if (!GetExitCodeProcess(processHandle, out exitCode)) throw LastWin32("无法读取隔离验证进程退出状态。");

            // Closing the kill-on-close job also terminates any descendants still holding output pipes.
            job.Dispose();
            job = null;
            var output = await CombineOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            var networkNote = allowInternet
                ? "还原步骤获得了 AppContainer internetClient 能力；nuget.org 作为唯一 NuGet 源，但 MSBuild 目标仍可能使用该联网权限。"
                : "测试步骤未授予网络能力。";
            phase = "回收临时 ACL";
            foreach (var root in permissionRoots) ClearTreeAccess(root, appContainerSecurityId);
            permissionRoots.Clear();
            phase = "回收临时 AppContainer 身份";
            var deleteResult = DeleteAppContainerProfile(profileName);
            if (deleteResult != 0)
                return new(true, timedOut, null, $"AppContainer 配置清理失败 (HRESULT {deleteResult})；验证结果不作为通过。{Environment.NewLine}{output}");
            profileCreated = false;
            return new(true, timedOut, unchecked((int)exitCode), $"已在 Windows AppContainer 中运行；临时 ACL 和身份已回收。{networkNote}{Environment.NewLine}{output}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (job is not null) _ = TerminateJobObject(job, TerminatedExitCode);
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException or InvalidOperationException or ExternalException)
        {
            if (job is not null) _ = TerminateJobObject(job, TerminatedExitCode);
            var suffix = ex is Win32Exception win32
                ? $" (Win32 {win32.NativeErrorCode})"
                : $" ({ex.GetType().Name}, HRESULT 0x{ex.HResult:X8})";
            return new(processStarted, false, null, $"AppContainer 在“{phase}”阶段失败{suffix}；没有以普通用户权限回退。请检查权限后重试。");
        }
        finally
        {
            job?.Dispose();
            if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
            if (appContainerSecurityId is not null)
            {
                foreach (var root in permissionRoots)
                {
                    try { ClearTreeAccess(root, appContainerSecurityId); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                        or ArgumentException or InvalidOperationException) { }
                }
            }
            if (profileCreated) _ = DeleteAppContainerProfile(profileName);
            if (attributeListInitialized) DeleteProcThreadAttributeList(attributeList);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (environmentBuffer != IntPtr.Zero) Marshal.FreeHGlobal(environmentBuffer);
            if (handleListBuffer != IntPtr.Zero) Marshal.FreeHGlobal(handleListBuffer);
            if (securityCapabilitiesBuffer != IntPtr.Zero) Marshal.FreeHGlobal(securityCapabilitiesBuffer);
            if (capabilitiesBuffer != IntPtr.Zero) Marshal.FreeHGlobal(capabilitiesBuffer);
            if (internetCapabilitySid != IntPtr.Zero) _ = FreeSid(internetCapabilitySid);
            if (capabilityGroupSid != IntPtr.Zero) _ = FreeSid(capabilityGroupSid);
            if (profileSid != IntPtr.Zero) _ = FreeSid(profileSid);
            stdoutRead?.Dispose(); stdoutWrite?.Dispose(); stderrRead?.Dispose(); stderrWrite?.Dispose(); stdin?.Dispose();
        }
    }

    private static IntPtr GetOrCreateProfileSid(string profileName)
    {
        if (profileName.Length > 64 || profileName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ' ')))
            throw new ArgumentException("AppContainer 配置名称无效。", nameof(profileName));
        var result = CreateAppContainerProfile(profileName, "小K隔离代码任务", "小K已批准的本机 .NET 验证进程", IntPtr.Zero, 0, out var sid);
        if (result == 0) return sid;
        if (result != ErrorAlreadyExistsHResult) throw new Win32Exception(result, "无法创建小K代码任务 AppContainer 配置文件。");
        var deriveResult = DeriveAppContainerSidFromAppContainerName(profileName, out sid);
        if (deriveResult != 0 || sid == IntPtr.Zero)
            throw Marshal.GetExceptionForHR(deriveResult) ?? new Win32Exception(deriveResult, "无法读取小K代码任务 AppContainer 身份。");
        return sid;
    }

    [SupportedOSPlatform("windows")]
    private static void GrantTreeAccess(string root, SecurityIdentifier sid, FileSystemRights rights)
    {
        var fullRoot = Path.GetFullPath(root);
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var path = pending.Pop();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("AppContainer ACL 拒绝经过重解析点的执行路径。");
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
            var rule = new FileSystemAccessRule(sid, rights, inheritance, PropagationFlags.None, AccessControlType.Allow);
            if (isDirectory)
            {
                var directory = new DirectoryInfo(path);
                var security = directory.GetAccessControl(AccessControlSections.Access);
                security.SetAccessRule(rule);
                directory.SetAccessControl(security);
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
            }
            else
            {
                var file = new FileInfo(path);
                var security = file.GetAccessControl(AccessControlSections.Access);
                security.SetAccessRule(rule);
                file.SetAccessControl(security);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ClearTreeAccess(string root, SecurityIdentifier sid)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot) && !File.Exists(fullRoot)) return;
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var path = pending.Pop();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("AppContainer 权限清理遇到重解析点；拒绝跟随该路径。");
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isDirectory)
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);

            if (isDirectory)
            {
                var directory = new DirectoryInfo(path);
                var security = directory.GetAccessControl(AccessControlSections.Access);
                security.PurgeAccessRules(sid);
                directory.SetAccessControl(security);
            }
            else
            {
                var file = new FileInfo(path);
                var security = file.GetAccessControl(AccessControlSections.Access);
                security.PurgeAccessRules(sid);
                file.SetAccessControl(security);
            }
        }
    }

    private static bool IsSameOrChild(string path, string parent)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static SECURITY_ATTRIBUTES CreateInheritableSecurityAttributes() => new()
    {
        Length = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
        InheritHandle = true,
        SecurityDescriptor = IntPtr.Zero
    };

    private static SafeJobHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw LastWin32("无法创建隔离验证进程作业。");
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "无法配置隔离验证进程回收作业。");
        }
        return job;
    }

    private static uint WaitForProcess(IntPtr process)
    {
        var result = WaitForSingleObject(process, Infinite);
        if (result == WaitFailed) throw LastWin32("等待 AppContainer 进程结束失败。");
        return result;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var output = new StringBuilder(Math.Min(MaximumCapturedCharactersPerStream, 2_048));
        var buffer = new char[2_048];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
            var remaining = MaximumCapturedCharactersPerStream - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        if (truncated) output.Append("\n[输出已截断]");
        return output.ToString();
    }

    private static async Task<string> CombineOutputAsync(Task<string> stdout, Task<string> stderr)
    {
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(error)) return output;
        return string.IsNullOrWhiteSpace(output) ? "标准错误：\n" + error : output + "\n标准错误：\n" + error;
    }

    private static IntPtr CreateEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var text = string.Join('\0', environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        return Marshal.StringToHGlobalUni(text);
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { QuoteWindowsArgument(executable) }.Concat(arguments.Select(QuoteWindowsArgument)));

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
        var result = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static Win32Exception LastWin32(string message) => new(Marshal.GetLastWin32Error(), message);

    private static void CheckHResult(int hresult, string message)
    {
        if (hresult < 0) throw Marshal.GetExceptionForHR(hresult) ?? new Win32Exception(hresult, message);
    }

    private static SafeFileHandle CreateFileW(string name, uint access, uint share, SECURITY_ATTRIBUTES security,
        uint creationDisposition, uint flags, IntPtr template) => CreateFileNative(name, access, share, ref security, creationDisposition, flags, template);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities { public IntPtr AppContainerSid; public IntPtr Capabilities; public uint CapabilityCount; public uint Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Length;
        public IntPtr Reserved2;
        public IntPtr StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CreateAppContainerProfile(string appContainerName, string displayName, string description,
        IntPtr capabilities, uint capabilityCount, out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string appContainerName, out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeleteAppContainerProfile(string appContainerName);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeriveCapabilitySidsFromName(string capabilityName, out IntPtr capabilityGroupSid, out IntPtr capabilitySid);

    [DllImport("advapi32.dll", SetLastError = true)] private static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, uint flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, uint attribute, IntPtr value,
        IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, uint informationClass,
        ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe,
        ref SECURITY_ATTRIBUTES pipeAttributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileNative(string fileName, uint desiredAccess, uint shareMode,
        ref SECURITY_ATTRIBUTES securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
