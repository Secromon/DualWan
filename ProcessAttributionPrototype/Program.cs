using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DualWAN.ProcessAttributionPrototype;

if (args.Length > 0 && args[0] == "--hold-tcp") { HoldTcp(); return; }
if (args.Length > 0 && args[0] == "--hold-udp") { HoldUdp(); return; }
if (args.Length > 1 && args[0] == "--hold-udp-reuse") { HoldUdp(int.Parse(args[1]), true); return; }

var tables = new OwnerTableReader();
var identities = new WindowsProcessIdentity();
var resolver = new AttributionResolver(identities);
var safe = new SafeAttributionLookup(tables, resolver);
var local = IPAddress.Loopback;
int pid = Environment.ProcessId;

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    Console.WriteLine($"PASS {label}");
}

using (var listener = new TcpListener(local, 0))
{
    listener.Start();
    using var client = new TcpClient(AddressFamily.InterNetwork);
    client.Connect(local, ((IPEndPoint)listener.LocalEndpoint).Port);
    using var accepted = listener.AcceptTcpClient();
    var key = new TcpKey(((IPEndPoint)client.Client.LocalEndPoint!).Address,
        ((IPEndPoint)client.Client.LocalEndPoint!).Port,
        ((IPEndPoint)client.Client.RemoteEndPoint!).Address,
        ((IPEndPoint)client.Client.RemoteEndPoint!).Port);
    var result = resolver.ResolveTcpOwner(key, tables.Tcp());
    Check(result.Status == Confidence.Confident && result.Pid == pid &&
          result.ExecutablePath == Environment.ProcessPath, "A1 TCP unique full tuple and path");
    Check(safe.ResolveTcpOwner(key).Status == Confidence.Confident,
        "A1 safe TCP API");
    Console.WriteLine($"A1 state={tables.Tcp().First(x => x.Key == key).State}");
}

int beforeClose = 0, afterClose = 0;
const int raceTrials = 30;
for (int i = 0; i < raceTrials; i++)
{
    using var listener = new TcpListener(local, 0);
    listener.Start();
    using var client = new TcpClient(AddressFamily.InterNetwork);
    client.Connect(local, ((IPEndPoint)listener.LocalEndpoint).Port);
    using var accepted = listener.AcceptTcpClient();
    var key = new TcpKey(((IPEndPoint)client.Client.LocalEndPoint!).Address,
        ((IPEndPoint)client.Client.LocalEndPoint!).Port,
        local, ((IPEndPoint)listener.LocalEndpoint).Port);
    if (resolver.ResolveTcpOwner(key, tables.Tcp()).Status == Confidence.Confident) beforeClose++;
    client.Close(); accepted.Close(); listener.Stop();
    if (resolver.ResolveTcpOwner(key, tables.Tcp()).Status == Confidence.Confident) afterClose++;
}
Console.WriteLine($"A2 TCP race: live={beforeClose}/{raceTrials}; after-close={afterClose}/{raceTrials}");

using (var child1 = Child.Start("--hold-tcp"))
using (var child2 = Child.Start("--hold-tcp"))
{
    var one = child1.TcpKey(); var two = child2.TcpKey();
    var a = resolver.ResolveTcpOwner(one, tables.Tcp());
    var b = resolver.ResolveTcpOwner(two, tables.Tcp());
    Check(a.Status == Confidence.Confident && b.Status == Confidence.Confident &&
          a.Pid == child1.Pid && b.Pid == child2.Pid && a.Pid != b.Pid, "A3 two TCP processes");
}

using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
{
    socket.Bind(new IPEndPoint(local, 0));
    var endpoint = (IPEndPoint)socket.LocalEndPoint!;
    using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    receiver.Bind(new IPEndPoint(local, 0));
    receiver.ReceiveTimeout = 1000;
    socket.SendTo([0x42], receiver.LocalEndPoint!);
    byte[] received = new byte[1];
    Check(receiver.Receive(received) == 1 && received[0] == 0x42,
        "A4 local UDP datagram delivered");
    var key = new UdpKey(endpoint.Address, endpoint.Port, local);
    var result = resolver.ResolveUdpOwner(key, tables.Udp());
    Check(result.Status == Confidence.Confident && result.Pid == pid, "A4 UDP unique loopback bind");
    Check(safe.ResolveUdpOwner(key).Status == Confidence.Confident,
        "A4 safe UDP API");
    var unknown = resolver.ResolveUdpOwner(new UdpKey(local, 0, local), []);
    Check(unknown.Status == Confidence.Unknown, "A9 no owner is UNKNOWN");
    var fake = new[] { new UdpOwnerRow(local, endpoint.Port, pid),
        new UdpOwnerRow(IPAddress.Any, endpoint.Port, pid + 1) };
    Check(resolver.ResolveUdpOwner(key, fake).Status == Confidence.Ambiguous,
        "A10 wildcard/specific or duplicate owner is AMBIGUOUS");
    Check(resolver.ResolveUdpOwner(new UdpKey(local, endpoint.Port,
              IPAddress.Parse("224.0.0.251")), tables.Udp()).Status == Confidence.Confident,
        "A7 synthetic mDNS destination: table owner unchanged");
    Check(resolver.ResolveUdpOwner(new UdpKey(local, endpoint.Port,
              IPAddress.Parse("239.255.255.250")), tables.Udp()).Status == Confidence.Confident,
        "A7 synthetic SSDP destination: table owner unchanged");
    Check(resolver.ResolveUdpOwner(new UdpKey(local, endpoint.Port,
              IPAddress.Broadcast), tables.Udp()).Status == Confidence.Confident,
        "A8 synthetic limited broadcast destination: table owner unchanged");
    Check(resolver.ResolveUdpOwner(new UdpKey(local, endpoint.Port,
              IPAddress.Parse("127.255.255.255")), tables.Udp()).Status == Confidence.Confident,
        "A8 synthetic subnet broadcast destination: table owner unchanged");
}

