using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("DualWAN-TestApp <http|https|tcp|udp> <url|host:port> [--source local-IP] [--message text] [--timeout seconds]");
    return;
}

var mode = args[0].ToLowerInvariant();
if (args.Length < 2 || !new[] { "http", "https", "tcp", "udp" }.Contains(mode))
    throw new ArgumentException("Protocollo o destinazione mancante.");
var target = args[1];
IPAddress? source = null;
string message = "DualWAN probe";
int timeoutSeconds = 8;
for (int i = 2; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length) throw new ArgumentException($"Valore mancante per {args[i]}");
    switch (args[i])
    {
        case "--source": source = IPAddress.Parse(args[i + 1]); break;
        case "--message": message = args[i + 1]; break;
        case "--timeout": timeoutSeconds = int.Parse(args[i + 1]); break;
        default: throw new ArgumentException($"Opzione sconosciuta: {args[i]}");
    }
}
if (timeoutSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

var nic = source is null ? null : NetworkInterface.GetAllNetworkInterfaces()
    .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(source)));
if (source is not null && nic is null) throw new ArgumentException("L'IP sorgente non appartiene a una scheda locale.");
var ifIndex = nic is null ? 0 : (source!.AddressFamily == AddressFamily.InterNetwork
    ? nic.GetIPProperties().GetIPv4Properties()?.Index ?? 0
    : nic.GetIPProperties().GetIPv6Properties()?.Index ?? 0);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
var sw = Stopwatch.StartNew();
Console.WriteLine($"PROCESS: {Environment.ProcessPath} PID: {Environment.ProcessId}");
Console.WriteLine($"SOURCE POLICY: {source?.ToString() ?? "default"}; INTERFACE: {nic?.Name ?? "default"}; INDEX: {ifIndex}");

Socket MakeSocket(AddressFamily family, SocketType type, ProtocolType protocol)
{
    var socket = new Socket(family, type, protocol);
    if (source is not null)
    {
        if (source.AddressFamily != family) throw new ArgumentException("Famiglia IP della destinazione incompatibile con --source.");
        if (ifIndex <= 0) throw new InvalidOperationException("InterfaceIndex non disponibile.");
        // IP_UNICAST_IF expects the IPv4 index in network byte order. IPv6 uses IPV6_UNICAST_IF.
        if (family == AddressFamily.InterNetwork)
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(ifIndex));
        else
            socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)31, ifIndex);
        socket.Bind(new IPEndPoint(source, 0));
    }
    return socket;
}

try
{
    if (mode is "http" or "https")
    {
        var uri = new Uri(target);
        if (uri.Scheme != mode) throw new ArgumentException("Lo schema URL non corrisponde al protocollo.");
        using var handler = new SocketsHttpHandler { UseProxy = false, ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            Exception? last = null;
            foreach (var address in addresses.Where(a => source is null || a.AddressFamily == source.AddressFamily))
            {
                var socket = MakeSocket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, ctx.DnsEndPoint.Port), ct);
                    Console.WriteLine($"TCP LOCAL: {socket.LocalEndPoint}; REMOTE: {socket.RemoteEndPoint}");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) { last = ex; socket.Dispose(); }
            }
            throw last ?? new SocketException((int)SocketError.HostUnreachable);
        }};
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        using var response = await client.GetAsync(uri, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Console.WriteLine($"HTTP STATUS: {(int)response.StatusCode}; BODY: {body[..Math.Min(200, body.Length)].Trim()}");
    }
    else
    {
        var sep = target.LastIndexOf(':');
        if (sep <= 0 || !int.TryParse(target[(sep + 1)..], out var port)) throw new ArgumentException("Destinazione attesa: host:port");
        var host = target[..sep].Trim('[', ']');
        var address = (await Dns.GetHostAddressesAsync(host, cts.Token)).First(a => source is null || a.AddressFamily == source.AddressFamily);
        var endpoint = new IPEndPoint(address, port);
        if (mode == "tcp")
        {
            using var socket = MakeSocket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(endpoint, cts.Token);
            Console.WriteLine($"TCP LOCAL: {socket.LocalEndPoint}; REMOTE: {socket.RemoteEndPoint}");
            if (message.Length > 0) await socket.SendAsync(Encoding.UTF8.GetBytes(message), SocketFlags.None, cts.Token);
            Console.WriteLine("CONNECTION: SUCCESS");
        }
        else
        {
            using var socket = MakeSocket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            byte[] payload;
            bool ntpProbe = port == 123 && message.Equals("ntp", StringComparison.OrdinalIgnoreCase);
            if (ntpProbe)
            {
                payload = new byte[48];
                payload[0] = 0x23; // NTP v4, client mode.
                Random.Shared.NextBytes(payload.AsSpan(40, 8)); // transmit timestamp nonce for reply correlation.
            }
            else
            {
                payload = Encoding.UTF8.GetBytes(message);
            }
            await socket.SendToAsync(payload, SocketFlags.None, endpoint, cts.Token);
            Console.WriteLine($"UDP LOCAL: {socket.LocalEndPoint}; REMOTE: {endpoint}; SENT: {payload.Length} bytes");
            try
            {
                var buffer = new byte[2048];
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), cts.Token);
                Console.WriteLine($"UDP RESPONSE: {result.ReceivedBytes} bytes from {result.RemoteEndPoint}");
                if (ntpProbe)
                {
                    int responseMode = buffer[0] & 0x07;
                    bool nonceMatches = result.ReceivedBytes >= 48 &&
                        buffer.AsSpan(24, 8).SequenceEqual(payload.AsSpan(40, 8));
                    if (responseMode is not (4 or 5) || !nonceMatches)
                        throw new InvalidDataException("Invalid NTP response or request nonce mismatch.");
                    Console.WriteLine("NTP RESPONSE: SUCCESS; request received and correlated reply returned");
                }
            }
            catch (OperationCanceledException) when (ntpProbe)
            {
                Console.WriteLine("UDP RESPONSE: timeout (send succeeded; delivery unconfirmed)");
                throw new TimeoutException("No correlated NTP reply was received before the timeout.");
            }
            catch (OperationCanceledException) { Console.WriteLine("UDP RESPONSE: timeout (send succeeded; delivery unconfirmed)"); }
        }
    }
    Console.WriteLine($"LATENCY: {sw.ElapsedMilliseconds} ms; RESULT: SUCCESS");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"LATENCY: {sw.ElapsedMilliseconds} ms; RESULT: FAIL; {ex.GetType().Name}: {ex.Message}");
    Environment.ExitCode = 1;
}
