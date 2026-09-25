using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetBinder.Shared.Models;

namespace NetBinder.Service.Services;

/// <summary>Per-flow UDP relay. Carries datagram payloads without inspecting them.</summary>
public sealed class UdpRelay : IDisposable
{
    public sealed class Session : IDisposable
    {
        private readonly Socket _loopback;
        private readonly Socket _outbound;
        private readonly CancellationTokenSource _cts = new();
        private int _sendLogged;
        private int _successLogged;
        private int _closed;

        public long Id { get; }
        public int Pid { get; }
        public string ProcessName { get; }
        public string Executable { get; }
        public BindingMapping Binding { get; }
        public IPEndPoint OriginalSource { get; }
        public IPEndPoint Destination { get; }
        public uint OriginalIfIdx { get; }
        public uint OriginalSubIfIdx { get; }
        public ushort RelayPort { get; }
        public ushort OutboundPort { get; }
        public IPAddress OutboundAddress { get; }
        public DateTimeOffset Created { get; } = DateTimeOffset.Now;
        public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
        public string State { get; private set; } = "CREATED";

        internal Session(long id, int pid, string executable, BindingMapping binding,
            IPEndPoint source, IPEndPoint destination, uint ifIdx, uint subIfIdx)
        {
            Id = id;
            Pid = pid;
            Executable = executable;
            ProcessName = Path.GetFileName(executable);
            Binding = binding;
            OriginalSource = source;
            Destination = destination;
            OriginalIfIdx = ifIdx;
            OriginalSubIfIdx = subIfIdx;
            OutboundAddress = IPAddress.Parse(binding.InterfaceIpv4);

            _loopback = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _loopback.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            RelayPort = checked((ushort)((IPEndPoint)_loopback.LocalEndPoint!).Port);

            _outbound = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _outbound.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31,
                IPAddress.HostToNetworkOrder(binding.InterfaceIndex));
            _outbound.Bind(new IPEndPoint(OutboundAddress, 0));
            OutboundPort = checked((ushort)((IPEndPoint)_outbound.LocalEndPoint!).Port);
            _outbound.Connect(destination);

            Console.WriteLine($"INFO UDP FLOW CREATED\nFlow ID: {Id}\nProcess: {ProcessName}\nPID: {Pid}\nExecutable: {Executable}\nProtocol: UDP\nOriginal source: {OriginalSource}\nDestination: {Destination}\nRule: {binding.ProcessName} -> {binding.LogicalWan}\nWAN: {binding.LogicalWan} / {binding.InterfaceName} [{binding.InterfaceIndex}]\nOutbound source: {OutboundAddress}:{OutboundPort}\nWAN OVERRIDE: {OverrideText}\nRelay: 127.0.0.1:{RelayPort}\nState: CREATED");

            _ = Task.Run(() => ForwardLoopAsync(_cts.Token));
            _ = Task.Run(() => ReplyLoopAsync(_cts.Token));
        }

        public bool Override => !OriginalSource.Address.Equals(OutboundAddress);
        private string OverrideText => Override ? "YES" : "NO";

        public void Touch()
        {
            LastActivityUtc = DateTime.UtcNow;
            if (State == "CREATED") State = "ACTIVE";
        }

