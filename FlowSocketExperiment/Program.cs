using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DualWAN.FlowSocketExperiment;

if (args.Length > 0 && args[0] == "--worker") { Worker.Run(); return; }
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

if (args.Length > 0 && args[0] == "--smoke")
{
    try
    {
        using var smokeFlow = new Observer(Layer.Flow, "true");
        using var smokeSocket = new Observer(Layer.Socket, "true");
        smokeFlow.Start(); Console.WriteLine("E1 FLOW observation handle OPEN");
        smokeSocket.Start(); Console.WriteLine("E2 SOCKET observation handle OPEN");
        smokeSocket.Dispose(); smokeFlow.Dispose();
        Console.WriteLine("E1/E2 handles CLOSED");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"OBSERVATION UNAVAILABLE: {ex.Message}");
        Environment.ExitCode = 2;
    }
    return;
}

var active = new List<Observer>();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    foreach (var observer in active.ToArray())
        try { observer.Dispose(); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
};

var ownPid = Environment.ProcessId;
var loop = IPAddress.Loopback;
using var oldReceiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
oldReceiver.Bind(new IPEndPoint(loop, 0));
using var oldSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
oldSocket.Bind(new IPEndPoint(loop, 0));
int oldPort = ((IPEndPoint)oldSocket.LocalEndPoint!).Port;
var oldDestination = oldReceiver.LocalEndPoint!;
oldSocket.SendTo([0x01], oldDestination); // flow and bind predate observers

Observer Open(Layer layer, string filter)
{
    var observer = new Observer(layer, filter);
    observer.Start();
    active.Add(observer);
    return observer;
}

