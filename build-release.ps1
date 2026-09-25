[CmdletBinding()]
param([string]$VersionOverride)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
$version = if ($VersionOverride) { $VersionOverride } else { [string]$props.Project.PropertyGroup.Version }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid SemVer: $version" }
$compiler = 'C:\Program Files (x86)\NSIS\makensis.exe'
if (-not (Test-Path $compiler)) { throw 'NSIS makensis.exe is required.' }
$stage = Join-Path $root "artifacts\stage\$version"
$release = Join-Path $root "artifacts\release\$version"
New-Item -ItemType Directory -Force $stage,$release | Out-Null
& (Join-Path $root 'scripts\generate-icon.ps1')
$originalAppData = $env:APPDATA
$env:APPDATA = Join-Path $stage 'build-profile'
New-Item -ItemType Directory -Force $env:APPDATA | Out-Null
$localPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
foreach ($project in @('Phase1\DualWAN-PoC.csproj','DualWAN.Dashboard\DualWAN.Dashboard.csproj')) {
    if (Test-Path $localPackages) {
        & dotnet restore (Join-Path $root $project) -r win-x64 --source $localPackages -p:NuGetAudit=false -p:Version=$version -v quiet
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $localPackages)) {
        & dotnet restore (Join-Path $root $project) -r win-x64 --configfile (Join-Path $root 'scripts\NuGet.Config') -p:Version=$version -v quiet
    }
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
}
& dotnet publish (Join-Path $root 'Phase1\DualWAN-PoC.csproj') -c Release -r win-x64 --self-contained true --no-restore -p:Version=$version -o (Join-Path $stage 'Service') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }
& dotnet publish (Join-Path $root 'DualWAN.Dashboard\DualWAN.Dashboard.csproj') -c Release -r win-x64 --self-contained true --no-restore -p:Version=$version -o (Join-Path $stage 'Dashboard') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Dashboard publish failed.' }
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'Dashboard\LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $root 'Phase1\WinDivert_LICENSE.txt') -Destination (Join-Path $stage 'Service\WinDivert_LICENSE.txt') -Force
$setup = Join-Path $release "DualWAN-Setup-$version.exe"
& $compiler "/DVERSION=$version" "/DSTAGE=$stage" "/DOUTPUT=$setup" (Join-Path $root 'installer\DualWAN.nsi') | Select-Object -Last 8
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $setup)) { throw 'Installer build failed.' }
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setup).Hash
"DualWAN-Setup-$version.exe`nSHA256: $hash" | Set-Content (Join-Path $release 'checksums.txt')
Write-Output $setup
$env:APPDATA = $originalAppData
