using System.Net;
using System.Net.NetworkInformation;
using NetBinder.Service.Services;

if (args.Length != 2 || !IPAddress.TryParse(args[0], out var source) || !int.TryParse(args[1], out var port))
{
    Console.Error.WriteLine("Usage: DualWAN-SocksProxy.exe <local-IP> <loopback-port>");
    return 2;
}
var nic = NetworkInterface.GetAllNetworkInterfaces()
    .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(source)));
if (nic is null) { Console.Error.WriteLine("IP is not assigned to a local interface."); return 2; }
var index = source.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
    ? nic.GetIPProperties().GetIPv4Properties()?.Index ?? 0
    : nic.GetIPProperties().GetIPv6Properties()?.Index ?? 0;
if (index <= 0) { Console.Error.WriteLine("Interface index unavailable."); return 2; }
using var proxy = new Socks5Proxy(index, nic.Name, port);
if (!proxy.Start()) return 1;
Console.WriteLine($"READY: {proxy.ProxyAddress} -> {nic.Name} [{index}] {source}; PID {Environment.ProcessId}");
using var done = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
done.Wait();
return 0;
