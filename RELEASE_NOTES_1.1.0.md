# DualWAN 1.1.0

Feature release after 1.0.0. Developed by GDM under GPLv3.

## Highlights

- Redesigned Groups editor and atomic Group rename.
- Per-application statistics for traffic routed through DualWAN.
- Stronger fail-open handling if the WinDivert packet-processing engine fails fatally.

## Groups

The two-pane Groups interface adds search, an explicit editor, a multi-application membership picker and an individual-override indicator. Group rename is atomic. Light/Dark surface consistency has improved. Routing precedence is unchanged: local destination bypass, individual Rule, active Group, then Windows passthrough.

## Application Statistics

The Statistics page shows top applications, upload/download totals, actual WAN1/WAN2 breakdown, TCP connection and UDP session counts, and a traffic chart for 1 hour, 24 hours, 7 days or 30 days. Retention defaults to 1 hour and can be set to 24 hours, 7 days or 30 days. Data is stored as minute aggregates in the local SQLite telemetry database. It contains application identity, WAN, time bucket, byte counts and TCP/UDP activity counts, with no destination history or packet contents.

Statistics cover **only IPv4 TCP/UDP payload traffic routed through DualWAN relays**. They exclude Windows passthrough, LAN bypass, IPv6 and other non-relay traffic. These figures are not total application bandwidth usage; the current incomplete minute may be absent.

## Reliability

On a fatal packet-processing failure, DualWAN releases WinDivert interception and marks routing inactive so new traffic can return toward native Windows handling. Existing connections may still break; no session migration or zero-loss guarantee is implied.

## Documentation

The new [LAN isolation research](docs/LAN_ISOLATION_RESEARCH.md) records attribution experiments for future contributors. Per-application LAN blocking is **not implemented**.

## Known Limitations

DualWAN does not bond WAN bandwidth or implement MPTCP. Failover decisions affect new flows, not established sessions. IPv6 and shared-process attribution remain limited. See [Known limitations](KNOWN_LIMITATIONS.md).

## Upgrade Notes

The installer preserves existing WAN assignments, friendly names, individual Rules, Groups, application catalogue, Dashboard preferences and the telemetry database. Application-stat tables are created additively on startup; their default retention is one hour. Statistics initialization failures do not disable routing. The installer and binaries are unsigned, so Windows may show an untrusted-publisher prompt.