        private async Task ForwardLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[65535];
            EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var result = await _loopback.ReceiveFromAsync(buffer, SocketFlags.None, sender, ct);
                    if (result.RemoteEndPoint is not IPEndPoint remote ||
                        remote.Port != OriginalSource.Port || !IPAddress.IsLoopback(remote.Address))
                    {
                        Console.WriteLine($"ERROR UDP FLOW {Id}: unexpected loopback sender {result.RemoteEndPoint}; datagram dropped");
                        continue;
                    }
                    Touch();
                    await _outbound.SendAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, ct);
                    if (Interlocked.Exchange(ref _sendLogged, 1) == 0)
                        Console.WriteLine($"DEBUG UDP FLOW {Id}: IP_UNICAST_IF={Binding.InterfaceIndex} SUCCESS; bind={OutboundAddress}:{OutboundPort} SUCCESS; send SUCCESS");
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Fail("send", ex); }
        }

        private async Task ReplyLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[65535];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int received = await _outbound.ReceiveAsync(buffer, SocketFlags.None, ct);
                    Touch();
                    await _loopback.SendToAsync(buffer.AsMemory(0, received), SocketFlags.None,
                        new IPEndPoint(IPAddress.Loopback, OriginalSource.Port), ct);
                    if (Interlocked.Exchange(ref _successLogged, 1) == 0)
                    {
                        State = "ACTIVE";
                        Console.WriteLine($"ROUTING | {Created:O} | FLOW={Id} | {ProcessName} | PID={Pid} | PROTOCOL=UDP | {OriginalSource}->{Destination} | RULE={Binding.ProcessName}->{Binding.LogicalWan} | WAN={Binding.LogicalWan}/{Binding.InterfaceName}[{Binding.InterfaceIndex}] | OUT={OutboundAddress} | OVERRIDE={OverrideText} | RESULT=SUCCESS");
                        Console.WriteLine($"INFO UDP FLOW ACTIVE: id={Id}, remote reply received and returned to PID={Pid}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Fail("receive/reply", ex); }
        }

        private void Fail(string operation, Exception ex)
        {
            if (_cts.IsCancellationRequested) return;
            State = "FAILED";
            int code = ex is SocketException socket ? socket.ErrorCode : ex.HResult;
            Console.WriteLine($"ERROR UDP FLOW FAILED\nFlow ID: {Id}\nProcess: {ProcessName}\nPID: {Pid}\nRule: {Binding.ProcessName} -> {Binding.LogicalWan}\nWAN: {Binding.LogicalWan} / {Binding.InterfaceName}\nOperation: {operation}\nError: {code}\nDescription: {ex.Message}\nResult: FAIL CLOSED");
            Console.WriteLine($"ROUTING | {Created:O} | FLOW={Id} | {ProcessName} | PID={Pid} | PROTOCOL=UDP | {OriginalSource}->{Destination} | RULE={Binding.ProcessName}->{Binding.LogicalWan} | WAN={Binding.LogicalWan}/{Binding.InterfaceName}[{Binding.InterfaceIndex}] | OUT={OutboundAddress} | OVERRIDE={OverrideText} | RESULT=FAILED_CLOSED");
        }

        public void Close(string reason, bool expired)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            State = expired ? "EXPIRED" : "CLOSED";
            _cts.Cancel();
            _loopback.Dispose();
            _outbound.Dispose();
            Console.WriteLine($"INFO UDP FLOW {State}: id={Id}, PID={Pid}, duration={(DateTimeOffset.Now - Created).TotalSeconds:F3}s, reason={reason}");
        }

        public void Dispose()
        {
            Close("relay disposed", false);
            _cts.Dispose();
        }
    }

    private readonly ConcurrentDictionary<ushort, Session> _byRelayPort = new();
    private readonly ConcurrentDictionary<(IPAddress, ushort), Session> _byOutbound = new();

    public Session? Create(long id, int pid, string executable, BindingMapping binding,
        IPEndPoint source, IPEndPoint destination, uint ifIdx, uint subIfIdx)
    {
        Session? session = null;
        try
        {
            session = new Session(id, pid, executable, binding, source, destination, ifIdx, subIfIdx);
            if (!_byRelayPort.TryAdd(session.RelayPort, session) ||
                !_byOutbound.TryAdd((session.OutboundAddress, session.OutboundPort), session))
            {
                Console.WriteLine($"ERROR UDP FLOW COLLISION: flow={id}; FAIL CLOSED");
                session.Dispose();
                return null;
            }
            return session;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            int code = ex is SocketException socket ? socket.ErrorCode : ex.HResult;
            Console.WriteLine($"ERROR UDP FLOW FAILED: flow={id}, PID={pid}, operation=create/bind/connect, error={code}, message={ex.Message}, result=FAIL CLOSED");
            return null;
        }
    }

    public bool TryGetRelay(ushort relayPort, out Session session) => _byRelayPort.TryGetValue(relayPort, out session!);
    public bool IsOutbound(IPAddress address, ushort port) => _byOutbound.ContainsKey((address, port));

    public void Remove(Session session, string reason, bool expired)
    {
        ((ICollection<KeyValuePair<ushort, Session>>)_byRelayPort).Remove(new(session.RelayPort, session));
        ((ICollection<KeyValuePair<(IPAddress, ushort), Session>>)_byOutbound)
            .Remove(new((session.OutboundAddress, session.OutboundPort), session));
        session.Close(reason, expired);
    }

    public void Dispose()
    {
        foreach (var session in _byRelayPort.Values) Remove(session, "engine stopped", false);
        _byRelayPort.Clear();
        _byOutbound.Clear();
    }
}
