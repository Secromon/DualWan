using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetBinder.Service.NativeInterop;

namespace NetBinder.Service.Services;

/// <summary>
/// A raw TCP Relay Server that listens on a loopback port.
/// It intercepts client connections rewritten by WinDivert, queries a NAT table
/// to find their original remote destination and target interface, binds a new outbound
/// socket to the target adapter's IP, and bidirectionally proxies all bytes.
/// </summary>
public class TransparentProxy : IDisposable
{
    private Socket? _listenSocket;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;
    private readonly List<Task> _activeConnections = [];
    private readonly object _lock = new();
    private readonly ApplicationTrafficAccumulator? _traffic;

    /// <summary>
    /// Delegate to lookup the original destination and target adapter index for a given client port.
    /// </summary>
    private readonly Func<IPAddress, ushort, (IPEndPoint OriginalRemoteEndpoint, int InterfaceIndex, long Generation, FlowRecord Report)?> _natLookup;

    /// <summary>
    /// The port this relay server is listening on.
    /// </summary>
    public int ListenPort { get; private set; }

    public bool IsRunning => _listenSocket != null && _cts != null && !_cts.IsCancellationRequested;

    public TransparentProxy(Func<IPAddress, ushort, (IPEndPoint, int, long, FlowRecord)?> natLookup,
        ApplicationTrafficAccumulator? traffic = null)
    {
        _natLookup = natLookup ?? throw new ArgumentNullException(nameof(natLookup));
        _traffic = traffic;
    }

    /// <summary>
    /// Starts the transparent proxy listener on a dynamic port on all interfaces.
    /// </summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (IsRunning) return true;

