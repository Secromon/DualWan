# DualWAN 1.0.0

First public release. Developed by GDM.

## Highlights

- Per-application WAN1/WAN2 routing with Prefer and Only choices, plus automatic failover for new flows.
- Optional Groups and local assisted application detection, including user-started Network Activity Detection.
- IPv4 LAN bypass and WAN traffic/quality History.
- Light, Dark, and System themes; Italian and English interface; local Help and contextual tooltips.
- Windows installer for the Dashboard, background Service, and WinDivert component.

## Requirements

64-bit Windows, administrator rights for installation, and two usable WAN interfaces for dual-WAN operation. The distributed build is self-contained and does not require a separate .NET runtime.

## Important limitations

DualWAN does not bond bandwidth, implement MPTCP, or migrate active sessions. Failover applies to new flows. IPv6 behavior is not fully validated, and attribution of shared/system processes can be ambiguous. Binaries and installer are unsigned, so Windows may show an untrusted-publisher warning. See [Known limitations](KNOWN_LIMITATIONS.md).

## Installation

Run `DualWAN-Setup-1.0.0.exe` as administrator and complete the installer. It installs the Service required for routing; the Dashboard can be launched after Finish or from the Start menu. See [README](README.md) for basic usage.

## License

Copyright © 2026 GDM. DualWAN is released under the GNU General Public License v3.0. See [LICENSE](LICENSE). Third-party notices remain in the repository for [WinDivert](Phase1/WinDivert_LICENSE.txt) and the [WireShift-derived diagnostic proxy](SocksProxy/WIREShift_LICENSE.txt).
