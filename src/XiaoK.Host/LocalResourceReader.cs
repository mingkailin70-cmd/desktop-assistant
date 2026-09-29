using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace XiaoK.Host;

internal static class LocalResourceReader
{
    private const long BytesPerMiB = 1024 * 1024;

    public static string GetHostProcessUsage(TimeSpan previousCpuTime, long previousSampleTimestamp)
    {
        using var process = Process.GetCurrentProcess();
        var cpuTime = process.TotalProcessorTime;
        var workingSetMiB = process.WorkingSet64 / BytesPerMiB;
        var elapsedMilliseconds = previousSampleTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(previousSampleTimestamp).TotalMilliseconds;
        var cpuMilliseconds = (cpuTime - previousCpuTime).TotalMilliseconds;
        var cpuPercent = elapsedMilliseconds <= 0
            ? (double?)null
            : Math.Clamp(cpuMilliseconds / elapsedMilliseconds / Environment.ProcessorCount * 100, 0, 100);
        var cpuText = cpuPercent is null ? "待下一次采样" : $"{cpuPercent.Value:F1}%（整机总算力比例）";
        return $"小K进程：CPU {cpuText}，RAM {workingSetMiB:N0} MiB";
    }

    public static Task<string> GetNvidiaMemoryUsageAsync() => Task.Run(ReadNvidiaMemoryUsage);

    private static string ReadNvidiaMemoryUsage()
    {
        var executable = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(executable)) return "NVIDIA 显存：未找到系统 nvidia-smi，无法读取。";

        try
        {
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
            process.StartInfo.ArgumentList.Add("--query-gpu=memory.used,memory.total");
            process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");

            if (!process.Start()) return "NVIDIA 显存：nvidia-smi 未能启动。";
            if (!process.WaitForExit(milliseconds: 3000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return "NVIDIA 显存：查询超时。";
            }

            var output = process.StandardOutput.ReadToEnd();
            if (process.ExitCode != 0) return "NVIDIA 显存：驱动工具未返回数据。";

            long usedMiB = 0;
            long totalMiB = 0;
            var gpuCount = 0;
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var values = line.Split(',');
                if (values.Length < 2
                    || !long.TryParse(values[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var used)
                    || !long.TryParse(values[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
                    continue;

                usedMiB += used;
                totalMiB += total;
                gpuCount++;
            }

            if (gpuCount == 0 || totalMiB <= 0) return "NVIDIA 显存：没有可用的 NVIDIA GPU 读数。";
            return $"NVIDIA 显卡系统总占用：{usedMiB:N0} / {totalMiB:N0} MiB（{gpuCount} 个 GPU）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"NVIDIA 显存：读取失败（{ex.GetType().Name}）。";
        }
    }
}
