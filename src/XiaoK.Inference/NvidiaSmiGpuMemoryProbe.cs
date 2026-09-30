using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace XiaoK.Inference;

/// <summary>Reads free/total memory for NVIDIA GPU 0 through the driver-provided nvidia-smi utility.</summary>
public sealed class NvidiaSmiGpuMemoryProbe : IGpuMemoryProbe
{
    private const int QueryTimeoutMilliseconds = 3000;

    public async Task<GpuMemorySnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var executable = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(executable)) return null;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--id=0");
        process.StartInfo.ArgumentList.Add("--query-gpu=memory.free,memory.total");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");

        try
        {
            if (!process.Start()) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(QueryTimeoutMilliseconds);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await TerminateAndWaitAsync(process).ConfigureAwait(false);
                return null;
            }

            if (process.ExitCode != 0) return null;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (line is null) return null;

            var values = line.Split(',');
            if (values.Length < 2
                || !long.TryParse(values[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var freeMiB)
                || !long.TryParse(values[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalMiB)
                || freeMiB <= 0 || totalMiB <= 0 || freeMiB > totalMiB)
                return null;

            return new GpuMemorySnapshot(freeMiB, totalMiB);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateAndWaitAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await TerminateAndWaitAsync(process).ConfigureAwait(false);
            return null;
        }
    }

    private static async Task TerminateAndWaitAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (TimeoutException) { }
    }
}
