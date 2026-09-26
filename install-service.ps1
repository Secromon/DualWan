[CmdletBinding()]
param(
    [string]$InstallDirectory = "$env:ProgramFiles\DualWAN"
)

$ErrorActionPreference = 'Stop'
$serviceName = 'DualWANService'
$displayName = 'DualWAN Routing Service'
$description = 'Provides per-application routing across multiple WAN interfaces.'
$programData = Join-Path $env:ProgramData 'DualWAN'
$configPath = Join-Path $programData 'config.json'
$projectPath = Join-Path $PSScriptRoot 'Phase1\DualWAN-PoC.csproj'
$defaultConfig = Join-Path $PSScriptRoot 'Phase1\rules.json'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run install-service.ps1 from an elevated PowerShell session.'
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "$serviceName is already installed. Run uninstall-service.ps1 first."
}

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $programData 'logs') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $programData 'state') | Out-Null
# Preserve an existing machine-wide configuration. The bundled neutral file
# leaves WAN selection to the first-run Dashboard when no config exists.
if (-not (Test-Path -LiteralPath $configPath)) {
    Copy-Item -LiteralPath $defaultConfig -Destination $configPath
}

dotnet publish $projectPath -c Release -r win-x64 --self-contained false -o $InstallDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$serviceExe = Join-Path $InstallDirectory 'DualWAN.Service.exe'
New-Service -Name $serviceName -BinaryPathName ('"{0}"' -f $serviceExe) -DisplayName $displayName `
    -Description $description -StartupType Automatic
sc.exe config $serviceName start= delayed-auto obj= LocalSystem | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
sc.exe failureflag $serviceName 1 | Out-Null

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Get-Service -Name $serviceName | Select-Object Name,DisplayName,Status,StartType