Observer? flow = null, socket = null, network = null;
try
{
    flow = Open(Layer.Flow, "true");
    socket = Open(Layer.Socket, "true");
    // NETWORK observes only loopback TCP SYN and UDP copies. SNIFF guarantees
    // that packet delivery is independent of this observer; no send API exists.
    network = Open(Layer.Network, "loopback and ip and (tcp or udp)");
    Console.WriteLine("OBSERVERS OPEN FLOW/SOCKET/NETWORK sniff+recv-only+no-install");
    var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    long memoryBefore = process.WorkingSet64;
    oldSocket.SendTo([0x02], oldDestination);

    var tcpTrials = new List<(int ClientPort, int ServerPort)>();
    for (int i = 0; i < 100; i++)
    {
        using var listener = new TcpListener(loop, 0);
        listener.Start();
        int serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient(AddressFamily.InterNetwork);
        client.Connect(loop, serverPort);
        using var accepted = listener.AcceptTcpClient();
        int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        tcpTrials.Add((clientPort, serverPort));
        if (i == 0)
        {
            Thread.Sleep(100);
            PrintVolume("one TCP", flow, socket, ownPid);
            EnsureSniffed(flow, socket, network);
        }
    }
    Thread.Sleep(250);
    PrintVolume("100 TCP", flow, socket, ownPid);
    var flowEvents = flow.Snapshot();
    var socketEvents = socket.Snapshot();
    var packetEvents = network.Snapshot();
    var timing = CountFirstSyn(tcpTrials, flowEvents, socketEvents, packetEvents, ownPid);
    Console.WriteLine($"E4 TCP first SYN runs={tcpTrials.Count} networkSeen={timing.NetworkSeen} " +
        $"FLOW-before={timing.FlowBefore} FLOW-after={timing.FlowAfter} FLOW-miss={timing.FlowMiss} " +
        $"SOCKET-before={timing.SocketBefore} SOCKET-after={timing.SocketAfter} SOCKET-miss={timing.SocketMiss}");
    Console.WriteLine($"E4 capture-timestamp delta (metadata minus SYN): FLOW {Stats(timing.FlowDeltaMs)}; " +
        $"SOCKET {Stats(timing.SocketDeltaMs)}");
    var delivery = CountDeliveryOrder(tcpTrials, flowEvents, socketEvents, packetEvents, ownPid);
    Console.WriteLine($"E4 user-mode delivery relative to SYN: FLOW-before={delivery.FlowBefore} " +
        $"FLOW-after={delivery.FlowAfter} FLOW-miss={delivery.FlowMiss}; " +
        $"SOCKET-before={delivery.SocketBefore} SOCKET-after={delivery.SocketAfter} " +
        $"SOCKET-miss={delivery.SocketMiss}; SOCKET delta {Stats(delivery.SocketDeltaMs)}");
    Console.WriteLine($"PERF receive latency: FLOW {Stats(ReceiveLatency(flowEvents))}; " +
        $"SOCKET {Stats(ReceiveLatency(socketEvents))}");
    Console.WriteLine($"EVENT TYPES 100 TCP own PID: FLOW established={flowEvents.Count(x => x.Pid == ownPid && x.Protocol == 6 && x.Event == 1)} " +
        $"deleted={flowEvents.Count(x => x.Pid == ownPid && x.Protocol == 6 && x.Event == 2)} " +
        $"SOCKET bind={socketEvents.Count(x => x.Pid == ownPid && x.Protocol == 6 && x.Event == 3)} " +
        $"connect={socketEvents.Count(x => x.Pid == ownPid && x.Protocol == 6 && x.Event == 4)} " +
        $"listen={socketEvents.Count(x => x.Pid == ownPid && x.Protocol == 6 && x.Event == 5)} " +
        $"accept={socketEvents.Count(x => x.Pid == ownPid && x.Protocol == 6 && x.Event == 6)} " +
        $"close={socketEvents.Count(x => x.Pid == ownPid && x.Protocol == 6 && x.Event == 7)}");

    int exitedChildPid;
    using (var childA = new WorkerClient())
    using (var childB = new WorkerClient())
    using (var listener = new TcpListener(loop, 0))
    {
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        childA.Command($"TCP {port}"); using var connectionA = listener.AcceptTcpClient();
        int aPort = childA.ClientPort();
        childB.Command($"TCP {port}"); using var connectionB = listener.AcceptTcpClient();
        int bPort = childB.ClientPort();
        Thread.Sleep(150);
        var events = flow.Snapshot().Concat(socket.Snapshot()).ToArray();
        Console.WriteLine($"E5 TCP children PID={childA.Pid}/{childB.Pid}; " +
            $"events={events.Count(x => x.Pid == childA.Pid && x.LocalPort == aPort)}/" +
            $"{events.Count(x => x.Pid == childB.Pid && x.LocalPort == bPort)} " +
            $"paths={ResolveIdentity(childA.Pid, 0).Status}/{ResolveIdentity(childB.Pid, 0).Status}");
        exitedChildPid = childA.Pid;
    }
    Console.WriteLine($"E13 exited process identity={ResolveIdentity(exitedChildPid, 0).Status}");

    using var sink = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    sink.Bind(new IPEndPoint(loop, 0));
    sink.ReceiveTimeout = 1000;
    using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    udp.Bind(new IPEndPoint(loop, 0));
    int udpPort = ((IPEndPoint)udp.LocalEndPoint!).Port;
    int udpFlowBefore = flow.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17);
    int udpSocketBefore = socket.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17);
    udp.SendTo([0x01], sink.LocalEndPoint!);
    Thread.Sleep(100);
    Console.WriteLine($"VOLUME one UDP: FLOW-delta={flow.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17) - udpFlowBefore} " +
        $"SOCKET-delta={socket.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17) - udpSocketBefore}");
    for (int i = 1; i < 100; i++) udp.SendTo([0x01], sink.LocalEndPoint!);
    byte[] sinkBuffer = new byte[8];
    int delivered = 0;
    for (int i = 0; i < 100; i++)
        if (sink.Receive(sinkBuffer) == 1) delivered++;
    Console.WriteLine($"E6 UDP loopback delivered={delivered}/100");
    Thread.Sleep(150);
    Console.WriteLine($"VOLUME 100 UDP sends: FLOW-delta={flow.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17) - udpFlowBefore} " +
        $"SOCKET-delta={socket.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17) - udpSocketBefore}");
    Console.WriteLine($"E6 UDP unique PID events FLOW={flow.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17 && x.LocalPort == udpPort)} " +
        $"SOCKET={socket.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17 && x.LocalPort == udpPort)}");

    using (var childA = new WorkerClient())
    using (var childB = new WorkerClient())
    {
        int port = childA.Bind("LOOP", 0);
        childB.Bind("LOOP", port);
        int sinkPort = ((IPEndPoint)sink.LocalEndPoint!).Port;
        childA.Send(loop.ToString(), sinkPort, 1);
        childB.Send(loop.ToString(), sinkPort, 1);
        int sharedDelivered = 0;
        for (int i = 0; i < 2; i++) if (sink.Receive(sinkBuffer) == 1) sharedDelivered++;
        Thread.Sleep(200);
        var sameTuple = flow.Snapshot().Where(x => x.Protocol == 17 && x.LocalPort == port &&
            x.RemotePort == sinkPort && (x.Pid == childA.Pid || x.Pid == childB.Pid)).ToArray();
        var binds = socket.Snapshot().Where(x => x.Event == 3 && x.Protocol == 17 && x.LocalPort == port &&
            (x.Pid == childA.Pid || x.Pid == childB.Pid)).ToArray();
        var packets = network.Snapshot().Where(x => x.Protocol == 17 && x.LocalPort == port && x.RemotePort == sinkPort).ToArray();
        Console.WriteLine($"E7 UDP reuse PID={childA.Pid}/{childB.Pid} bindEvents={binds.Length} " +
            $"FLOW-events={sameTuple.Length} FLOW-PIDs={string.Join(',', sameTuple.Select(x => x.Pid).Distinct())} " +
            $"NETWORK-packets={packets.Length} NETWORK-distinct-tuples={packets.Select(x => x.Tuple).Distinct().Count()} " +
            $"delivered={sharedDelivered}/2 paths={ResolveIdentity(childA.Pid, 0).Status}/{ResolveIdentity(childB.Pid, 0).Status}");
        using var secondSink = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        secondSink.Bind(new IPEndPoint(loop, 0));
        int secondPort = ((IPEndPoint)secondSink.LocalEndPoint!).Port;
        childA.Send(loop.ToString(), secondPort, 1);
        Thread.Sleep(100);
        var distinctFlow = flow.Snapshot().Where(x => x.Protocol == 17 && x.LocalPort == port &&
            x.RemotePort == secondPort && (x.Pid == childA.Pid || x.Pid == childB.Pid)).ToArray();
        Console.WriteLine($"E7 UDP reuse with distinct destination FLOW-events={distinctFlow.Length} " +
            $"PIDs={string.Join(',', distinctFlow.Select(x => x.Pid).Distinct())}");
    }

    using (var childA = new WorkerClient())
    using (var childB = new WorkerClient())
    {
        int port = childA.Bind("ANY", 0);
        childB.Bind("LOOP", port);
        childA.Send(loop.ToString(), ((IPEndPoint)sink.LocalEndPoint!).Port, 1);
        childB.Send(loop.ToString(), ((IPEndPoint)sink.LocalEndPoint!).Port, 1);
        Thread.Sleep(100);
        var events = socket.Snapshot().Where(x => x.Event == 3 && x.LocalPort == port &&
            (x.Pid == childA.Pid || x.Pid == childB.Pid)).ToArray();
        Console.WriteLine($"E8 wildcard/specific bind events={events.Length} " +
            $"addresses={string.Join(',', events.Select(x => x.LocalIp.ToString()).Distinct())}");
    }

    TestMulticast(flow, socket, ownPid);
    TestBroadcast(flow, socket, ownPid);

    var rapidPorts = new HashSet<int>();
    for (int i = 0; i < 30; i++)
    {
        using var rapid = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        rapid.Bind(new IPEndPoint(loop, 0));
        rapidPorts.Add(((IPEndPoint)rapid.LocalEndPoint!).Port);
        rapid.SendTo([0x52], sink.LocalEndPoint!);
    }
    Thread.Sleep(150);
    Console.WriteLine($"E6 rapid UDP sockets={rapidPorts.Count} " +
        $"FLOW-established={flow.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17 && x.Event == 1 && rapidPorts.Contains(x.LocalPort))} " +
        $"SOCKET-bind={socket.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17 && x.Event == 3 && rapidPorts.Contains(x.LocalPort))}");

    var identity = ResolveIdentity(ownPid, 0);
    Console.WriteLine($"E3/E15 identity={identity.Status} basename={identity.Name} " +
        $"pathMatches={string.Equals(identity.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)} " +
        $"startTicks={identity.StartTicks} sharedHostCheck={IsSharedHost("svchost")}");
    long staleEventTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 86400;
    Console.WriteLine($"E14 simulated older event identity={ResolveIdentity(ownPid, staleEventTimestamp).Status}");
    Console.WriteLine($"E11 pre-existing UDP port={oldPort} FLOW={flow.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17 && x.LocalPort == oldPort)} " +
        $"SOCKET-bind={socket.Snapshot().Count(x => x.Pid == ownPid && x.Event == 3 && x.LocalPort == oldPort)}");

    var cpu = process.TotalProcessorTime - cpuBefore;
    Console.WriteLine($"PERF processCPUms={cpu.TotalMilliseconds:F2} workingSetBefore={memoryBefore} workingSetAfter={process.WorkingSet64} " +
        $"FLOW-buffer={flow.Snapshot().Length} SOCKET-buffer={socket.Snapshot().Length} NETWORK-buffer={network.Snapshot().Length} " +
        $"discarded={flow.DroppedByBound}/{socket.DroppedByBound}/{network.DroppedByBound}");
    Console.WriteLine($"ERRORS FLOW={flow.ReceiveError ?? "none"} SOCKET={socket.ReceiveError ?? "none"} NETWORK={network.ReceiveError ?? "none"}");
    var lookupKey = tcpTrials[0];
    var watch = Stopwatch.StartNew();
    for (int i = 0; i < 1000; i++)
        _ = flowEvents.Where(x => x.Pid == ownPid && x.LocalPort == lookupKey.ClientPort &&
            x.RemotePort == lookupKey.ServerPort).ToArray();
    watch.Stop();
    Console.WriteLine($"PERF bounded event-snapshot correlation lookup={watch.Elapsed.TotalMilliseconds / 1000:F5} ms/op over 1000 iterations");

    network.Dispose(); socket.Dispose(); flow.Dispose(); active.Clear();
    flow = Open(Layer.Flow, "true"); socket = Open(Layer.Socket, "true");
    oldSocket.SendTo([0x03], oldDestination);
    Thread.Sleep(200);
    Console.WriteLine($"E12 observer restart, existing UDP port={oldPort} FLOW={flow.Snapshot().Count(x => x.Pid == ownPid && x.Protocol == 17 && x.LocalPort == oldPort)} " +
        $"SOCKET-bind={socket.Snapshot().Count(x => x.Pid == ownPid && x.Event == 3 && x.LocalPort == oldPort)}");
    Console.WriteLine("EXPERIMENT COMPLETE; no packet send/injection API was called");
}
finally
{
    foreach (var observer in active.ToArray())
        try { observer.Dispose(); }
        catch (Exception ex) { Console.Error.WriteLine($"OBSERVER CLEANUP: {ex.Message}"); }
}

