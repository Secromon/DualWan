# Known limitations

- **No combined bandwidth:** DualWAN does not bond WANs, implement MPTCP, or combine two connections into one faster session.
- **No session migration:** A policy change, WAN failure, or failback does not move an established connection. Failover decisions affect new flows.
- **IPv6 validation:** The accepted local-destination bypass and routing checks cover IPv4. Equivalent IPv6 routing and LAN bypass are not fully validated.
- **DNS attribution:** Windows DNS Client behavior can prevent reliable attribution of a DNS request to the originating application.
- **Shared Windows processes:** A shared `svchost` process can represent several services, so application-level attribution may be ambiguous.
- **Brief network activity:** Network Activity Detection samples process-owned TCP connections and UDP sockets. Very short connections may appear and disappear between samples.
- **UDP ownership:** An owned UDP socket does not by itself prove that the process transmitted data during the scan.
- **Unsigned builds:** Current binaries and installer are unsigned, so Windows may display an untrusted-publisher warning.

These limits describe the 1.0.0 implementation. They do not imply that traffic contents are captured by Network Activity Detection.
