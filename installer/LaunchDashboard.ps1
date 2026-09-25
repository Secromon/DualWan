param(
    [Parameter(Mandatory = $true)]
    [string]$DashboardPath
)

$ErrorActionPreference = 'Stop'

try {
    if (-not [System.IO.Path]::IsPathRooted($DashboardPath) -or
        -not (Test-Path -LiteralPath $DashboardPath -PathType Leaf)) {
        throw 'Dashboard executable not found.'
    }

    $shell = New-Object -ComObject Shell.Application
    $shell.ShellExecute($DashboardPath, '',
        [System.IO.Path]::GetDirectoryName($DashboardPath), 'open', 1)
    exit 0
}
catch {
    Write-Output $_.Exception.Message
    exit 1
}
