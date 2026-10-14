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

    try {
        $json = ConvertTo-Json -InputObject $InputObject -Depth 8
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($fullPath)) {
            [IO.File]::Replace($temporaryPath, $fullPath, $null)
        }
        else {
            [IO.File]::Move($temporaryPath, $fullPath)
        }
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

Export-ModuleMember -Function Write-XiaoKLongRunJsonAtomic