using (var child1 = Child.Start("--hold-udp"))
using (var child2 = Child.Start("--hold-udp"))
{
    var one = child1.UdpEndpoint(); var two = child2.UdpEndpoint();
    var rows = tables.Udp();
    var a = resolver.ResolveUdpOwner(new UdpKey(one.Address, one.Port, local), rows);
    var b = resolver.ResolveUdpOwner(new UdpKey(two.Address, two.Port, local), rows);
    Check(a.Status == Confidence.Confident && b.Status == Confidence.Confident &&
          a.Pid == child1.Pid && b.Pid == child2.Pid && a.Pid != b.Pid,
          "A5 two UDP processes on distinct ports");
}

using (var reused = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
{
    reused.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    reused.Bind(new IPEndPoint(local, 0));
    int port = ((IPEndPoint)reused.LocalEndPoint!).Port;
    try
    {
        using var child = Child.Start($"--hold-udp-reuse {port}");
        var candidates = tables.Udp().Where(x => x.LocalPort == port && x.LocalIp.Equals(local)).ToArray();
        var verdict = resolver.ResolveUdpOwner(new UdpKey(local, port, local), candidates);
        Check(candidates.Length >= 2 && verdict.Status == Confidence.Ambiguous,
            "A5 reused UDP port across processes is AMBIGUOUS");
    }
    catch (SocketException ex)
    { Console.WriteLine($"A5 same-port bind unavailable on this host: {ex.SocketErrorCode}"); }
}

using (var wildcard = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
{
    wildcard.Bind(new IPEndPoint(IPAddress.Any, 0));
    int port = ((IPEndPoint)wildcard.LocalEndPoint!).Port;
    var result = resolver.ResolveUdpOwner(new UdpKey(local, port, local), tables.Udp());
    Check(result.Status == Confidence.Confident && result.Pid == pid,
          "A6 UDP wildcard bind matches local address");
    var stopped = resolver.ResolveUdpOwner(new UdpKey(local, port, local),
        [new UdpOwnerRow(IPAddress.Any, port, int.MaxValue)]);
    Check(stopped.Status == Confidence.Unknown, "A11 exited PID/path is UNKNOWN");
}

int udpLive = 0, udpClosed = 0;
for (int i = 0; i < raceTrials; i++)
{
    using var shortSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    shortSocket.Bind(new IPEndPoint(local, 0));
    var endpoint = (IPEndPoint)shortSocket.LocalEndPoint!;
    var key = new UdpKey(local, endpoint.Port, local);
    if (resolver.ResolveUdpOwner(key, tables.Udp()).Status == Confidence.Confident) udpLive++;
    shortSocket.Close();
    if (resolver.ResolveUdpOwner(key, tables.Udp()).Status == Confidence.Confident) udpClosed++;
}
Console.WriteLine($"UDP close race: live={udpLive}/{raceTrials}; after-close={udpClosed}/{raceTrials}");

var identity = identities.Resolve(pid)!.Value;
var confident = new AttributionResult(Confidence.Confident, pid, identity.Path,
    identity.Name, identity.StartTicks, "test");
var cache = new BoundedAttributionCache(4, TimeSpan.FromMilliseconds(40), identities);
cache.Put("sample", confident);
Check(cache.TryGet("sample", _ => true, out _), "A12 cache initial hit");
Thread.Sleep(60);
Check(!cache.TryGet("sample", _ => true, out _) && cache.Count == 0, "A12 cache expiry");
var wrongStart = confident with { StartTicks = confident.StartTicks - 1 };
cache.Put("reuse", wrongStart);
Check(!cache.TryGet("reuse", _ => true, out _), "A13 PID reuse/start-time defense");
for (int i = 0; i < 8; i++) cache.Put($"bounded-{i}", confident);
Check(cache.Count <= 4, "A12 cache bounded size");
cache.Put("stale", confident);
Check(!cache.TryGet("stale", _ => false, out _),
    "A12 stale endpoint validator forces UNKNOWN");
var missingPath = new AttributionResolver(new MissingIdentity());
Check(missingPath.ResolveTcpOwner(new TcpKey(local, 1, local, 2),
    [new TcpOwnerRow(new TcpKey(local, 1, local, 2), 5, pid)]).Status == Confidence.Unknown,
    "A11 path resolution failure is UNKNOWN");
var failing = new SafeAttributionLookup(new ThrowingTables(), resolver);
Check(failing.ResolveTcpOwner(tcpKeyForFailure()).Status == Confidence.Unknown &&
      failing.ResolveUdpOwner(new UdpKey(local, 1, local)).Status == Confidence.Unknown,
      "A15 table errors fail open as UNKNOWN");

const int samples = 200;
Measure("TCP table snapshot", samples, () => tables.Tcp());
Measure("UDP table snapshot", samples, () => tables.Udp());
var tcpRows = tables.Tcp(); var udpRows = tables.Udp();
var tcpKey = new TcpKey(local, 1, local, 2);
var udpKey = new UdpKey(local, 1, local);
Measure("TCP lookup on snapshot miss", samples, () => resolver.ResolveTcpOwner(tcpKey, tcpRows));
Measure("UDP lookup on snapshot miss", samples, () => resolver.ResolveUdpOwner(udpKey, udpRows));
Measure("TCP full table+lookup miss", samples, () => resolver.ResolveTcpOwner(tcpKey, tables.Tcp()));
Measure("UDP full table+lookup miss", samples, () => resolver.ResolveUdpOwner(udpKey, tables.Udp()));
Measure("path/start-time resolution", samples, () => identities.Resolve(pid));
var perfCache = new BoundedAttributionCache(4, TimeSpan.FromSeconds(5), identities);
perfCache.Put("perf", confident);
Measure("cache hit, synthetic ownership validator", samples,
    () => perfCache.TryGet("perf", _ => true, out _));
Measure("cache miss", samples, () => perfCache.TryGet("absent", _ => true, out _));
Console.WriteLine("PROTOTYPE PASS; no WinDivert handle opened, no production Service change");

TcpKey tcpKeyForFailure() => new(local, 1, local, 2);

static void Measure(string name, int count, Action action)
{
    var watch = Stopwatch.StartNew();
    for (int i = 0; i < count; i++) action();
    watch.Stop();
    Console.WriteLine($"PERF {name}: {watch.Elapsed.TotalMilliseconds / count:F4} ms/op over {count} iterations");
}

static void HoldTcp()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var client = new TcpClient(AddressFamily.InterNetwork);
    client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
    using var accepted = listener.AcceptTcpClient();
    var a = (IPEndPoint)client.Client.LocalEndPoint!;
    var b = (IPEndPoint)client.Client.RemoteEndPoint!;
    Console.WriteLine($"READY TCP {a.Address} {a.Port} {b.Address} {b.Port}");
    Console.Out.Flush();
    Console.ReadLine();
}

static void HoldUdp(int port = 0, bool reuse = false)
{
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    if (reuse) socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
    var a = (IPEndPoint)socket.LocalEndPoint!;
    Console.WriteLine($"READY UDP {a.Address} {a.Port}");
    Console.Out.Flush();
    Console.ReadLine();
}

internal sealed class MissingIdentity : IProcessIdentity
{
    public Identity? Resolve(int pid) => null;
}

internal sealed class ThrowingTables : IFlowTableSource
{
    public IReadOnlyList<TcpOwnerRow> Tcp() => throw new InvalidOperationException("synthetic table error");
    public IReadOnlyList<UdpOwnerRow> Udp() => throw new InvalidOperationException("synthetic table error");
}

internal sealed class Child : IDisposable
{
    private readonly Process _process;
    private readonly string[] _parts;
    public int Pid => _process.Id;
    private Child(Process process, string[] parts) { _process = process; _parts = parts; }
    public static Child Start(string mode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, mode)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        };
        var process = Process.Start(start) ?? throw new InvalidOperationException("Child failed to start");
        string line = process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("Child exited");
        var parts = line.Split(' ');
        if (parts.Length < 4 || parts[0] != "READY") throw new InvalidOperationException(line);
        return new Child(process, parts);
    }
    public TcpKey TcpKey() => new(IPAddress.Parse(_parts[2]), int.Parse(_parts[3]),
        IPAddress.Parse(_parts[4]), int.Parse(_parts[5]));
    public IPEndPoint UdpEndpoint() => new(IPAddress.Parse(_parts[2]), int.Parse(_parts[3]));
    public void Dispose()
    {
        try { _process.StandardInput.WriteLine(); }
        catch (IOException) { }
        if (!_process.WaitForExit(3000)) _process.Kill();
        _process.Dispose();
    }
}
