# DualWAN

DualWAN is a Windows application that lets different programs on the same PC use different Internet connections. It can assign applications to one of two WAN connections, fall back to the other connection for new flows when the preferred WAN is unavailable, and leave local IPv4 traffic on normal Windows routing.

DualWAN 1.1.0 is the current public feature release. Developed by GDM.

## What DualWAN does

- Routes individual applications through WAN1 or WAN2.
- Prefers one WAN with automatic fallback, or restricts an application to one WAN.
- Applies shared policies through optional Groups.
- Offers a two-pane Groups editor with search, membership selection, individual-override indicators, and atomic rename.
- Leaves applications without a DualWAN policy on normal Windows routing.
- Preserves access to local IPv4 resources such as NAS devices and printers.
- Monitors WAN health and shows traffic and quality history.
- Shows per-application statistics for traffic routed through DualWAN, with recorded WAN breakdown and configurable short history.
- Assists with finding applications installed or active on this PC.

## What DualWAN does not do

DualWAN does not combine the bandwidth of two connections. It is not bonding, MPTCP, or session migration. Policy changes and WAN failover affect new flows; active connections are not moved to another WAN automatically.

## Main features

The Dashboard shows the two WANs and service status. Applications configures individual routing choices. Groups provides shared policies, and Profiles applies predefined Group configurations. History shows WAN download, upload, latency, and packet loss. Statistics shows aggregate routed traffic by application. Settings includes appearance, language, and history retention.

## How routing works

The effective order is:

1. Local/LAN IPv4 destination bypass.
2. Individual application rule.
3. Active Group policy.
4. Normal Windows routing.

Local IPv4 destinations stay on Windows routing. For Internet destinations, an individual choice takes priority over a Group. If neither applies, DualWAN leaves routing to Windows. DualWAN does not change global Windows route metrics.

## Applications

Add an executable, choose a running application, or use assisted detection. The choices are **Prefer WAN1**, **Prefer WAN2**, **Only WAN1**, and **Only WAN2**. Prefer uses the selected WAN while it is available and may use the other WAN for new flows when the preferred WAN is confirmed unavailable. Only uses the selected WAN and does not fall back. Leave an application without an individual rule if a Group or Windows should decide. Individual rules are managed from Applications.

## Groups

Group membership is optional. A Group policy applies to a member without an individual rule. An individual rule always takes priority. The two-pane Groups page supports search, an explicit editor, a multi-application membership picker, an individual-override indicator, and atomic rename. Detection may suggest a Group but does not silently add applications to it.

## Failover

The Service checks WAN health. With a Prefer policy, it can select the other healthy WAN for a new flow when the preferred WAN is unavailable. With an Only policy, the flow does not automatically switch. Existing connections are not migrated. If the WinDivert packet-processing engine fails fatally, DualWAN releases interception and marks routing inactive, returning new traffic toward native Windows behavior; existing flows may still break.

## Local network access

Local IPv4 destinations bypass application WAN policies, so Windows can route traffic to NAS devices, printers, other PCs, and local services. Equivalent IPv6 behavior has not been validated.

## Application detection

Assisted detection uses local application information. Network Activity Detection runs only when started by the user and observes local process ownership of TCP connections and UDP sockets for a selected period. It does not inspect packet contents, web pages, messages, or files. Results remain local and in memory until the user applies selections.

## History

History displays both WANs' download, upload, latency, and packet loss over 1 hour, 24 hours, 7 days, or 30 days. The Service stores historical telemetry in SQLite.

## Application statistics

Statistics displays top applications, upload/download totals, actual WAN1/WAN2 breakdown, TCP connection and UDP session counts, and a traffic chart for 1 hour, 24 hours, 7 days, or 30 days. Retention defaults to 1 hour and can be set to 24 hours, 7 days, or 30 days. These figures cover only IPv4 TCP/UDP payload traffic routed through DualWAN relays; Windows passthrough, LAN bypass, IPv6 and non-relay traffic are excluded. The feature stores minute-level aggregates, not destination or connection histories.

## Requirements

- 64-bit Windows. No precise minimum Windows release is established by this repository.
- Administrator rights to install the background Service and WinDivert component.
- Two usable WAN connections for dual-WAN routing.

The distributed Windows build is self-contained; users do not need to install a separate .NET runtime. See [BUILD.md](BUILD.md) for source-build tools.

## Installation

Download the DualWAN installer, run it with administrator rights, complete installation, and launch DualWAN from the Start menu or the installer's Finish page. The installer sets up the background Service needed for routing and manages WinDivert.

On a fresh installation, open **Settings → WAN configuration** and explicitly select a different network interface for WAN1 and WAN2. Optional friendly names help identify them. The Service remains running without routing until both interfaces are saved; it does not choose adapters automatically. Existing WAN configuration is preserved during upgrades.

## Basic usage

1. Configure WAN1 and WAN2 in Settings, then check both connections in the Dashboard.
2. Add or detect the applications you want to manage.
3. Assign each application a Prefer or Only choice where needed.
4. Leave an application without a rule to use an active Group policy or normal Windows routing.
5. Open History to review WAN traffic and quality.

## Privacy

Routing and application detection run locally; neither requires cloud processing. Network Activity Detection identifies processes using network connections without inspecting the content of those communications. Application statistics store aggregate application identity, WAN, minute bucket, byte and TCP/UDP activity counts; they do not store destination histories or packet contents. See [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md) for attribution limits.

## Known limitations

Failover applies to new flows, IPv6 behavior is not fully validated, and Windows DNS Client and shared system processes can limit application attribution. Detection can miss brief activity. See [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md) for details.

## Building from source

Use the .NET 9 SDK for the Dashboard and Service. The release script also requires NSIS and produces self-contained win-x64 output. See [BUILD.md](BUILD.md) and [ARCHITECTURE.md](ARCHITECTURE.md).

## License

Copyright © 2026 GDM. DualWAN is released under the GNU General Public License v3.0. See [LICENSE](LICENSE). Distributed components retain their own notices, including [WinDivert](Phase1/WinDivert_LICENSE.txt) and the [WireShift-derived diagnostic proxy](SocksProxy/WIREShift_LICENSE.txt).
