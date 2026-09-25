[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'DualWANService'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run uninstall-service.ps1 from an elevated PowerShell session.'
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "$serviceName is not installed."
    return
}
if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
sc.exe delete $serviceName | Out-Null
Write-Host "$serviceName removed. Configuration and logs remain in $env:ProgramData\DualWAN."
