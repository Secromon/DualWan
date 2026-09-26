using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;

namespace NetBinder.Service.Services;

/// <summary>A Windows IPv4 route snapshot entry; OnLink distinguishes direct delivery from a gateway.</summary>
public readonly record struct LocalIpv4Route(IPAddress Network, IPAddress Mask, int InterfaceIndex,
    bool OnLink, uint Metric = 0);

public interface ILocalIpv4RouteSource
{
    IReadOnlyList<LocalIpv4Route> ReadRoutes();
}

/// <summary>Reads active-adapter IPv4 routes from the Windows routing table.</summary>
public sealed class WindowsLocalIpv4RouteSource : ILocalIpv4RouteSource
{
    private const uint ErrorInsufficientBuffer = 122;
    private const uint DirectRoute = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow
    {
        public uint Destination, Mask, Policy, NextHop, InterfaceIndex, Type, Protocol, Age,
            NextHopAs, Metric1, Metric2, Metric3, Metric4, Metric5;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpForwardTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool ordered);

    public IReadOnlyList<LocalIpv4Route> ReadRoutes()
    {
        // GetIpForwardTable exposes network-order addresses in a native row layout;
        // copy them into managed IPAddress values before releasing its buffer.
        var active = new HashSet<int>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            try
            {
                int? index = nic.GetIPProperties().GetIPv4Properties()?.Index;
                if (index is > 0) active.Add(index.Value);
            }
            catch (NetworkInformationException) { /* Ignore an adapter that disappeared during enumeration. */ }
        }

        uint size = 0;
        uint result = GetIpForwardTable(IntPtr.Zero, ref size, false);
        if (result != ErrorInsufficientBuffer || size < sizeof(uint))
            throw new InvalidOperationException($"GetIpForwardTable size query failed: {result}");
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            result = GetIpForwardTable(buffer, ref size, false);
            if (result != 0) throw new InvalidOperationException($"GetIpForwardTable failed: {result}");
            int count = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibIpForwardRow>();
            if (count < 0 || (long)count * rowSize > size - sizeof(uint))
                throw new InvalidOperationException("Invalid IPv4 route table size.");
            var routes = new List<LocalIpv4Route>(count);
            for (int index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibIpForwardRow>(IntPtr.Add(buffer, sizeof(uint) + index * rowSize));
                if (row.InterfaceIndex > int.MaxValue || !active.Contains((int)row.InterfaceIndex)) continue;
                routes.Add(new(new IPAddress(row.Destination), new IPAddress(row.Mask),
                    (int)row.InterfaceIndex, row.Type == DirectRoute, row.Metric1));
            }
            return routes;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

/// <summary>
/// Keeps directly reachable IPv4 destinations on the Windows path before any
/// per-application Internet policy is evaluated by the redirector.
/// </summary>
public sealed class LocalDestinationClassifier(ILocalIpv4RouteSource source)
{
    private readonly record struct RouteMatch(uint Network, uint Mask, int Prefix, bool OnLink, uint Metric);
    private RouteMatch[] _routes = [];

    public void Refresh()
    {
        // Publish a complete immutable route snapshot to the packet thread. On
        // refresh failure, an empty snapshot avoids treating unknown routes as LAN.
        try
        {
            var routes = source.ReadRoutes()
                .Where(route => route.InterfaceIndex > 0 &&
                    route.Network.AddressFamily == AddressFamily.InterNetwork &&
                    route.Mask.AddressFamily == AddressFamily.InterNetwork)
                .Select(route =>
                {
                    uint mask = BinaryPrimitives.ReadUInt32BigEndian(route.Mask.GetAddressBytes());
                    uint network = BinaryPrimitives.ReadUInt32BigEndian(route.Network.GetAddressBytes());
                    return new RouteMatch(network & mask, mask, BitOperations.PopCount(mask), route.OnLink, route.Metric);
                }).ToArray();
            Volatile.Write(ref _routes, routes);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _routes, []);
            Console.WriteLine($"[RedirectorService] IPv4 local route refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public bool IsLocalDestination(IPAddress destination)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork) return false;
        Span<byte> bytes = stackalloc byte[4];
        destination.TryWriteBytes(bytes, out _);
        // These scopes must stay local even when no usable route-table row exists.
        if (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254) ||
            (bytes[0] >= 224 && bytes[0] <= 239) || destination.Equals(IPAddress.Broadcast)) return true;

        uint address = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        int bestPrefix = -1;
        uint bestMetric = uint.MaxValue;
        bool bestOnLink = false;
        // Windows selects the most-specific route, then lowest metric. A gateway
        // route at that precedence must not be mistaken for an on-link destination.
        foreach (RouteMatch route in Volatile.Read(ref _routes))
        {
            if ((address & route.Mask) != route.Network) continue;
            if (route.Prefix > bestPrefix || (route.Prefix == bestPrefix && route.Metric < bestMetric) ||
                (route.Prefix == bestPrefix && route.Metric == bestMetric && !route.OnLink))
            {
                bestPrefix = route.Prefix;
                bestMetric = route.Metric;
                bestOnLink = route.OnLink;
            }
        }
        return bestPrefix > 0 && bestOnLink; // A default route is never local.
    }
}
