# Changelog

## 1.1.0 — 2026

### Added

- Per-application statistics for DualWAN-routed IPv4 TCP/UDP payload traffic: top applications, upload/download, recorded WAN breakdown, TCP/UDP counts, bounded chart periods, and configurable retention (1 hour by default).
- Additive minute-bucket SQLite persistence and bounded local IPC statistics queries.

### Changed

- Two-pane Groups interface with search, explicit editor, multi-application picker, individual-override indicator, and atomic rename.
- More consistent Groups surfaces in Light and Dark themes.

### Fixed

- Fatal WinDivert packet-loop failures release interception and mark routing inactive, allowing new traffic to return toward native Windows handling. Existing flows may still break.

### Documentation

- Added LAN isolation attribution research; per-application LAN blocking remains unimplemented.

## 1.0.0 — 2026

First public release. Developed by GDM under the GNU General Public License v3.0.

- **Routing:** Per-application WAN1/WAN2 selection with Prefer and Only policies for TCP and UDP flows.
- **Applications and Groups:** Individual policies, application catalogue, optional Group membership, and individual-rule precedence.
- **WAN failover and LAN:** WAN health monitoring, failover for new flows, and local IPv4 destination bypass.
- **Application detection:** Local assisted detection and user-started Network Activity Detection.
- **History:** WAN download, upload, latency, and packet-loss views with SQLite-backed history.
- **Interface:** Light, Dark, and System themes; Italian and English; local Help and contextual tooltips.
- **Installer:** Self-contained win-x64 Dashboard and Service, WinDivert management, and upgrade support.
- **Documentation:** README, architecture, build instructions, known limitations, and GPLv3 license.
