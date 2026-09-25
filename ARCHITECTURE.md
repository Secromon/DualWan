# Architecture

DualWAN is a Windows Service and a WPF Dashboard. The Service owns routing and WAN health; the Dashboard configures policies and displays status over a local named pipe.

## Traffic path

```text
Application traffic
  → WinDivert
  → DualWAN Service transparent redirect
  → local TCP or UDP relay
  → outbound socket bound to the selected WAN
```

The routing path does not change global Windows route metrics. The Service uses WinDivert's driver and local relay sockets. It does not install a DualWAN kernel driver.

## Policy precedence

```text
Local/LAN IPv4 bypass
  > Individual Rule
  > active Group policy
  > Windows passthrough
```

The local IPv4 destination classifier uses a route snapshot to leave local traffic on Windows routing. An individual application Rule takes priority over a Group policy. If neither applies, traffic follows Windows routing. Applications presents the individual Rule controls; the dedicated Rules navigation is absent.

Rules and Groups are persisted in Service configuration. The Dashboard maintains its language, theme, and application catalogue in its user settings. No configuration schema is defined by this document; the source models are authoritative.

## WAN health and new flows

The Service tracks `CHECKING`, `UP`, `SUSPECT`, `DOWN`, and `RECOVERING` states. Routing decisions use the current health state. `STRICT` selects only its configured WAN; `FAILOVER` may select the other WAN when the primary is unavailable and the secondary is confirmed `UP`. Policy updates and failover decisions affect new flows. Established connections are not migrated.

## Dashboard and IPC

The Dashboard is a WPF client. It communicates with the Service through `\\.\pipe\DualWAN.Control`, API version `1`. Requests include `apiVersion`, `requestId`, and `command`; responses report success with data or an error. The current Service command switch includes `ping`, `getStatus`, `getWans`, `getRules`, `getGroups`, `getTelemetry`, `getTelemetryHistory`, `getTelemetryStorageStatus`, `setRule`, `upsertRule`, `deleteRule`, `upsertGroup`, `deleteGroup`, `applyPreset`, `setTelemetryStoragePolicy`, `cleanTelemetry`, and `reloadConfig`. The source in `Phase1/Services/ControlPipeServer.cs` defines request validation and authorization.

## Detection and telemetry

Assisted detection reads local application metadata. Network Activity Detection observes local process ownership of TCP connections and UDP sockets on explicit user request; its scan results are in memory. The Service records historical WAN telemetry in SQLite, including raw samples and aggregates. History displays both WANs' traffic and quality over the available ranges.

## Packaging

`build-release.ps1` publishes self-contained win-x64 Service and Dashboard output, then invokes NSIS. The installer installs/configures the Service, manages WinDivert during installation and upgrade, and can launch the Dashboard after Finish. WinDivert's license notice is in `Phase1/WinDivert_LICENSE.txt`.