static (int NetworkSeen, int FlowBefore, int FlowAfter, int FlowMiss,
    int SocketBefore, int SocketAfter, int SocketMiss, double[] FlowDeltaMs,
    double[] SocketDeltaMs) CountFirstSyn(
    List<(int ClientPort, int ServerPort)> trials, EventRecord[] flow,
    EventRecord[] socket, EventRecord[] packets, int pid)
{
    int seen = 0, fb = 0, fa = 0, fm = 0, sb = 0, sa = 0, sm = 0;
    var flowDelta = new List<double>(); var socketDelta = new List<double>();
    foreach (var (clientPort, serverPort) in trials)
    {
        var packet = packets.Where(x => x.Protocol == 6 && x.LocalPort == clientPort &&
            x.RemotePort == serverPort).OrderBy(x => x.NativeTimestamp).FirstOrDefault();
        if (packet == default) continue;
        seen++;
        var flowEvent = flow.Where(x => x.Pid == pid && x.Protocol == 6 &&
            x.LocalPort == clientPort && x.RemotePort == serverPort && x.Event == 1)
            .OrderBy(x => x.NativeTimestamp).FirstOrDefault();
        if (flowEvent == default) fm++;
        else
        {
            flowDelta.Add((flowEvent.NativeTimestamp - packet.NativeTimestamp) * 1000.0 / Stopwatch.Frequency);
            if (flowEvent.NativeTimestamp <= packet.NativeTimestamp) fb++; else fa++;
        }
        var socketEvent = socket.Where(x => x.Pid == pid && x.Protocol == 6 &&
            x.LocalPort == clientPort && x.RemotePort == serverPort && x.Event == 4)
            .OrderBy(x => x.NativeTimestamp).FirstOrDefault();
        if (socketEvent == default) sm++;
        else
        {
            socketDelta.Add((socketEvent.NativeTimestamp - packet.NativeTimestamp) * 1000.0 / Stopwatch.Frequency);
            if (socketEvent.NativeTimestamp <= packet.NativeTimestamp) sb++; else sa++;
        }
    }
    return (seen, fb, fa, fm, sb, sa, sm, flowDelta.ToArray(), socketDelta.ToArray());
}