            try
            {
                _cts = new CancellationTokenSource();
                _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                
                _listenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _listenSocket.Listen(100);

                var localEp = (IPEndPoint)_listenSocket.LocalEndPoint!;
                ListenPort = localEp.Port;

                _acceptTask = AcceptLoopAsync(_cts.Token);
                Console.WriteLine($"[TransparentProxy] Started listening on 127.0.0.1:{ListenPort}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransparentProxy] Failed to start: {ex.Message}");
                Stop();
                return false;
            }
        }
    }

    /// <summary>
    /// Stops the transparent proxy listener and cancels all active connections.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();

            try
            {
                _listenSocket?.Close();
            }
            catch (Exception ex) { Console.WriteLine($"[TransparentProxy] Listener close failed: {ex.Message}"); }
            _listenSocket = null;

            // Wait for accept loop to exit
            if (_acceptTask != null)
            {
                try { _acceptTask.Wait(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) { Console.WriteLine($"[TransparentProxy] Accept loop stop failed: {ex.Message}"); }
                _acceptTask = null;
            }

            // Copy list to avoid concurrent modification issues
            List<Task> connectionsToWait;
            lock (_activeConnections)
            {
                connectionsToWait = new List<Task>(_activeConnections);
            }

            try
            {
                Task.WhenAll(connectionsToWait).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) { Console.WriteLine($"[TransparentProxy] Connection stop failed: {ex.Message}"); }

            lock (_activeConnections)
            {
                _activeConnections.Clear();
            }

            _cts?.Dispose();
            _cts = null;
            Console.WriteLine("[TransparentProxy] Stopped.");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listenSocket = _listenSocket;
        if (listenSocket == null) return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket clientSocket = await listenSocket.AcceptAsync(ct);
                var clientEp = (IPEndPoint)clientSocket.RemoteEndPoint!;
                var localEp = (IPEndPoint)clientSocket.LocalEndPoint!;
                ushort clientPort = (ushort)clientEp.Port;
                Console.WriteLine($"[TransparentProxy] Accepted connection from {clientEp} on {localEp}, client port={clientPort}");

                var connTask = Task.Run(() => HandleClientAsync(clientSocket, clientPort, ct), ct);
                
                lock (_activeConnections)
                {
                    _activeConnections.Add(connTask);
                }

                // Cleanup completed connection tasks
                _ = connTask.ContinueWith(t =>
                {
                    lock (_activeConnections)
                    {
                        _activeConnections.Remove(t);
                    }
                }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { Console.WriteLine("[TransparentProxy] Accept loop cancelled"); }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[TransparentProxy] Error in accept loop: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(Socket clientSocket, ushort clientPort, CancellationToken ct)
    {
        FlowRecord? flowReport = null;
        try
        {
            // Lookup original destination and target interface for the connection
            var clientIp = ((IPEndPoint)clientSocket.RemoteEndPoint!).Address;
            var mapping = _natLookup(clientIp, clientPort);
            if (mapping == null)
            {
                Console.WriteLine($"[TransparentProxy] Warning: No NAT mapping found for client port {clientPort}. Closing connection.");
                clientSocket.Close();
                return;
            }

            var (originalRemoteEp, interfaceIndex, generation, report) = mapping.Value;
            flowReport = report;
            var counter = _traffic?.CreateFlow(report.Executable, report.Wan);
            report.RelayAccepted();
            
            // Get local IP address associated with the target interface index
            string? targetIpStr = null;
            try
            {
                var adapters = IphlpApiWrapper.GetNetworkAdapters();
                foreach (var adapter in adapters)
                {
                    if (adapter.InterfaceIndex == interfaceIndex)
                    {
                        targetIpStr = adapter.IpAddress;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransparentProxy] Error retrieving IP address for interface index {interfaceIndex}: {ex.Message}");
            }

            if (string.IsNullOrEmpty(targetIpStr))
            {
                Console.WriteLine($"[TransparentProxy] Error: Could not find IP address for target interface index {interfaceIndex}. Closing connection.");
                report.Fail("interface lookup", detail: $"No IPv4 address for interface {interfaceIndex}");
                clientSocket.Close();
                return;
            }

            // Create outbound socket and bind it to the target interface's IP
            IPAddress targetIp = IPAddress.Parse(targetIpStr);
            using var targetSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            
            try
            {
                // Try setting IP_UNICAST_IF (31) to force traffic through the interface
                int indexNetOrder = IPAddress.HostToNetworkOrder(interfaceIndex);
                targetSocket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, indexNetOrder);
                report.InterfaceApplied();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransparentProxy] FLOW {generation} WAN BIND FAILED: {ex.Message}");
                report.Fail("IP_UNICAST_IF", ex);
                clientSocket.Close();
                return;
            }

            try
            {
                targetSocket.Bind(new IPEndPoint(targetIp, 0));
                report.Bound((IPEndPoint)targetSocket.LocalEndPoint!);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransparentProxy] Error: Failed to bind outbound socket to IP {targetIpStr}: {ex.Message}");
                report.Fail("bind", ex);
                clientSocket.Close();
                return;
            }

            // Connect to the original destination
            try
            {
                report.ConnectAttempted();
                await targetSocket.ConnectAsync(originalRemoteEp, ct);
                report.ConnectSucceeded((IPEndPoint)targetSocket.LocalEndPoint!);
                counter?.TcpConnected();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransparentProxy] Error: Failed to connect to original destination {originalRemoteEp} via interface index {interfaceIndex}: {ex.Message}");
                report.Fail("ConnectAsync", ex);
                clientSocket.Close();
                return;
            }

            // Bidirectionally relay data
            await RelayDataAsync(clientSocket, targetSocket, counter, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TransparentProxy] Error handling client connection: {ex.Message}");
            flowReport?.Fail("relay handling", ex);
        }
        finally
        {
            try { clientSocket.Close(); }
            catch (Exception ex) { Console.WriteLine($"[TransparentProxy] Client close failed: {ex.Message}"); }
        }
    }

    private static async Task RelayDataAsync(Socket client, Socket target,
        ApplicationTrafficAccumulator.FlowCounter? counter, CancellationToken ct)
    {
        var clientToTarget = RelayOneDirectionAsync(client, target, counter is null ? null : counter.Upload, ct);
        var targetToClient = RelayOneDirectionAsync(target, client, counter is null ? null : counter.Download, ct);
        await Task.WhenAny(clientToTarget, targetToClient);

        try { client.Close(); }
        catch (Exception ex) { Console.WriteLine($"[TransparentProxy] Client relay close failed: {ex.Message}"); }
        try { target.Close(); }
        catch (Exception ex) { Console.WriteLine($"[TransparentProxy] WAN relay close failed: {ex.Message}"); }
    }

    private static async Task RelayOneDirectionAsync(Socket from, Socket to, Action<int>? counted, CancellationToken ct)
    {
        var buffer = new byte[8192];
        // The delegate is created once per direction, never per payload chunk.
        ValueTask<int> Send(ReadOnlyMemory<byte> data, CancellationToken token) => to.SendAsync(data, token);
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<int>> sender = Send;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await from.ReceiveAsync(buffer.AsMemory(), ct);
                if (read == 0) break;

                await SendFullyAsync(buffer.AsMemory(0, read), sender, counted, ct);
            }
        }
        catch (OperationCanceledException) { Console.WriteLine("[TransparentProxy] Relay cancelled"); }
        catch (Exception ex) { Console.WriteLine($"[TransparentProxy] Relay failed: {ex.Message}"); }
    }

    /// <summary>Send all relay payload, counting only bytes accepted by the socket.</summary>
    public static async Task SendFullyAsync(ReadOnlyMemory<byte> data,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<int>> send,
        Action<int>? counted, CancellationToken ct = default)
    {
        while (!data.IsEmpty)
        {
            int sent = await send(data, ct);
            if (sent <= 0 || sent > data.Length)
                throw new IOException("TCP relay send made no valid progress.");
            try { counted?.Invoke(sent); }
            catch { /* Statistics must never affect relay delivery. */ }
            data = data[sent..];
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Stop();
        }
    }
}
