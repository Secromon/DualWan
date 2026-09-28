# Known limitations

- **No combined bandwidth:** DualWAN does not bond WANs, implement MPTCP, or combine two connections into one faster session.
- **No session migration:** A policy change, WAN failure, or failback does not move an established connection. Failover decisions affect new flows.
- **IPv6 validation:** The accepted local-destination bypass and routing checks cover IPv4. Equivalent IPv6 routing and LAN bypass are not fully validated.
- **DNS attribution:** Windows DNS Client behavior can prevent reliable attribution of a DNS request to the originating application.
- **Shared Windows processes:** A shared `svchost` process can represent several services, so application-level attribution may be ambiguous.
- **Brief network activity:** Network Activity Detection samples process-owned TCP connections and UDP sockets. Very short connections may appear and disappear between samples.
- **UDP ownership:** An owned UDP socket does not by itself prove that the process transmitted data during the scan.
- **Application statistics coverage:** Figures cover only IPv4 TCP/UDP payload traffic routed through DualWAN relays. Windows passthrough, LAN bypass, IPv6, and other non-relay traffic are absent; this is not total application bandwidth.
- **Statistics delay and retention:** Completed minute buckets are persisted, so the newest activity may appear after roughly one minute. Retention defaults to one hour; increasing it cannot reconstruct previously discarded history.
- **LAN isolation:** Per-application LAN blocking is not implemented. Current NETWORK-layer attribution cannot confidently map every packet to its owner, especially with reused UDP endpoints. See [research findings](docs/LAN_ISOLATION_RESEARCH.md).
- **Unsigned builds:** Current binaries and installer are unsigned, so Windows may display an untrusted-publisher warning.

These limits describe the current implementation. They do not imply that traffic contents are captured by Network Activity Detection or application statistics.