static double[] ReceiveLatency(EventRecord[] events) => events
    .Select(x => (x.ReceivedTick - x.NativeTimestamp) * 1000.0 / Stopwatch.Frequency)
    .ToArray();

static (int FlowBefore, int FlowAfter, int FlowMiss, int SocketBefore, int SocketAfter,
    int SocketMiss, double[] SocketDeltaMs) CountDeliveryOrder(
    List<(int ClientPort, int ServerPort)> trials, EventRecord[] flow,
    EventRecord[] socket, EventRecord[] packets, int pid)
{
    int fb = 0, fa = 0, fm = 0, sb = 0, sa = 0, sm = 0;
    var socketDeltas = new List<double>();
    foreach (var (clientPort, serverPort) in trials)
    {
        var packet = packets.FirstOrDefault(x => x.Protocol == 6 && x.LocalPort == clientPort &&
            x.RemotePort == serverPort);
        if (packet == default) continue;
        var flowEvent = flow.FirstOrDefault(x => x.Pid == pid && x.Protocol == 6 && x.Event == 1 &&
            x.LocalPort == clientPort && x.RemotePort == serverPort);
        if (flowEvent == default) fm++;
        else if (flowEvent.ReceivedTick <= packet.ReceivedTick) fb++; else fa++;
        var socketEvent = socket.FirstOrDefault(x => x.Pid == pid && x.Protocol == 6 && x.Event == 4 &&
            x.LocalPort == clientPort && x.RemotePort == serverPort);
        if (socketEvent == default) sm++;
        else
        {
            socketDeltas.Add((socketEvent.ReceivedTick - packet.ReceivedTick) * 1000.0 / Stopwatch.Frequency);
            if (socketEvent.ReceivedTick <= packet.ReceivedTick) sb++; else sa++;
        }
    }
    return (fb, fa, fm, sb, sa, sm, socketDeltas.ToArray());
}

