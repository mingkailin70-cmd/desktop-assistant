function Write-XiaoKLongRunJsonAtomic {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [object] $InputObject
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $directory = [IO.Path]::GetDirectoryName($fullPath)
    [void][IO.Directory]::CreateDirectory($directory)
    $temporaryPath = Join-Path $directory ('.{0}.{1}.{2}.tmp' -f
        [IO.Path]::GetFileName($fullPath), $PID, [guid]::NewGuid().ToString('N'))
    $backupPath = "$temporaryPath.bak"

    try {
        $json = ConvertTo-Json -InputObject $InputObject -Depth 8
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($fullPath)) {
            [IO.File]::Replace($temporaryPath, $fullPath, $backupPath)
            if ([IO.File]::Exists($backupPath)) {
                [IO.File]::Delete($backupPath)
            }
        }
        else {
            [IO.File]::Move($temporaryPath, $fullPath)
        }
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
        if ([IO.File]::Exists($backupPath)) {
            [IO.File]::Delete($backupPath)
        }
    }
}

Export-ModuleMember -Function Write-XiaoKLongRunJsonAtomic
