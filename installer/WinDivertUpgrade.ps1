[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall')][string]$Mode = 'Install',
    [string]$Source,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

function Get-ManagedServiceStatus([string]$Name) {
    try {
        $service = Get-Service -Name $Name -ErrorAction Stop
        return $service.Status.ToString()
    } catch [Microsoft.PowerShell.Commands.ServiceCommandException] {
        if ($_.FullyQualifiedErrorId -like 'NoServiceFoundForGivenName,*') { return $null }
        throw
    }
}

function Request-ManagedServiceStop([string]$Name) {
    try {
        Stop-Service -Name $Name -ErrorAction Stop
    } catch {
        # WinDivert may remove its own service entry as it stops (SCM 1060).
        if ($null -ne (Get-ManagedServiceStatus $Name) -and
            (Get-ManagedServiceStatus $Name) -ne 'Stopped') { throw }
    }
}

function Wait-ManagedServiceStopped([string]$Name, [int]$MaxPolls = 21, [int]$DelayMilliseconds = 500) {
    for ($poll = 0; $poll -lt $MaxPolls; $poll++) {
        $status = Get-ManagedServiceStatus $Name
        if ($null -eq $status -or $status -eq 'Stopped') { return $true }
        if ($poll -lt $MaxPolls - 1) { Start-Sleep -Milliseconds $DelayMilliseconds }
    }
    return $false
}

function Stop-And-Wait([string]$Name) {
    $status = Get-ManagedServiceStatus $Name
    if ($null -eq $status -or $status -eq 'Stopped') { return }
    Request-ManagedServiceStop $Name
    if (-not (Wait-ManagedServiceStopped $Name)) {
        throw "Timed out waiting for $Name to stop."
    }
}

function Copy-DriverFile([string]$From, [string]$To) {
    Copy-Item -LiteralPath $From -Destination $To -Force -ErrorAction Stop
}

function Copy-DriverWithRetry([string]$From, [string]$To, [int]$MaxAttempts = 3, [int]$DelayMilliseconds = 500) {
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Copy-DriverFile $From $To
            return
        } catch {
            if ($attempt -eq $MaxAttempts) { throw }
            Start-Sleep -Milliseconds ($DelayMilliseconds * $attempt)
        }
    }
}

function Invoke-WinDivertUpgrade([string]$Operation, [string]$From, [string]$To) {
    Stop-And-Wait 'DualWANService'
    Stop-And-Wait 'WinDivert'
    if ($Operation -eq 'Install') {
        if (-not $From -or -not $To) { throw 'Driver source and destination are required.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $To) -Force | Out-Null
        Copy-DriverWithRetry $From $To
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        Invoke-WinDivertUpgrade $Mode $Source $Destination
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 1
    }
}
