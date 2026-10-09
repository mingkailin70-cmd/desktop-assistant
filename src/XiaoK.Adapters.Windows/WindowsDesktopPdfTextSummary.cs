using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public sealed partial class WindowsDesktopTools
{
    private static async Task<PdfSummaryReadResult> TryReadStablePdfTextAsync(SafeFileHandle handle,
        long expectedLength, long expectedWriteTime, CancellationToken cancellationToken)
    {
        if (expectedLength < 0 || expectedLength > LocalDocumentSummaryPolicy.MaximumPdfFileBytes)
            return new(false, string.Empty, "PDF_TOO_LARGE");

        try
        {
            using var stream = new FileStream(handle, FileAccess.Read, 32 * 1024, isAsync: false);
            using var output = new MemoryStream((int)expectedLength);
            var buffer = new byte[32 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var allowed = LocalDocumentSummaryPolicy.MaximumPdfFileBytes - (int)output.Length;
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, allowed + 1));
                if (read == 0) break;
                if (output.Length + read > LocalDocumentSummaryPolicy.MaximumPdfFileBytes)
                    return new(false, string.Empty, "PDF_TOO_LARGE");
                output.Write(buffer, 0, read);
            }

            if (output.Length != expectedLength
                || !TryGetFileSnapshot(stream.SafeFileHandle, out var lengthAfterRead, out var writeTimeAfterRead)
                || lengthAfterRead != expectedLength || writeTimeAfterRead != expectedWriteTime)
                return new(false, string.Empty, "PDF_UNSUPPORTED_OR_UNSTABLE");

            cancellationToken.ThrowIfCancellationRequested();
            return await RunPdfWorkerAsync(output.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException
            or Win32Exception or OverflowException or InvalidDataException)
        {
            return new(false, string.Empty, "PDF_UNSUPPORTED_OR_UNSTABLE");
        }
    }

    private static async Task<PdfSummaryReadResult> RunPdfWorkerAsync(byte[] pdfBytes,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        Task<byte[]>? outputTask = null;
        Task? errorTask = null;
        try
        {
            if (!OperatingSystem.IsWindows() || pdfBytes.Length > LocalDocumentSummaryPolicy.MaximumPdfFileBytes)
                return new(false, string.Empty, "PDF_WORKER_UNAVAILABLE");

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                return new(false, string.Empty, "PDF_WORKER_UNAVAILABLE");

            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--pdf-worker");
            startInfo.ArgumentList.Add(pdfBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start()) return new(false, string.Empty, "PDF_WORKER_UNAVAILABLE");

            using var job = CreateBoundedWorkerJob();
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                TryKill(process);
                return new(false, string.Empty, "PDF_WORKER_UNAVAILABLE");
            }

            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }

            using var timeout = new CancellationTokenSource(PdfWorkerTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            outputTask = ReadWorkerOutputAsync(process.StandardOutput.BaseStream,
                MaximumWorkerResponseBytes, cancellationToken);
            errorTask = DrainWorkerErrorAsync(process.StandardError.BaseStream);

            await process.StandardInput.BaseStream.WriteAsync(pdfBytes, linked.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            var response = await outputTask.WaitAsync(linked.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || !TryParseWorkerResponse(response, out var result))
                return new(false, string.Empty, "PDF_WORKER_FAILED");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (process is not null) TryKill(process);
            await ObserveWorkerOutputAsync(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (process is not null) TryKill(process);
            await ObserveWorkerOutputAsync(outputTask, errorTask).ConfigureAwait(false);
            return new(false, string.Empty, "PDF_WORKER_TIMEOUT");
        }
        catch (Win32Exception)
        {
            if (process is not null) TryKill(process);
            await ObserveWorkerOutputAsync(outputTask, errorTask).ConfigureAwait(false);
            return new(false, string.Empty, "PDF_WORKER_UNAVAILABLE");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SecurityException or ArgumentException or InvalidOperationException
            or InvalidDataException or OverflowException or ObjectDisposedException)
        {
            if (process is not null) TryKill(process);
            await ObserveWorkerOutputAsync(outputTask, errorTask).ConfigureAwait(false);
            return new(false, string.Empty, "PDF_WORKER_FAILED");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task<byte[]> ReadWorkerOutputAsync(Stream stream, int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("PDF worker response exceeded its fixed output limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static async Task DrainWorkerErrorAsync(Stream stream)
    {
        var buffer = new byte[8 * 1024];
        while (await stream.ReadAsync(buffer).ConfigureAwait(false) is > 0) { }
    }

    private static async Task ObserveWorkerOutputAsync(Task<byte[]>? outputTask, Task? errorTask)
    {
        try
        {
            if (outputTask is not null) await outputTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException
            or ObjectDisposedException or InvalidDataException) { }
        try
        {
            if (errorTask is not null) await errorTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException
            or ObjectDisposedException) { }
    }

    private static bool TryParseWorkerResponse(byte[] response, out PdfSummaryReadResult result)
    {
        result = new(false, string.Empty, "PDF_WORKER_FAILED");
        if (response.Length < WorkerResponseHeaderBytes) return false;
        var status = response[0];
        var textLength = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(1, sizeof(int)));
        if (textLength < 0 || textLength > MaximumWorkerTextBytes
            || response.Length != WorkerResponseHeaderBytes + textLength)
            return false;

        if (status != PdfWorkerSuccess)
        {
            var errorCode = status switch
            {
                PdfWorkerTooManyPages => "PDF_TOO_MANY_PAGES",
                PdfWorkerTextTooLarge => "PDF_TEXT_TOO_LARGE",
                _ => "PDF_UNSUPPORTED_OR_UNSTABLE"
            };
            result = new(false, string.Empty, errorCode);
            return textLength == 0;
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(response, WorkerResponseHeaderBytes, textLength);
            result = new(true, text, string.Empty);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static SafePdfWorkerJobHandle CreateBoundedWorkerJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitProcessMemory
            },
            ProcessMemoryLimit = new UIntPtr(PdfWorkerMaximumMemoryBytes)
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error);
        }
        return job;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
            or NotSupportedException or ObjectDisposedException) { }
    }

    private const byte PdfWorkerSuccess = 0;
    private const byte PdfWorkerFailure = 1;
    private const byte PdfWorkerTooManyPages = 2;
    private const byte PdfWorkerTextTooLarge = 3;
    private const int WorkerResponseHeaderBytes = 1 + sizeof(int);
    private const int MaximumWorkerTextBytes = LocalDocumentSummaryPolicy.MaximumPdfTextCharacters * 4;
    private const int MaximumWorkerResponseBytes = WorkerResponseHeaderBytes + MaximumWorkerTextBytes;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const long PdfWorkerMaximumMemoryBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan PdfWorkerTimeout = TimeSpan.FromSeconds(20);

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
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafePdfWorkerJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafePdfWorkerJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafePdfWorkerJobHandle job, int informationClass,
        ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafePdfWorkerJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private readonly record struct PdfSummaryReadResult(bool Success, string Text, string ErrorCode);
}

