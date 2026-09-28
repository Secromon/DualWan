# Per-application LAN isolation research

This document records controlled experiments that informed a **parked, unimplemented** feature. It is research, not a LAN security control. Measurements below came from one Windows test environment and are not universal guarantees.

## Current decision point

Production routing intercepts IPv4 packets at WinDivert NETWORK layer. Local destinations are bypassed before process attribution and follow native Windows routing. NETWORK packets do not contain a PID. The current process lookup was designed for WAN routing, not a confident per-application LAN Block decision. A future LAN policy would need a separate decision before local-packet reinjection while preserving the existing precedence for Internet traffic.

## Owner-table prototype

The isolated `ProcessAttributionPrototype` read Windows IPv4 TCP/UDP owner tables without changing production routing. TCP rows can identify a live established connection by local/remote tuple and PID, but this experiment did not prove ownership at the first outbound SYN. Short-lived rows can disappear before lookup.

`MIB_UDPROW_OWNER_PID` provides local address, local port and PID but no remote endpoint. A unique bound UDP socket was attributable in controlled tests. Reused local endpoints, wildcard/specific bindings and close races were ambiguous. Choosing the first matching row would be unsafe for a Block decision. Process path resolution, PID reuse and shared host processes add uncertainty.

## FLOW and SOCKET experiment

The isolated observation-only `FlowSocketExperiment` opened separate WinDivert FLOW, SOCKET and test NETWORK handles. It did not inject or block packets and was not integrated with the Service.

In 100 controlled local TCP connections, FLOW established metadata was captured **after** the first outbound SYN in all 100. SOCKET CONNECT's native capture timestamp preceded SYN in 100/100; nevertheless the user-mode SOCKET observer processed CONNECT after the NETWORK observer processed SYN in 1/100 in the final run. A prior run had 2/100 such inversions. This does not establish a guaranteed first-packet enforcement order in production.

Two UDP processes bound to the same local endpoint and sent to the same destination. FLOW/SOCKET emitted distinct PID metadata, while their NETWORK packets had identical five-tuples and no endpoint identifier. Thus a packet could not be confidently assigned to one PID. A unique destination in a controlled variant was distinguishable, but does not solve the same-destination case.

FLOW/SOCKET metadata is non-retroactive: a socket created before observer startup or retained across observer restart produced no replayed event. A table snapshot can recover some current sockets but not a complete event history or reliable ownership for reused endpoints. Controlled multicast and broadcast observations were incomplete and do not justify blocking claims.

## Safety conclusion

Per-application LAN isolation remains **parked and not implemented**. The current NETWORK-layer architecture cannot reliably guarantee packet ownership for all required traffic, especially UDP. A future design must treat an unknown or ambiguous owner as **allow / fail open**, never guess a PID. It must also release interception if the packet-processing engine fails; existing flows may still break. IPv6, DNS through shared processes, multicast and broadcast require independent coverage analysis before any security claim.

The research projects may remain in source for contributors, but production Service and Dashboard do not reference them and the installer does not include them.
