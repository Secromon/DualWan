using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NetBinder.Service.NativeInterop;
using NetBinder.Shared.Models;

namespace NetBinder.Service.Services;

/// <summary>
/// Redirector service using WinDivert to transparently redirect bound app traffic
/// through a specific network interface via a local transparent proxy.
///
/// ARCHITECTURE:
/// 1. Capture outbound SYN from bound app (e.g., Brave -> google.com:443)
/// 2. Rewrite dst to 127.0.0.1:proxyPort AND set addr to loopback interface
/// 3. The transparent proxy receives the connection, looks up the original dest
/// 4. Proxy connects to google.com:443 via the TARGET interface (IP_UNICAST_IF + Bind)
/// 5. Capture inbound reply from proxy (127.0.0.1:proxyPort -> 127.0.0.1:clientPort)
/// 6. Rewrite src back to original remote (google.com:443) AND dst back to client IP
///    so the kernel's TCP state machine accepts the reply
/// 7. Set addr back to original interface for proper delivery
///
/// KEY INSIGHT: WinDivert's WinDivertSend uses addr.IfIdx to determine which interface
/// to inject the packet on. For loopback delivery, we MUST set IfIdx=1 (loopback)
/// and the Loopback flag=true. For reverse NAT, we must restore the original interface.
/// </summary>
public class RedirectorService : IDisposable
{
    private readonly object _lifecycleLock = new();
    private readonly object _stopLock = new();
    private readonly IWinDivertIo _io;
    private readonly Action<RedirectorService>? _onFailure;
    private IntPtr _divertHandle = IntPtr.Zero;
    private Thread? _divertThread;
    private volatile bool _isRunning;
    private int _consecutiveSendFailures;
    private int _proxyPort;
    private readonly UdpRelay _udpRelay;
    private readonly LocalDestinationClassifier _localDestinations;
    private Timer? _localRouteRefresh;

    /// <summary>
    /// NAT table entry: stores everything needed for bidirectional packet rewriting.
    /// </summary>
    private sealed class NatEntry
    {
        public required FlowKey Key;
        public required IPEndPoint OriginalDest;       // Where the app wanted to connect (e.g., google.com:443)
        public required IPAddress OriginalClientIp;    // Client's source IP on the physical interface
        public int TargetInterfaceIndex;      // Interface to route through
        public uint OriginalIfIdx;            // Original WinDivert interface index (for reverse NAT)
        public uint OriginalSubIfIdx;         // Original WinDivert sub-interface index
        public DateTime LastSeenUtc = DateTime.UtcNow;
        public FlowState State = FlowState.Created;
        public required FlowRecord Report;
    }

    private enum FlowState { Created, Active, Closed, Expired }
    private readonly record struct PacketTuple(IPAddress Source, ushort SourcePort, IPAddress Destination, ushort DestinationPort);
    private readonly record struct FlowKey(AddressFamily Family, byte Protocol, IPAddress Source, ushort SourcePort,
        IPAddress Destination, ushort DestinationPort, int ProcessId, long Generation);
    private readonly record struct RoutingDecision(bool Matched, BindingMapping? Binding,
        ProcessRoutingPolicy? Policy, WanHealthState PrimaryState, bool Failover, bool Failback, string Reason);
    private readonly ConcurrentDictionary<PacketTuple, NatEntry> _natTable = new();
    private readonly ConcurrentDictionary<PacketTuple, UdpRelay.Session> _udpTable = new();
    private readonly ConcurrentDictionary<(IPAddress, ushort), NatEntry> _relayTable = new();
    private readonly ConcurrentDictionary<long, FlowRecord> _records = new();
    private readonly DateTimeOffset _sessionStarted = DateTimeOffset.Now;
    private long _ownerUnknown;
    private long _nextGeneration;
    private DateTime _nextSweepUtc = DateTime.UtcNow.AddSeconds(5);
    private readonly List<BindingMapping> _activeBindings = new();
    private readonly List<ProcessRoutingPolicy> _policies = new();
    private readonly Dictionary<string, BindingMapping> _wanBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastSelectedWan = new(StringComparer.OrdinalIgnoreCase);
    private WanHealthService? _health;
    private long _failovers;
    private long _failbacks;
    private long _strictFailures;
    private long _udpRoutedFlows;
    private long _udpFailedFlows;

    public bool IsRunning => _isRunning;
    public int ActiveFlowCount => _natTable.Count + _udpTable.Count;
    public long TotalRoutedFlowCount => _records.Values.LongCount(x => x.Connected) + Interlocked.Read(ref _udpRoutedFlows);
    public long FailedFlowCount => _records.Values.LongCount(x => x.Failed) + Interlocked.Read(ref _udpFailedFlows);
    public long FailoverCount => Interlocked.Read(ref _failovers);
    public long FailbackCount => Interlocked.Read(ref _failbacks);
    public long StrictFailureCount => Interlocked.Read(ref _strictFailures);
    private readonly object _bindingsLock = new();
    // Protects the mutable policy/binding snapshot and last-WAN decisions from
    // IPC updates while the WinDivert thread classifies new packets.