/// <summary>小K专用 PDF 子进程入口。仅接受父 Host 通过标准输入传入的一份有界文件。</summary>
public static class WindowsPdfTextWorkerEntryPoint
{
    private const byte Success = 0;
    private const byte Failure = 1;
    private const byte TooManyPages = 2;
    private const byte TextTooLarge = 3;
    private const int ResponseHeaderBytes = 1 + sizeof(int);

    public static int Run(string[] arguments)
    {
        if (arguments.Length != 2 || !string.Equals(arguments[0], "--pdf-worker", StringComparison.Ordinal)
            || !int.TryParse(arguments[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var length)
            || length < 0 || length > LocalDocumentSummaryPolicy.MaximumPdfFileBytes)
            return 2;

        try
        {
            var pdfBytes = new byte[length];
            using (var input = Console.OpenStandardInput()) input.ReadExactly(pdfBytes);
            var (status, text) = Extract(pdfBytes);
            var textBytes = Encoding.UTF8.GetBytes(text);
            using var output = Console.OpenStandardOutput();
            output.WriteByte(status);
            Span<byte> header = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, textBytes.Length);
            output.Write(header);
            output.Write(textBytes);
            output.Flush();
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or InvalidOperationException
            or System.Security.SecurityException or OverflowException)
        {
            try
            {
                using var output = Console.OpenStandardOutput();
                output.WriteByte(Failure);
                var header = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, 0);
                output.Write(header);
                output.Flush();
                return 0;
            }
            catch (IOException) { return 3; }
        }
    }

    private static (byte Status, string Text) Extract(byte[] pdfBytes)
    {
        try
        {
            using var input = new MemoryStream(pdfBytes, writable: false);
            using var document = PdfDocument.Open(input, new ParsingOptions
            {
                UseLenientParsing = false,
                SkipMissingFonts = true,
                MaxStackDepth = LocalDocumentSummaryPolicy.MaximumPdfParserStackDepth,
                Password = string.Empty
            });

            if (document.NumberOfPages > LocalDocumentSummaryPolicy.MaximumPdfPages)
                return (TooManyPages, string.Empty);

            var builder = new StringBuilder(Math.Min(pdfBytes.Length,
                LocalDocumentSummaryPolicy.MaximumPdfTextCharacters));
            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                var pageText = ContentOrderTextExtractor.GetText(document.GetPage(pageNumber));
                var separatorLength = builder.Length == 0 ? 0 : 2;
                if (pageText.Length > LocalDocumentSummaryPolicy.MaximumPdfTextCharacters
                    - builder.Length - separatorLength)
                    return (TextTooLarge, string.Empty);

                if (separatorLength > 0) builder.Append('\n').Append('\n');
                builder.Append(pageText);
            }
            return (Success, builder.ToString());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return (Failure, string.Empty);
        }
    }
}
