[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [switch]$Install
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$package = Get-Item -LiteralPath $PackagePath -ErrorAction Stop
$packagePath = [System.IO.Path]::GetFullPath($package.FullName)
$allowedRoots = @(
    [System.IO.Path]::GetFullPath((Join-Path $artifactRoot 'msix-validation')),
    [System.IO.Path]::GetFullPath((Join-Path $artifactRoot 'packages'))
)
$packageAllowed = $false
foreach ($allowedRoot in $allowedRoots) {
    $prefix = $allowedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($packagePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $packageAllowed = $true
        break
    }
}
if (-not $packageAllowed -or (($package.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw 'PackagePath must be a regular file under the repository artifacts\msix-validation or artifacts\packages directory.'
}

$signingDirectory = Join-Path $artifactRoot 'signing'
$metadataPath = Join-Path $signingDirectory 'xiaok-development-certificate.json'
$publicCertificatePath = Join-Path $signingDirectory 'xiaok-development-certificate.cer'
if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf) -or -not (Test-Path -LiteralPath $publicCertificatePath -PathType Leaf)) {
    throw 'Local development certificate metadata or public certificate is missing. Run tools\new_xiaok_dev_certificate.ps1 first.'
}
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$certificate = New-Object -TypeName System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $publicCertificatePath
$expectedThumbprint = ([string]$metadata.thumbprint -replace '\s', '').ToUpperInvariant()
if ($expectedThumbprint -notmatch '^[0-9A-F]{40}$' -or $certificate.Thumbprint -cne $expectedThumbprint) {
    throw 'The public certificate does not match the locally recorded development certificate thumbprint.'
}
if ($certificate.Subject -cne 'CN=XiaoK Local Development') {
    throw "Unexpected development certificate subject: $($certificate.Subject)"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $manifestEntry) { throw 'The MSIX package does not contain AppxManifest.xml.' }
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { $packageManifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
} finally {
    $archive.Dispose()
}
$packageIdentity = $packageManifest.Package.Identity
$manifestPublisher = [string]$packageIdentity.Publisher
$manifestName = [string]$packageIdentity.Name
if ($manifestPublisher -cne $certificate.Subject -or $manifestName -cne 'MingKaiLin.XiaoK') {
    throw 'The package identity does not match the expected XiaoK development publisher and package name.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $packagePath
if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -cne $certificate.Thumbprint) {
    throw 'The package signer is missing or does not match the pinned local public certificate.'
}
if ($signature.Status.ToString() -notin @('Valid', 'UnknownError')) {
    throw "Package signature status is $($signature.Status): $($signature.StatusMessage)"
}

$packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
$certificateHash = (Get-FileHash -LiteralPath $publicCertificatePath -Algorithm SHA256).Hash
$trustedCertificate = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -ceq $certificate.Thumbprint } | Select-Object -First 1

Write-Output "Package: $packagePath"
Write-Output "Package SHA-256: $packageHash"
Write-Output "Publisher: $manifestPublisher"
Write-Output "Signer subject: $($certificate.Subject)"
Write-Output "Signer SHA-1 thumbprint: $($certificate.Thumbprint)"
Write-Output "Public certificate SHA-256: $certificateHash"
Write-Output "Signature status before trust: $($signature.Status) ($($signature.StatusMessage))"
Write-Output "Trusted People store: $(if ($null -eq $trustedCertificate) { 'certificate not present' } else { 'certificate already present' })"

if (-not $Install) {
    Write-Output 'Preflight only. No certificate trust, installation, or app launch was changed.'
    return
}

$previousPackage = Get-AppxPackage -Name $manifestName |
    Where-Object { $_.Publisher -ceq $manifestPublisher } |
    Sort-Object { [version]$_.Version } -Descending |
    Select-Object -First 1