static string Stats(double[] values)
{
    if (values.Length == 0) return "none";
    Array.Sort(values);
    return $"n={values.Length} min={values[0]:F3}ms p50={values[values.Length / 2]:F3}ms " +
        $"p95={values[Math.Min(values.Length - 1, (int)(values.Length * 0.95))]:F3}ms max={values[^1]:F3}ms";
}

static void PrintVolume(string label, Observer flow, Observer socket, int pid)
{
    Console.WriteLine($"VOLUME {label}: FLOW={flow.Snapshot().Count(x => x.Pid == pid)} " +
        $"SOCKET={socket.Snapshot().Count(x => x.Pid == pid)}");
}

static void EnsureSniffed(params Observer[] observers)
{
    foreach (var observer in observers)
    {
        var events = observer.Snapshot();
        if (events.Any(x => !x.Sniffed))
            throw new InvalidOperationException("Observer received a non-sniffed event; stopping experiment");
    }
    Console.WriteLine("SAFETY all observed initial events have Sniffed=true");
}

static void TestMulticast(Observer flow, Observer socket, int pid)
{
    try
    {
        var group = IPAddress.Parse("239.255.77.77");
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        receiver.Bind(new IPEndPoint(IPAddress.Any, 0));
        receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
            new MulticastOption(group, IPAddress.Loopback));
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
            IPAddress.Loopback.GetAddressBytes());
        sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 0);
        sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)sender.LocalEndPoint!).Port;
        sender.SendTo([0x4d], new IPEndPoint(group, ((IPEndPoint)receiver.LocalEndPoint!).Port));
        Thread.Sleep(150);
        var flowEvents = flow.Snapshot().Where(x => x.Pid == pid && x.Protocol == 17 && x.LocalPort == port).ToArray();
        Console.WriteLine($"E9 multicast sent; FLOW={flowEvents.Length} " +
            $"SOCKET={socket.Snapshot().Count(x => x.Pid == pid && x.Protocol == 17 && x.LocalPort == port)} " +
            $"FLOW-remote={string.Join(',', flowEvents.Select(x => x.RemoteIp.ToString()).Distinct())}");
    }
    catch (SocketException ex) { Console.WriteLine($"E9 multicast unavailable: {ex.SocketErrorCode}"); }
}

