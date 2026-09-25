# Building DualWAN

Run these commands from the repository root on Windows.

## Tools

- .NET 9 SDK, which builds the `net9.0` Service and `net9.0-windows` WPF Dashboard.
- PowerShell for `build-release.ps1`.
- NSIS with `makensis.exe` at `C:\Program Files (x86)\NSIS\makensis.exe` for the installer script as currently written.
- The repository's WinDivert DLL and driver files in `Phase1`.

Restore may need access to the package sources specified in `scripts/NuGet.Config`. The release script first tries the local NuGet package cache.

## Build Service and Dashboard

```powershell
dotnet restore .\Phase1\DualWAN-PoC.csproj --configfile .\scripts\NuGet.Config
dotnet restore .\DualWAN.Dashboard\DualWAN.Dashboard.csproj --configfile .\scripts\NuGet.Config
dotnet build .\Phase1\DualWAN-PoC.csproj -c Release --no-restore
dotnet build .\DualWAN.Dashboard\DualWAN.Dashboard.csproj -c Release --no-restore
```

The Service assembly is named `DualWAN.Service`; the Dashboard assembly is `DualWAN.Dashboard`. The version comes from `Directory.Build.props`.

## Publish self-contained win-x64 output

The release script performs a `win-x64` restore first. For a direct publish after restore:

```powershell
dotnet restore .\Phase1\DualWAN-PoC.csproj -r win-x64 --configfile .\scripts\NuGet.Config
dotnet restore .\DualWAN.Dashboard\DualWAN.Dashboard.csproj -r win-x64 --configfile .\scripts\NuGet.Config
dotnet publish .\Phase1\DualWAN-PoC.csproj -c Release -r win-x64 --self-contained true --no-restore -o .\artifacts\stage\manual\Service
dotnet publish .\DualWAN.Dashboard\DualWAN.Dashboard.csproj -c Release -r win-x64 --self-contained true --no-restore -o .\artifacts\stage\manual\Dashboard
```

The distributed self-contained build does not require a separate .NET runtime on the target PC.

## Build the installer

```powershell
.\build-release.ps1
```

The script reads `Directory.Build.props`, generates the application icon, restores and publishes both projects, and calls NSIS with `installer/DualWAN.nsi`. It stages files in `artifacts/stage/<version>/` and writes `artifacts/release/<version>/DualWAN-Setup-<version>.exe` plus `checksums.txt`. An optional `-VersionOverride` parameter exists in the script; normal builds use the centralized version.

The installer manages the Service and WinDivert driver during install/upgrade. Its Finish page can launch the Dashboard. See the installer source for implementation details.

The installer does not install a machine-specific WAN configuration. The Service owns `%ProgramData%\DualWAN\config.json`; Settings saves explicit WAN selections there through the local Service API. A fresh Service starts inactive until both interfaces are selected. `Phase1/rules.json` is a neutral template used by `install-service.ps1`, which preserves an existing configuration.