if ($null -ne $previousPackage) {
    $previousHostPath = [System.IO.Path]::GetFullPath((Join-Path $previousPackage.InstallLocation 'XiaoK.Host.exe'))
    $matchingHostProcesses = @()
    foreach ($hostProcess in @(Get-Process -Name 'XiaoK.Host' -ErrorAction SilentlyContinue)) {
        $processPath = $null
        try { $processPath = $hostProcess.Path } catch { }
        if ([string]::IsNullOrWhiteSpace($processPath)) {
            throw "Cannot verify XiaoK.Host process $($hostProcess.Id); no certificate or package changes were made. Close XiaoK normally and retry."
        }

        $processPath = [System.IO.Path]::GetFullPath($processPath)
        if ([string]::Equals($processPath, $previousHostPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            $matchingHostProcesses += $hostProcess
        }
    }

    if ($matchingHostProcesses.Count -gt 0) {
        if ([version]$previousPackage.Version -lt [version]'0.1.7.0') {
            $runningIds = ($matchingHostProcesses | ForEach-Object { $_.Id }) -join ', '
            throw "Installed XiaoK $($previousPackage.Version) does not support the verified graceful-shutdown request (PID $runningIds). Right-click the XiaoK pet or tray icon and choose '退出小K', then retry. No process was terminated; no certificate or package changes were made."
        }

        if (-not ('XiaoK.MsixInstaller.ShutdownWindowMessage' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace XiaoK.MsixInstaller
{
    public static class ShutdownWindowMessage
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string messageName);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        public static bool TryRequestShutdown(uint expectedProcessId, string expectedWindowTitle, out bool windowFound,
            out uint windowProcessId, out int lastError)
        {
            windowFound = false;
            windowProcessId = 0;
            lastError = 0;

            uint message = RegisterWindowMessage("XiaoK.DesktopAssistant.Shutdown.v1");
            if (message == 0)
            {
                lastError = Marshal.GetLastWin32Error();
                return false;
            }

            IntPtr window = IntPtr.Zero;
            int matchingWindows = 0;
            EnumWindowsProc callback = (candidate, _) =>
            {
                uint processId;
                GetWindowThreadProcessId(candidate, out processId);
                if (processId != expectedProcessId) return true;

                var title = new StringBuilder(256);
                GetWindowText(candidate, title, title.Capacity);
                if (!string.Equals(title.ToString(), expectedWindowTitle, StringComparison.Ordinal)) return true;

                matchingWindows++;
                window = candidate;
                return true;
            };

            if (!EnumWindows(callback, IntPtr.Zero))
            {
                lastError = Marshal.GetLastWin32Error();
                return false;
            }

            if (matchingWindows == 0)
            {
                lastError = 1168; // ERROR_NOT_FOUND
                return false;
            }

            windowFound = true;
            windowProcessId = expectedProcessId;
            if (matchingWindows != 1)
            {
                lastError = 183; // ERROR_ALREADY_EXISTS: ambiguous matching windows
                return false;
            }
            if (PostMessage(window, message, IntPtr.Zero, IntPtr.Zero)) return true;

            lastError = Marshal.GetLastWin32Error();
            return false;
        }
    }
}
'@
        }

        $shutdownRequested = $false
        $windowProcessId = [uint32]0
        $shutdownError = 0
        foreach ($hostProcess in $matchingHostProcesses) {
            $windowFound = $false
            $candidateWindowProcessId = [uint32]0
            $candidateError = 0
            $posted = [XiaoK.MsixInstaller.ShutdownWindowMessage]::TryRequestShutdown(
                [uint32]$hostProcess.Id,
                '小K',
                [ref]$windowFound,
                [ref]$candidateWindowProcessId,
                [ref]$candidateError)
            if ($posted) {
                $shutdownRequested = $true
                $windowProcessId = $candidateWindowProcessId
                break
            }
            if ($windowFound -and $matchingHostProcesses.Id -notcontains [int]$candidateWindowProcessId) {
                throw "The XiaoK main window belongs to unexpected PID $candidateWindowProcessId; no certificate or package changes were made."
            }
            if ($candidateError -ne 0) { $shutdownError = $candidateError }
        }

        if (-not $shutdownRequested) {
            throw "Could not send a verified graceful shutdown request to the installed XiaoK window (Win32 error $shutdownError). No certificate or package changes were made."
        }

        Write-Output "Requested graceful shutdown from installed XiaoK PID $windowProcessId; waiting up to 45 seconds for cleanup."
        $shutdownDeadline = [DateTime]::UtcNow.AddSeconds(45)
        do {
            $runningHostProcesses = @()
            foreach ($hostProcess in $matchingHostProcesses) {
                try {
                    $hostProcess.Refresh()
                    if (-not $hostProcess.HasExited) { $runningHostProcesses += $hostProcess }
                } catch [System.InvalidOperationException] { }
            }
            if ($runningHostProcesses.Count -eq 0) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $shutdownDeadline)

        if ($runningHostProcesses.Count -gt 0) {
            $runningIds = ($runningHostProcesses | ForEach-Object { $_.Id }) -join ', '
            throw "XiaoK did not complete graceful shutdown within 45 seconds (PID $runningIds). No process was force-terminated; no certificate or package changes were made."
        }
        Write-Output 'XiaoK exited normally; continuing signed package verification and current-user installation.'
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object -TypeName Security.Principal.WindowsPrincipal -ArgumentList $identity
if ($null -eq $trustedCertificate -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The development certificate is not in LocalMachine\TrustedPeople. Run an elevated PowerShell once to add machine-level trust, or have an administrator import the public certificate.'
}

$addedTrustedCertificate = $false
if ($null -eq $trustedCertificate) {
    Import-Certificate -FilePath $publicCertificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    $addedTrustedCertificate = $true
}

$signToolPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
if (-not (Test-Path -LiteralPath $signToolPath -PathType Leaf)) {
    if ($addedTrustedCertificate) { Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" }
    throw "SignTool.exe not found: $signToolPath"
}
& $signToolPath verify /pa /v $packagePath
if ($LASTEXITCODE -ne 0) {
    if ($addedTrustedCertificate) { Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" }
    throw "Signature verification failed with exit code $LASTEXITCODE. A newly added trust entry has been removed."
}

try {
    Add-AppxPackage -Path $packagePath
} catch {
    if ($addedTrustedCertificate) {
        Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue
    }
    throw
}

$installedPackage = Get-AppxPackage -Name $manifestName | Where-Object { $_.Publisher -ceq $manifestPublisher } | Select-Object -First 1
if ($null -eq $installedPackage) {
    throw 'Add-AppxPackage returned without an error, but the XiaoK package could not be found for the current user. Inspect Windows deployment logs before retrying.'
}
Write-Output "Installed for current user: $($installedPackage.PackageFullName)"
$trustResult = if ($addedTrustedCertificate) { 'The certificate was added to LocalMachine\TrustedPeople.' } else { 'The certificate was already trusted in LocalMachine\TrustedPeople.' }
Write-Output "The app was not launched. $trustResult See the rollback commands in docs\开发与发布\MSIX打包说明.md."