    // Windows loopback interface index is always 1
    private const uint LOOPBACK_IFIDX = 1;

    public RedirectorService(UdpRelay udpRelay)
        : this(udpRelay, new LocalDestinationClassifier(new WindowsLocalIpv4RouteSource()),
            new NativeWinDivertIo()) { }

    public RedirectorService(UdpRelay udpRelay, Action<RedirectorService> onFailure)
        : this(udpRelay, new LocalDestinationClassifier(new WindowsLocalIpv4RouteSource()),
            new NativeWinDivertIo(), onFailure) { }

    public RedirectorService(UdpRelay udpRelay, LocalDestinationClassifier localDestinations)
        : this(udpRelay, localDestinations, new NativeWinDivertIo()) { }

    public RedirectorService(UdpRelay udpRelay, LocalDestinationClassifier localDestinations,
        IWinDivertIo io, Action<RedirectorService>? onFailure = null)
    {
        _udpRelay = udpRelay ?? throw new ArgumentNullException(nameof(udpRelay));
        _localDestinations = localDestinations ?? throw new ArgumentNullException(nameof(localDestinations));
        _io = io ?? throw new ArgumentNullException(nameof(io));
        _onFailure = onFailure;
    }

    /// <summary>
    /// Looks up the NAT mapping for a given client local port.
    /// Used by the Transparent Proxy to know the real destination and target interface.
    /// </summary>
    public (IPEndPoint OriginalDest, int InterfaceIndex, long Generation, FlowRecord Report)? GetNATMapping(IPAddress clientIp, ushort localPort)
    {
        if (_relayTable.TryGetValue((clientIp, localPort), out var entry) && entry.State is FlowState.Created or FlowState.Active)
        {
            entry.LastSeenUtc = DateTime.UtcNow;
            Console.WriteLine($"DEBUG FLOW {entry.Key.Generation} relay mapping resolved: {clientIp}:{localPort} -> {entry.OriginalDest}");
            return (entry.OriginalDest, entry.TargetInterfaceIndex, entry.Key.Generation, entry.Report);
        }
        return null;
    }

    /// <summary>
    /// Updates WAN bindings and policies for new flows; existing connections and DNS remain unchanged.
    /// </summary>
    public void UpdateBindings(IEnumerable<BindingMapping> bindings,
        IEnumerable<ProcessRoutingPolicy> policies, WanHealthService health)
    {
        lock (_bindingsLock)
        {
            _activeBindings.Clear();
            _wanBindings.Clear();
            foreach (var b in bindings)
            {
                if (b.IsActive)
                {
                    _activeBindings.Add(b);
                    _wanBindings[b.LogicalWan] = b;
                }
            }
            _policies.Clear();
            _policies.AddRange(policies);
            _health = health;
            Console.WriteLine($"[RedirectorService] Updated WANs={_wanBindings.Count}; policies={_policies.Count}.");
        }
    }

    /// <summary>
    /// Starts the WinDivert packet capture and redirection loop.
    /// </summary>
    public bool Start(int proxyPort)
    {
        lock (_lifecycleLock)
        {
            if (_isRunning) return true;
            _localDestinations.Refresh();
            _proxyPort = proxyPort;
            string filter = $"(outbound and ip and ((tcp and tcp.DstPort != {proxyPort} and tcp.SrcPort != {proxyPort}) or (udp and udp.SrcPort != 67 and udp.SrcPort != 68 and udp.DstPort != 67 and udp.DstPort != 68))) or (loopback and ip and ((tcp and tcp.SrcPort == {proxyPort}) or udp))";
            Console.WriteLine($"[RedirectorService] Opening WinDivert with filter: {filter}");
            IntPtr handle = _io.Open(filter);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                Console.WriteLine($"[RedirectorService] Failed to open WinDivert handle. Win32 Error: {_io.LastError}");
                return false;
            }
            _divertHandle = handle;
            try
            {
                _consecutiveSendFailures = 0;
                _localRouteRefresh = new Timer(_ => _localDestinations.Refresh(), null,
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                _divertThread = new Thread(PacketLoop)
                {
                    IsBackground = true,
                    Name = "NetBinderWinDivertThread"
                };
                _isRunning = true;
                _divertThread.Start();
            }
            catch
            {
                ReleaseCapture();
                _divertThread = null;
                throw;
            }
        }
        Console.WriteLine("[RedirectorService] Started successfully.");
        return _isRunning;
    }

    public void Stop()
    {
        lock (_stopLock)
        {
            bool wasRunning = ReleaseCapture();
            if (!wasRunning && _divertThread is null) return;
            Console.WriteLine("[RedirectorService] Stopping...");
            Thread? thread = _divertThread;
            if (thread is not null && thread != Thread.CurrentThread &&
                !thread.Join(TimeSpan.FromSeconds(3)))
                Console.WriteLine("[RedirectorService] Warning: Thread did not stop gracefully.");
            _divertThread = null;

            foreach (var entry in _natTable.Values)
                RemoveFlow(entry, FlowState.Closed, "engine stopped");
            _natTable.Clear();
            _relayTable.Clear();
            foreach (var session in _udpTable.Values)
                _udpRelay.Remove(session, "engine stopped", false);
            _udpTable.Clear();
            PrintSessionSummary();
            Console.WriteLine("[RedirectorService] Stopped.");
        }
    }