static void TestBroadcast(Observer flow, Observer socket, int pid)
{
    try
    {
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.EnableBroadcast = true;
        sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)sender.LocalEndPoint!).Port;
        sender.SendTo([0x42], new IPEndPoint(IPAddress.Parse("127.255.255.255"), 37991));
        Thread.Sleep(150);
        var flowEvents = flow.Snapshot().Where(x => x.Pid == pid && x.Protocol == 17 && x.LocalPort == port).ToArray();
        Console.WriteLine($"E10 loopback subnet broadcast sent; FLOW={flowEvents.Length} " +
            $"SOCKET={socket.Snapshot().Count(x => x.Pid == pid && x.Protocol == 17 && x.LocalPort == port)} " +
            $"FLOW-remote={string.Join(',', flowEvents.Select(x => x.RemoteIp.ToString()).Distinct())}");
        try
        {
            sender.SendTo([0x42], new IPEndPoint(IPAddress.Broadcast, 37992));
            Thread.Sleep(100);
            Console.WriteLine($"E10 limited broadcast sent; FLOW-total={flow.Snapshot().Count(x => x.Pid == pid && x.Protocol == 17 && x.LocalPort == port)}");
        }
        catch (SocketException ex) { Console.WriteLine($"E10 limited broadcast unavailable: {ex.SocketErrorCode}"); }
    }
    catch (SocketException ex) { Console.WriteLine($"E10 loopback broadcast unavailable: {ex.SocketErrorCode}"); }
}

static bool IsSharedHost(string name) => name is "svchost" or "dllhost" or "rundll32";

