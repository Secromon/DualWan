# FLOW/SOCKET attribution experiment

Standalone local diagnostic for Step 10.4D. It is not referenced by the Service, Dashboard, or installer.

Run `DualWAN.FlowSocketExperiment.exe --smoke` from an elevated terminal to check observation-only FLOW and SOCKET handle open/close. Run without arguments for bounded loopback TCP/UDP experiments. The installed WinDivert driver must already be running; `NO_INSTALL` prevents this program from installing it.

All handles use `SNIFF | RECV_ONLY | NO_INSTALL` at priority `-1000`. No packet reinjection API is imported. The NETWORK observer is limited to loopback IPv4 TCP/UDP copies. The program opens controlled local sockets, sends at most one-byte multicast/broadcast test datagrams, and closes handles on normal exit, error, or Ctrl+C. Its event buffers are capped at 10,000 entries each and remain in memory only.

Run this diagnostic only for local investigation. Its event observations and caches are not suitable for enforcement or LAN blocking.
