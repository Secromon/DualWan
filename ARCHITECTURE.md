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

The redirector treats a fatal packet-loop or send/receive failure as a routing failure: it releases the WinDivert interception handle and marks routing inactive. New traffic can then return to native Windows routing. This does not preserve every in-flight packet or migrate established connections.

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

The Dashboard is a WPF client. It communicates with the Service through `\\.\pipe\DualWAN.Control`, API version `1`. Requests include `apiVersion`, `requestId`, and `command`; responses report success with data or an error. The Service also exposes bounded `getAppStatisticsSummary`, `getAppStatisticsDetail`, and `getAppStatsRetention` reads plus the admin-authorized `setAppStatsRetention` mutation. The source in `Phase1/Services/ControlPipeServer.cs` defines the complete command set, validation, and authorization.

## Detection and telemetry

Assisted detection reads local application metadata. Network Activity Detection observes local process ownership of TCP connections and UDP sockets on explicit user request; its scan results are in memory. The Service records historical WAN telemetry in SQLite, including raw samples and aggregates. History displays both WANs' traffic and quality over the available ranges.

`ApplicationTrafficAccumulator` counts IPv4 TCP/UDP payload bytes and relay activity attributed to each routed application and actual WAN. `ApplicationTrafficPersistence` writes completed one-minute aggregate buckets to the existing SQLite telemetry database, with a separate retention of 1 hour by default (24 hours, 7 days, or 30 days optionally). Query APIs aggregate by application, WAN and bounded time bins; the Dashboard Statistics page renders those results. Current partial minutes can be absent. The app-stat storage path is best-effort and does not govern routing readiness. No destination, DNS or packet-content history is persisted by this feature.

Per-application LAN isolation is research only and is not implemented; see [LAN isolation research](docs/LAN_ISOLATION_RESEARCH.md).

## Packaging

`build-release.ps1` publishes self-contained win-x64 Service and Dashboard output, then invokes NSIS. The installer installs/configures the Service, manages WinDivert during installation and upgrade, and can launch the Dashboard after Finish. WinDivert's license notice is in `Phase1/WinDivert_LICENSE.txt`.