static (string Status, string? Name, string? Path, long StartTicks) ResolveIdentity(int pid, long eventTimestamp)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        string name = process.ProcessName;
        if (IsSharedHost(name)) return ("UNSUITABLE_FOR_LAN_BLOCK", name, null, 0);
        long start = process.StartTime.ToUniversalTime().Ticks;
        if (process.MainModule?.FileName is not { Length: > 0 } path)
            return ("UNKNOWN", name, null, start);
        if (eventTimestamp > 0)
        {
            long nowQpc = Stopwatch.GetTimestamp();
            long elapsedTicks = checked((long)((nowQpc - eventTimestamp) *
                (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
            long eventUtcTicks = DateTime.UtcNow.Ticks - elapsedTicks;
            if (start > eventUtcTicks) return ("UNKNOWN_PID_REUSE", name, null, start);
        }
        return ("CONFIDENT_PROCESS", name, System.IO.Path.GetFullPath(path), start);
    }
    catch { return ("UNKNOWN", null, null, 0); }
}

internal sealed class WorkerClient : IDisposable
{
    private readonly Process _process;
    private string[] _response = [];
    public int Pid => _process.Id;
    public WorkerClient()
    {
        _process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--worker")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        }) ?? throw new InvalidOperationException("Worker start failed");
        _response = ReadResponse().Split(' ');
        if (_response[0] != "READY") throw new InvalidOperationException("Worker not ready");
    }
    public void Command(string command)
    {
        _process.StandardInput.WriteLine(command);
        _response = ReadResponse().Split(' ');
        if (_response[0] == "ERROR") throw new InvalidOperationException(string.Join(' ', _response));
    }
    private string ReadResponse() => _process.StandardOutput.ReadLineAsync()
        .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException("Worker exited without a response");
    public int Bind(string address, int port)
    { Command($"BIND {address} {port}"); return int.Parse(_response[2]); }
    public void Send(string ip, int port, int count) => Command($"SEND {ip} {port} {count}");
    public int ClientPort() => int.Parse(_response[2]);
    public void Dispose()
    {
        try { _process.StandardInput.WriteLine("EXIT"); } catch (IOException) { }
        if (!_process.WaitForExit(3000)) _process.Kill();
        _process.Dispose();
    }
}

internal static class Worker
{
    public static void Run()
    {
        var sockets = new List<IDisposable>();
        Socket? udp = null;
        Console.WriteLine("READY"); Console.Out.Flush();
        try
        {
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                try
                {
                    var parts = line.Split(' ');
                    if (parts[0] == "EXIT") break;
                    if (parts[0] == "TCP")
                    {
                        var client = new TcpClient(AddressFamily.InterNetwork);
                        client.Connect(IPAddress.Loopback, int.Parse(parts[1]));
                        sockets.Add(client);
                        Console.WriteLine($"TCP {Environment.ProcessId} {((IPEndPoint)client.Client.LocalEndPoint!).Port}");
                    }
                    else if (parts[0] == "BIND")
                    {
                        udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        udp.Bind(new IPEndPoint(parts[1] == "ANY" ? IPAddress.Any : IPAddress.Loopback,
                            int.Parse(parts[2])));
                        sockets.Add(udp);
                        Console.WriteLine($"BOUND {Environment.ProcessId} {((IPEndPoint)udp.LocalEndPoint!).Port}");
                    }
                    else if (parts[0] == "SEND" && udp is not null)
                    {
                        for (int i = 0; i < int.Parse(parts[3]); i++)
                            udp.SendTo([0x57], new IPEndPoint(IPAddress.Parse(parts[1]), int.Parse(parts[2])));
                        Console.WriteLine("SENT");
                    }
                    else Console.WriteLine("ERROR unknown command");
                }
                catch (Exception ex) { Console.WriteLine($"ERROR {ex.GetType().Name} {ex.Message}"); }
                Console.Out.Flush();
            }
        }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }
}