    // Detach the handle under one lock so Stop and fatal-loop cleanup cannot
    // close it twice. Closing unblocks a pending WinDivertRecv.
    private bool ReleaseCapture()
    {
        lock (_lifecycleLock)
        {
            bool wasRunning = _isRunning;
            _isRunning = false;
            IntPtr handle = _divertHandle;
            _divertHandle = IntPtr.Zero;
            Timer? timer = _localRouteRefresh;
            _localRouteRefresh = null;
            timer?.Dispose();
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                if (!_io.Close(handle))
                    Environment.FailFast($"WinDivertClose failed ({_io.LastError}); terminating to release process-owned interception handle.");
                Console.WriteLine("[RedirectorService] WinDivert interception released");
            }
            return wasRunning;
        }
    }

    private void PacketLoop()
    {
        const int bufferSize = 65536;
        IntPtr pPacketBuffer = IntPtr.Zero;
        WINDIVERT_ADDRESS addr = new WINDIVERT_ADDRESS();
        int receiveFailures = 0;
        try
        {
            pPacketBuffer = Marshal.AllocHGlobal(bufferSize);
            while (_isRunning)
            {
                IntPtr handle = _divertHandle;
                if (!_io.Receive(handle, pPacketBuffer, bufferSize, out uint recvLen, ref addr))
                {
                    if (!_isRunning) break;
                    int err = _io.LastError;
                    if (err is 6 or 995 || ++receiveFailures >= 3)
                        throw new IOException($"WinDivertRecv failed repeatedly or fatally: {err}");
                    Thread.Sleep(1);
                    continue;
                }
                receiveFailures = 0;
                ProcessPacket(pPacketBuffer, recvLen, ref addr);
                if (DateTime.UtcNow >= _nextSweepUtc)
                {
                    SweepFlows();
                    _nextSweepUtc = DateTime.UtcNow.AddSeconds(5);
                }
            }
        }
        catch (Exception ex)
        {
            if (_isRunning) Console.WriteLine($"PACKET LOOP FAILURE: {ex}");
        }
        finally
        {
            if (pPacketBuffer != IntPtr.Zero) Marshal.FreeHGlobal(pPacketBuffer);
            if (ReleaseCapture())
            {
                Console.WriteLine("[RedirectorService] Routing engine inactive after packet-loop failure");
                try { _onFailure?.Invoke(this); }
                catch (Exception ex) { Console.WriteLine($"[RedirectorService] Failure notification failed: {ex.Message}"); }
            }
        }
    }

    private void SendPacket(IntPtr packet, uint length, ref WINDIVERT_ADDRESS address)
    {
        if (!_isRunning) return;
        if (_io.Send(_divertHandle, packet, length, out _, ref address))
        {
            _consecutiveSendFailures = 0;
            return;
        }
        if (!_isRunning) return;
        if (++_consecutiveSendFailures >= 3)
            throw new IOException($"WinDivertSend failed repeatedly: {_io.LastError}");
        if (_consecutiveSendFailures == 1)
            Console.WriteLine($"[RedirectorService] WinDivertSend failed; monitoring consecutive failures: {_io.LastError}");
    }

    private unsafe void ProcessPacket(IntPtr pPacketBuffer, uint recvLen, ref WINDIVERT_ADDRESS addr)
    {
        byte* packet = (byte*)pPacketBuffer.ToPointer();
        byte ipVersion = (byte)((packet[0] >> 4) & 0x0F);

        // IPv6: block TCP SYN + QUIC from bound apps
        if (ipVersion == 6)
        {
            HandleIPv6(packet, recvLen, pPacketBuffer, ref addr);
            return;
        }

        if (ipVersion != 4)
        {
            SendPacket(pPacketBuffer, recvLen, ref addr);
            return;
        }

        byte ipProto = packet[9];

        // UDP uses a separate datagram relay and timeout-based session table.
        if (ipProto == 17)
        {
            HandleUdp(packet, recvLen, pPacketBuffer, ref addr);
            return;
        }

        if (ipProto != 6) // Not TCP
        {
            SendPacket(pPacketBuffer, recvLen, ref addr);
            return;
        }

        // TCP processing
        int ipHeaderLen = (packet[0] & 0x0F) * 4;
        ushort srcPort = (ushort)((packet[ipHeaderLen] << 8) | packet[ipHeaderLen + 1]);
        ushort dstPort = (ushort)((packet[ipHeaderLen + 2] << 8) | packet[ipHeaderLen + 3]);
        byte tcpFlags = packet[ipHeaderLen + 13];
        var srcIp = new IPAddress(*(uint*)(packet + 12));
        var dstIp = new IPAddress(*(uint*)(packet + 16));
        var tuple = new PacketTuple(srcIp, srcPort, dstIp, dstPort);

        if (addr.Loopback)
        {
            // LOOPBACK PACKET: This could be from our proxy replying to the client.
            // Check if it's from our proxy port and has a NAT entry.
            if (srcPort == (ushort)_proxyPort &&
                _relayTable.TryGetValue((dstIp, dstPort), out var natEntry) &&
                natEntry.State is FlowState.Created or FlowState.Active)
            {
                natEntry.LastSeenUtc = DateTime.UtcNow;
                natEntry.State = FlowState.Active;
                // REVERSE NAT: Rewrite source to look like original remote server
                byte[] origIpBytes = natEntry.OriginalDest.Address.GetAddressBytes();
                packet[12] = origIpBytes[0]; // src IP = original remote IP
                packet[13] = origIpBytes[1];
                packet[14] = origIpBytes[2];
                packet[15] = origIpBytes[3];

                // Restore destination to original client IP
                byte[] clientIpBytes = natEntry.OriginalClientIp.GetAddressBytes();
                packet[16] = clientIpBytes[0]; // dst IP = original client IP
                packet[17] = clientIpBytes[1];
                packet[18] = clientIpBytes[2];
                packet[19] = clientIpBytes[3];

                // Rewrite source port to original remote port
                ushort origPort = (ushort)natEntry.OriginalDest.Port;
                packet[ipHeaderLen] = (byte)(origPort >> 8);
                packet[ipHeaderLen + 1] = (byte)(origPort & 0xFF);

                // Restore addr to original physical interface
                addr.IfIdx = natEntry.OriginalIfIdx;
                addr.SubIfIdx = natEntry.OriginalSubIfIdx;
                addr.SetLoopback(false);
                addr.SetOutbound(false); // Mark as inbound so Windows TCP stack delivers it to the client
                // Keep as inbound (proxy -> client)

                WinDivertNative.WinDivertHelperCalcChecksums(pPacketBuffer, recvLen, ref addr, 0);

                if ((tcpFlags & (0x01 | 0x04)) != 0)
                    ScheduleNatCleanup(natEntry);

                natEntry.Report.ReverseNatOnce();
            }

            // Re-inject (modified or not)
            SendPacket(pPacketBuffer, recvLen, ref addr);
            return;
        }

        // NON-LOOPBACK PACKET
        if (addr.Outbound)
        {
            // Leave directly reachable IPv4 traffic untouched before owner lookup or WAN policy.
            if (_localDestinations.IsLocalDestination(dstIp))
            {
                SendPacket(pPacketBuffer, recvLen, ref addr);
                return;
            }

            bool newSyn = (tcpFlags & 0x02) != 0 && (tcpFlags & 0x10) == 0;
            if (newSyn && _natTable.TryGetValue(tuple, out var oldEntry))
                RemoveFlow(oldEntry, FlowState.Expired, "new SYN reused tuple");
            bool isNatted = _natTable.TryGetValue(tuple, out var existingEntry);

            if (isNatted)
            {
                existingEntry!.LastSeenUtc = DateTime.UtcNow;
                existingEntry.State = FlowState.Active;
                // Already tracked: redirect to our proxy on loopback
                // Rewrite both IPs to 127.0.0.1
                packet[12] = 127; packet[13] = 0; packet[14] = 0; packet[15] = 1; // src = 127.0.0.1
                packet[16] = 127; packet[17] = 0; packet[18] = 0; packet[19] = 1; // dst = 127.0.0.1

                // Rewrite dst port to proxy port
                packet[ipHeaderLen + 2] = (byte)(_proxyPort >> 8);
                packet[ipHeaderLen + 3] = (byte)(_proxyPort & 0xFF);

                // CRITICAL: Set addr to loopback interface so WinDivert delivers correctly
                addr.IfIdx = LOOPBACK_IFIDX;
                addr.SubIfIdx = 0;
                addr.SetLoopback(true);

                WinDivertNative.WinDivertHelperCalcChecksums(pPacketBuffer, recvLen, ref addr, 0);

                if ((tcpFlags & (0x01 | 0x04)) != 0)
                    ScheduleNatCleanup(existingEntry);
            }
            else
            {
                // Check for new SYN from bound app
                if (newSyn)
                {
                    int pid = TcpTableWrapper.GetOwnerPidForFlow(srcIp, srcPort, dstIp, dstPort);
                    if (pid > 0)
                    {
                        string? exePath = GetProcessPath(pid);
                        Console.WriteLine($"DEBUG PID resolution: PID={pid}, Executable={exePath ?? "UNKNOWN"}, tuple={tuple}");
                        if (dstPort == 53)
                            LogDnsObservation("TCP", pid, exePath, srcIp, srcPort, dstIp, dstPort);
                        if (exePath is null)
                        {
                            Interlocked.Increment(ref _ownerUnknown);
                            Console.WriteLine($"INFO OWNER UNKNOWN | Timestamp={DateTimeOffset.Now:O} | Process=UNKNOWN | PID={pid} | Executable=UNKNOWN | Source={srcIp}:{srcPort} | Destination={dstIp}:{dstPort} | Reason=Executable path unavailable at capture time");
                        }
                        var decision = ResolveRouting(exePath, dstIp);
                        if (decision.Matched && decision.Binding is null)
                        {
                            long failedGeneration = Interlocked.Increment(ref _nextGeneration);
                            LogPolicyDecision(failedGeneration, "TCP", pid, exePath, tuple, decision, "FAILED_CLOSED");
                            return;
                        }
                        var binding = decision.Binding;

                        if (binding is not null)
                        {
                            // Save NAT entry with all the info needed for bidirectional rewriting
                            uint origDstIpRaw = *(uint*)(packet + 16);
                            uint origSrcIpRaw = *(uint*)(packet + 12);

                            var generation = Interlocked.Increment(ref _nextGeneration);
                            LogPolicyDecision(generation, "TCP", pid, exePath, tuple, decision, "SELECTED");
                            var report = new FlowRecord(generation, pid, exePath!, binding,
                                new IPEndPoint(srcIp, srcPort), new IPEndPoint(dstIp, dstPort), _proxyPort);
                            var natEntry = new NatEntry
                            {
                                Key = new FlowKey(AddressFamily.InterNetwork, 6, srcIp, srcPort, dstIp, dstPort,
                                    pid, generation),
                                OriginalDest = new IPEndPoint(new IPAddress(origDstIpRaw), dstPort),
                                OriginalClientIp = new IPAddress(origSrcIpRaw),
                                TargetInterfaceIndex = binding.InterfaceIndex,
                                OriginalIfIdx = addr.IfIdx,
                                OriginalSubIfIdx = addr.SubIfIdx,
                                Report = report
                            };

                            if (!_relayTable.TryAdd((IPAddress.Loopback, srcPort), natEntry))
                            {
                                Console.WriteLine($"[RedirectorService] FLOW COLLISION: {tuple} PID={pid}; dropped (fail closed)");
                                report.Fail("relay mapping", detail: "Source port collision");
                                return;
                            }
                            if (!_natTable.TryAdd(tuple, natEntry))
                            {
                                ((ICollection<KeyValuePair<(IPAddress, ushort), NatEntry>>)_relayTable)
                                    .Remove(new((IPAddress.Loopback, srcPort), natEntry));
                                Console.WriteLine($"[RedirectorService] FLOW COLLISION: {tuple} PID={pid}; dropped (fail closed)");
                                report.Fail("NAT mapping", detail: "Tuple collision");
                                return;
                            }
                            _records[generation] = report;
                            Console.WriteLine($"DEBUG FLOW {generation} FlowKey: AF=IPv4, protocol=TCP, source={srcIp}:{srcPort}, destination={dstIp}:{dstPort}, PID={pid}, generation={generation}");
                            report.Created();

                            // Rewrite to loopback proxy
                            packet[12] = 127; packet[13] = 0; packet[14] = 0; packet[15] = 1;
                            packet[16] = 127; packet[17] = 0; packet[18] = 0; packet[19] = 1;
                            packet[ipHeaderLen + 2] = (byte)(_proxyPort >> 8);
                            packet[ipHeaderLen + 3] = (byte)(_proxyPort & 0xFF);

                            addr.IfIdx = LOOPBACK_IFIDX;
                            addr.SubIfIdx = 0;
                            addr.SetLoopback(true);

                            WinDivertNative.WinDivertHelperCalcChecksums(pPacketBuffer, recvLen, ref addr, 0);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref _ownerUnknown);
                        Console.WriteLine($"INFO OWNER UNKNOWN | Timestamp={DateTimeOffset.Now:O} | Process=UNKNOWN | PID=UNKNOWN | Executable=UNKNOWN | Source={srcIp}:{srcPort} | Destination={dstIp}:{dstPort} | Reason=TCP owner table did not contain tuple at capture time");
                    }
                }
            }

            SendPacket(pPacketBuffer, recvLen, ref addr);
        }
        else
        {
            // Inbound non-loopback: just pass through
            SendPacket(pPacketBuffer, recvLen, ref addr);
        }
    }

    private unsafe void HandleIPv6(byte* packet, uint recvLen, IntPtr pPacketBuffer, ref WINDIVERT_ADDRESS addr)
    {
        if (addr.Outbound)
        {
            byte nextHeader = packet[6];

            if (nextHeader == 6) // TCP
            {
                ushort srcPort = (ushort)((packet[40] << 8) | packet[41]);
                byte tcpFlags = packet[40 + 13];
                bool isSyn = (tcpFlags & 0x02) != 0 && (tcpFlags & 0x10) == 0;

                if (isSyn)
                {
                    int pid = TcpTableWrapper.GetOwnerPidForLocalPort(srcPort);
                    if (pid > 0 && HasPolicy(GetProcessPath(pid)))
                    {
                        Console.WriteLine($"[RedirectorService] Blocked IPv6 TCP SYN. PID={pid}, Port={srcPort}");
                        return; // DROP
                    }
                }
            }
            else if (nextHeader == 17) // UDP
            {
                ushort udpDstPort = (ushort)((packet[40 + 2] << 8) | packet[40 + 3]);
                if (udpDstPort == 443)
                {
                    ushort udpSrcPort = (ushort)((packet[40] << 8) | packet[41]);
                    int pid = TcpTableWrapper.GetOwnerPidForLocalUdpPort(udpSrcPort);
                    if (pid > 0 && HasPolicy(GetProcessPath(pid)))
                    {
                        Console.WriteLine($"[RedirectorService] Blocked IPv6 QUIC. PID={pid}");
                        return; // DROP
                    }
                }
            }
        }

        SendPacket(pPacketBuffer, recvLen, ref addr);
    }

    private unsafe void HandleUdp(byte* packet, uint recvLen, IntPtr pPacketBuffer, ref WINDIVERT_ADDRESS addr)
    {
        int ipHdrLen = (packet[0] & 0x0F) * 4;
        if (recvLen < ipHdrLen + 8)
        {
            SendPacket(pPacketBuffer, recvLen, ref addr);
            return;
        }

        ushort srcPort = (ushort)((packet[ipHdrLen] << 8) | packet[ipHdrLen + 1]);
        ushort dstPort = (ushort)((packet[ipHdrLen + 2] << 8) | packet[ipHdrLen + 3]);
        var srcIp = new IPAddress(*(uint*)(packet + 12));
        var dstIp = new IPAddress(*(uint*)(packet + 16));

        if (addr.Loopback)
        {
            if (_udpRelay.TryGetRelay(srcPort, out var replySession) &&
                dstPort == replySession.OriginalSource.Port)
            {
                byte[] remote = replySession.Destination.Address.GetAddressBytes();
                byte[] client = replySession.OriginalSource.Address.GetAddressBytes();
                for (int i = 0; i < 4; i++) { packet[12 + i] = remote[i]; packet[16 + i] = client[i]; }
                packet[ipHdrLen] = (byte)(replySession.Destination.Port >> 8);
                packet[ipHdrLen + 1] = (byte)(replySession.Destination.Port & 0xFF);
                addr.IfIdx = replySession.OriginalIfIdx;
                addr.SubIfIdx = replySession.OriginalSubIfIdx;
                addr.SetLoopback(false);
                addr.SetOutbound(false);
                replySession.Touch();
                WinDivertNative.WinDivertHelperCalcChecksums(pPacketBuffer, recvLen, ref addr, 0);
            }
            SendPacket(pPacketBuffer, recvLen, ref addr);
            return;
        }

        if (!addr.Outbound)
        {
            SendPacket(pPacketBuffer, recvLen, ref addr);
            return;
        }

        // Packets generated by the bound relay socket must leave through the selected WAN unchanged.
        if (_udpRelay.IsOutbound(srcIp, srcPort))
        {
            SendPacket(pPacketBuffer, recvLen, ref addr);
            return;
        }

        if (_localDestinations.IsLocalDestination(dstIp))
        {
            SendPacket(pPacketBuffer, recvLen, ref addr);
            return;
        }

        var tuple = new PacketTuple(srcIp, srcPort, dstIp, dstPort);
        if (!_udpTable.TryGetValue(tuple, out var session))
        {
            int pid = TcpTableWrapper.GetOwnerPidForLocalUdpPort(srcPort);
            string? exePath = pid > 0 ? GetProcessPath(pid) : null;
            Console.WriteLine($"DEBUG UDP PID resolution: PID={(pid > 0 ? pid : 0)}, Executable={exePath ?? "UNKNOWN"}, source={srcIp}:{srcPort}, destination={dstIp}:{dstPort}");
            if (dstPort == 53)
                LogDnsObservation("UDP", pid, exePath, srcIp, srcPort, dstIp, dstPort);
            var decision = ResolveRouting(exePath, dstIp);
            if (!decision.Matched)
            {
                SendPacket(pPacketBuffer, recvLen, ref addr);
                return;
            }
            long generation = Interlocked.Increment(ref _nextGeneration);
            if (decision.Binding is null)
            {
                Interlocked.Increment(ref _udpFailedFlows);
                LogPolicyDecision(generation, "UDP", pid, exePath, tuple, decision, "FAILED_CLOSED");
                return;
            }
            var binding = decision.Binding;
            LogPolicyDecision(generation, "UDP", pid, exePath, tuple, decision, "SELECTED");
            session = _udpRelay.Create(generation, pid, exePath!, binding,
                new IPEndPoint(srcIp, srcPort), new IPEndPoint(dstIp, dstPort), addr.IfIdx, addr.SubIfIdx);
            if (session is null)
            {
                Interlocked.Increment(ref _udpFailedFlows);
                return; // fail closed
            }

            if (!_udpTable.TryAdd(tuple, session))
            {
                _udpRelay.Remove(session, "FLOW COLLISION", false);
                Interlocked.Increment(ref _udpFailedFlows);
                Console.WriteLine($"ERROR UDP FLOW COLLISION: {tuple}, PID={pid}; FAIL CLOSED");
                return;
            }
            Interlocked.Increment(ref _udpRoutedFlows);
        }

        if (session.State == "FAILED") return;
        session.Touch();
        packet[12] = 127; packet[13] = 0; packet[14] = 0; packet[15] = 1;
        packet[16] = 127; packet[17] = 0; packet[18] = 0; packet[19] = 1;
        packet[ipHdrLen + 2] = (byte)(session.RelayPort >> 8);
        packet[ipHdrLen + 3] = (byte)(session.RelayPort & 0xFF);
        addr.IfIdx = LOOPBACK_IFIDX;
        addr.SubIfIdx = 0;
        addr.SetLoopback(true);
        WinDivertNative.WinDivertHelperCalcChecksums(pPacketBuffer, recvLen, ref addr, 0);
        SendPacket(pPacketBuffer, recvLen, ref addr);
    }

    private void ScheduleNatCleanup(NatEntry entry)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(t =>
        {
            RemoveFlow(entry, FlowState.Closed, "TCP FIN/RST");
        });
    }

    private void RemoveFlow(NatEntry entry, FlowState state, string reason)
    {
        entry.State = state;
        var tuple = new PacketTuple(entry.Key.Source, entry.Key.SourcePort,
            entry.Key.Destination, entry.Key.DestinationPort);
        ((ICollection<KeyValuePair<PacketTuple, NatEntry>>)_natTable).Remove(new(tuple, entry));
        ((ICollection<KeyValuePair<(IPAddress, ushort), NatEntry>>)_relayTable)
            .Remove(new((IPAddress.Loopback, entry.Key.SourcePort), entry));
        entry.Report.Closed(reason, state == FlowState.Expired);
    }

    private void SweepFlows()
    {
        foreach (var entry in _natTable.Values)
        {
            if (DateTime.UtcNow - entry.LastSeenUtc > TimeSpan.FromMinutes(2))
            {
                RemoveFlow(entry, FlowState.Expired, "idle timeout");
                continue;
            }
            try { using var process = Process.GetProcessById(entry.Key.ProcessId); }
            catch (ArgumentException) { RemoveFlow(entry, FlowState.Expired, "process exited"); }
        }

        foreach (var pair in _udpTable)
        {
            var session = pair.Value;
            bool expired = DateTime.UtcNow - session.LastActivityUtc > TimeSpan.FromSeconds(30);
            bool processExited = false;
            if (!expired)
            {
                try { using var process = Process.GetProcessById(session.Pid); }
                catch (ArgumentException) { processExited = true; }
            }
            if (expired || processExited)
            {
                ((ICollection<KeyValuePair<PacketTuple, UdpRelay.Session>>)_udpTable).Remove(new(pair.Key, session));
                _udpRelay.Remove(session, processExited ? "process exited" : "idle timeout", true);
            }
        }
    }

    private void LogDnsObservation(string protocol, int pid, string? executable,
        IPAddress source, ushort sourcePort, IPAddress destination, ushort destinationPort)
    {
        string owner = executable is null ? "UNKNOWN" : Path.GetFileName(executable);
        string requester = owner.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) ? "UNKNOWN" : owner;
        BindingMapping? wan;
        lock (_bindingsLock)
            wan = _activeBindings.FirstOrDefault(b => b.InterfaceIpv4 == source.ToString());

        long flow = Interlocked.Increment(ref _nextGeneration);
        string wanText = wan is null ? "UNKNOWN" : $"{wan.LogicalWan}/{wan.InterfaceName}[{wan.InterfaceIndex}]";
        Console.WriteLine($"ROUTING | {DateTimeOffset.Now:O} | DNS | FLOW={flow} | OWNER={owner} | PID={(pid > 0 ? pid : 0)} | REQUESTER={requester} | PROTOCOL={protocol} | {source}:{sourcePort}->{destination}:{destinationPort} | WAN={wanText} | OUT={source} | RESULT=OBSERVED");
    }

    private string? GetProcessPath(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            string? path = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path)) return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RedirectorService] Process path lookup PID={pid} failed: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    private bool HasPolicy(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        string exeName = Path.GetFileName(exePath);
        string nameWithoutExt = Path.GetFileNameWithoutExtension(exePath);
        lock (_bindingsLock)
            return _policies.Any(x => x.ProcessName.Equals(exeName, StringComparison.OrdinalIgnoreCase) ||
                x.ProcessName.Equals(nameWithoutExt, StringComparison.OrdinalIgnoreCase));
    }

    private RoutingDecision ResolveRouting(string? exePath, IPAddress destination)
    {
        // Precedence is LAN bypass > individual rule > active group > Windows
        // passthrough. Directly reachable LAN traffic never enters WAN selection.
        if (_localDestinations.IsLocalDestination(destination))
            return new(false, null, null, WanHealthState.Checking, false, false, "LOCAL_BYPASS");
        if (string.IsNullOrEmpty(exePath)) return new(false, null, null, WanHealthState.Checking, false, false, "NO_POLICY");
        string exeName = Path.GetFileName(exePath);
        string nameWithoutExt = Path.GetFileNameWithoutExtension(exePath);
        lock (_bindingsLock)
        {
            bool Matches(ProcessRoutingPolicy candidate) =>
                candidate.ProcessName.Equals(exeName, StringComparison.OrdinalIgnoreCase) ||
                candidate.ProcessName.Equals(nameWithoutExt, StringComparison.OrdinalIgnoreCase);
            var policy = _policies.FirstOrDefault(x => Matches(x) &&
                x.Match.Equals("INDIVIDUAL", StringComparison.OrdinalIgnoreCase))
                ?? _policies.FirstOrDefault(Matches);
            if (policy is null) return new(false, null, null, WanHealthState.Checking, false, false, "NO_POLICY");
            // Health is sampled for each new flow. STRICT never uses the secondary;
            // FAILOVER can use it only after health has confirmed it UP.
            var primaryState = _health?.GetState(policy.PrimaryWan) ?? WanHealthState.Checking;
            if (primaryState == WanHealthState.Up && _wanBindings.TryGetValue(policy.PrimaryWan, out var primary))
            {
                bool failback = _lastSelectedWan.TryGetValue(policy.ProcessName, out string? previous) &&
                    !previous.Equals(policy.PrimaryWan, StringComparison.OrdinalIgnoreCase);
                _lastSelectedWan[policy.ProcessName] = policy.PrimaryWan;
                if (failback) Interlocked.Increment(ref _failbacks);
                return new(true, ForProcess(primary, policy.ProcessName), policy, primaryState,
                    false, failback, failback ? "PRIMARY_RESTORED" : "PRIMARY_UP");
            }

            if (policy.Mode == FailoverMode.Strict)
            {
                Interlocked.Increment(ref _strictFailures);
                return new(true, null, policy, primaryState, false, false, "PRIMARY_NOT_UP");
            }

            string secondaryWan = policy.PrimaryWan.Equals("WAN1", StringComparison.OrdinalIgnoreCase) ? "WAN2" : "WAN1";
            if (_health?.GetState(secondaryWan) == WanHealthState.Up && _wanBindings.TryGetValue(secondaryWan, out var secondary))
            {
                _lastSelectedWan[policy.ProcessName] = secondaryWan;
                Interlocked.Increment(ref _failovers);
                return new(true, ForProcess(secondary, policy.ProcessName), policy, primaryState,
                    true, false, "PRIMARY_DOWN");
            }
            return new(true, null, policy, primaryState, false, false, "NO_HEALTHY_WAN");
        }
    }

    private static BindingMapping ForProcess(BindingMapping wan, string processName) => new()
    {
        ProcessName = processName,
        InterfaceName = wan.InterfaceName,
        InterfaceIndex = wan.InterfaceIndex,
        LogicalWan = wan.LogicalWan,
        InterfaceIpv4 = wan.InterfaceIpv4,
        GatewayIpv4 = wan.GatewayIpv4,
        RoutingMethod = RoutingMethod.Transparent,
        IsActive = true
    };

    private static void LogPolicyDecision(long generation, string protocol, int pid, string? executable,
        PacketTuple tuple, RoutingDecision decision, string result)
    {
        string process = executable is null ? "UNKNOWN" : Path.GetFileName(executable);
        string selected = decision.Binding is null
            ? "NONE"
            : $"{decision.Binding.LogicalWan}/{decision.Binding.InterfaceName}[{decision.Binding.InterfaceIndex}]";
        Console.WriteLine($"ROUTING | {DateTimeOffset.Now:O} | FLOW={generation} | PROCESS={process} | PID={pid} | PROTOCOL={protocol} | {tuple.Source}:{tuple.SourcePort}->{tuple.Destination}:{tuple.DestinationPort} | MATCH={decision.Policy!.Match} | PRIMARY={decision.Policy.PrimaryWan} | PRIMARY_STATE={decision.PrimaryState.ToString().ToUpperInvariant()} | MODE={decision.Policy.Mode.ToString().ToUpperInvariant()} | SELECTED={selected} | FAILOVER={(decision.Failover ? "YES" : "NO")} | FAILBACK={(decision.Failback ? "YES" : "NO")} | REASON={decision.Reason} | RESULT={result}");
    }

    private void PrintSessionSummary()
    {
        var records = _records.Values.ToArray();
        Console.WriteLine($"INFO SESSION SUMMARY\nStarted: {_sessionStarted:O}\nStopped: {DateTimeOffset.Now:O}\nFlows observed: {records.Length}\nFlows routed: {records.Count(x => x.Connected)}\nFlows failed: {records.Count(x => x.Failed)}\nFlows owner unknown: {Interlocked.Read(ref _ownerUnknown)}\nOverrides: {records.Count(x => x.Connected && x.Override)}\nSame-WAN routes: {records.Count(x => x.Connected && !x.Override)}");
        foreach (var group in records.Where(x => x.Connected).GroupBy(x => (x.Wan, x.Interface)))
            Console.WriteLine($"INFO SESSION WAN {group.Key.Wan} / {group.Key.Interface}: {group.Count()} flows");
        Console.WriteLine($"INFO SESSION FAILOVER SUMMARY: failovers={Interlocked.Read(ref _failovers)}, failbacks={Interlocked.Read(ref _failbacks)}, strict failures={Interlocked.Read(ref _strictFailures)}");
        Console.WriteLine("INFO SESSION Cleanup: handles and flow mappings closed");
    }

    public void Dispose()
    {
        Stop();
    }
}
