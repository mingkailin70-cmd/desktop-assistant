using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class WindowsChildProcessSupervisor
{
    private static readonly TimeSpan ReapTimeout = TimeSpan.FromSeconds(5);

    public static async Task<bool> WaitForStartSignalAsync()
    {
        try
        {
            var signal = await Console.In.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            return string.Equals(signal, "GO", StringComparison.Ordinal);
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static async Task<ChildProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The evaluation worker requires Windows x64.");
        if (arguments.Count == 0 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var startInfo = CreateStartInfo(arguments);
        using var process = new Process { StartInfo = startInfo };
        using var outputDrains = new OutputDrains();
        var clock = Stopwatch.StartNew();
        var startedAtUtc = DateTimeOffset.UtcNow;
        int? processId = null;
        int? exitCode = null;
        DateTimeOffset? processStartedAtUtc = null;
        var timedOut = false;
        var jobAssigned = false;
        var processReaped = false;
        string? startErrorCode = null;
        WindowsKillOnCloseJob? job = null;

        try
        {
            job = WindowsKillOnCloseJob.Create();
            if (!process.Start())
            {
                startErrorCode = "WORKER_START_FAILED";
            }
            else
            {
                processId = process.Id;
                processStartedAtUtc = process.StartTime.ToUniversalTime();
                outputDrains.Start(process);
                try
                {
                    job.Assign(process);
                    jobAssigned = true;
                    await process.StandardInput.WriteLineAsync("GO").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
                {
                    startErrorCode = jobAssigned ? "WORKER_START_SIGNAL_FAILED" : "WORKER_JOB_ASSIGNMENT_FAILED";
                }

                if (startErrorCode is null)
                {
                    using var deadline = new CancellationTokenSource(timeout);
                    try
                    {
                        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        timedOut = !process.HasExited;
                    }
                }
            }
        }
        catch (Win32Exception exception)
        {
            startErrorCode ??= $"WORKER_WIN32_{exception.NativeErrorCode}";
        }
        catch (IOException)
        {
            startErrorCode ??= "WORKER_PIPE_FAILED";
        }
        catch (InvalidOperationException)
        {
            startErrorCode ??= "WORKER_PROCESS_STATE_INVALID";
        }
        finally
        {
            if (processId is not null)
            {
                try
                {
                    process.Refresh();
                    if (!process.HasExited)
                    {
                        await StopAndReapAsync(process, job).ConfigureAwait(false);
                        process.Refresh();
                    }
                    processReaped = process.HasExited;
                    exitCode = processReaped ? process.ExitCode : null;
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException)
                {
                    processReaped = false;
                    startErrorCode ??= $"WORKER_REAP_{exception.GetType().Name.ToUpperInvariant()}";
                }
            }
            else processReaped = true;
            job?.Dispose();
            try { await outputDrains.FinishAsync().ConfigureAwait(false); }
            catch (Exception) { }
        }

        if (processId is not null && !processReaped)
            startErrorCode ??= "WORKER_REAP_FAILED";
        return new ChildProcessResult(processId, exitCode, timedOut, jobAssigned, processReaped,
            clock.Elapsed.TotalMilliseconds, startedAtUtc, startErrorCode, processStartedAtUtc);
    }

    public static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("The current executable path is unavailable.");

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssemblyPath) || !File.Exists(entryAssemblyPath))
                throw new InvalidOperationException("The diagnostics assembly path is unavailable.");
            startInfo.ArgumentList.Add(entryAssemblyPath);
        }
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        foreach (var name in new[] { "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE" })
            startInfo.Environment.Remove(name);
        return startInfo;
    }

    private static async Task StopAndReapAsync(Process process, WindowsKillOnCloseJob? job)
    {
        job?.Dispose();
        try
        {
            process.Refresh();
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }

        try
        {
            await process.WaitForExitAsync().WaitAsync(ReapTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        catch (InvalidOperationException) { }
    }

    private sealed class OutputDrains : IDisposable
    {
        private Task? _stdout;
        private Task? _stderr;

        public void Start(Process process)
        {
            _stdout = DrainAndDiscardAsync(process.StandardOutput);
            _stderr = DrainAndDiscardAsync(process.StandardError);
        }

        public async Task FinishAsync()
        {
            var drains = new[] { _stdout, _stderr }.Where(task => task is not null).Cast<Task>().ToArray();
            if (drains.Length != 0)
                await Task.WhenAll(drains).WaitAsync(ReapTimeout).ConfigureAwait(false);
        }

        public void Dispose()
        {
            // The caller owns the process and closes it before awaiting these drains.
        }

        private static async Task DrainAndDiscardAsync(StreamReader reader)
        {
            var buffer = new char[1024];
            while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0) { }
        }
    }

    internal sealed record ChildProcessResult(int? ProcessId, int? ExitCode, bool TimedOut,
        bool JobAssigned, bool ProcessReaped, double ElapsedMilliseconds, DateTimeOffset StartedAtUtc,
        string? ErrorCode, DateTimeOffset? ProcessStartedAtUtc = null);

    private sealed class WindowsKillOnCloseJob : IDisposable
    {
        private const uint KillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private IntPtr _handle;

        private WindowsKillOnCloseJob(IntPtr handle) => _handle = handle;

        public static WindowsKillOnCloseJob Create()
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the evaluation worker job.");

            var information = new JobObjectExtendedLimitInformation();
            information.BasicLimitInformation.LimitFlags = KillOnJobClose;
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, ref information,
                    checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
            {
                var error = Marshal.GetLastWin32Error();
                _ = CloseHandle(handle);
                throw new Win32Exception(error, "Could not configure kill-on-close for the evaluation worker.");
            }
            return new WindowsKillOnCloseJob(handle);
        }

        public void Assign(Process process)
        {
            if (_handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(WindowsKillOnCloseJob));
            if (!AssignProcessToJobObject(_handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the evaluation worker to its job.");
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
                _ = CloseHandle(handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass,
            ref JobObjectExtendedLimitInformation information, uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
