using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace DualWAN.ProcessAttributionPrototype;

internal enum Confidence { Confident, Unknown, Ambiguous }
internal readonly record struct TcpKey(IPAddress LocalIp, int LocalPort, IPAddress RemoteIp, int RemotePort);
internal readonly record struct UdpKey(IPAddress LocalIp, int LocalPort, IPAddress RemoteIp);
internal readonly record struct TcpOwnerRow(TcpKey Key, uint State, int Pid);
internal readonly record struct UdpOwnerRow(IPAddress LocalIp, int LocalPort, int Pid);
internal readonly record struct Identity(int Pid, string Path, string Name, long StartTicks);
internal readonly record struct AttributionResult(Confidence Status, int Pid, string? ExecutablePath,
    string? ProcessName, long StartTicks, string Reason)
{
    public static AttributionResult Unknown(string why) => new(Confidence.Unknown, 0, null, null, 0, why);
    public static AttributionResult Ambiguous(string why) => new(Confidence.Ambiguous, 0, null, null, 0, why);
}

internal interface IProcessIdentity
{
    Identity? Resolve(int pid);
}

internal sealed class WindowsProcessIdentity : IProcessIdentity
{
    public Identity? Resolve(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            // Start time guards a future cache against PID reuse; unresolved path fails open.
            long start = process.StartTime.ToUniversalTime().Ticks;
            string? path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) return null;
            return new Identity(pid, Path.GetFullPath(path), process.ProcessName, start);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
        { return null; }
    }
}

internal sealed class AttributionResolver(IProcessIdentity identities)
{
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
        { "svchost", "dllhost", "rundll32" };

    public AttributionResult ResolveTcpOwner(TcpKey key, IEnumerable<TcpOwnerRow> rows)
    {
        // LISTEN has no meaningful remote tuple. TIME_WAIT may no longer have a live owner.
        var matches = rows.Where(x => x.Key == key && x.State is >= 3 and <= 9).ToArray();
        return ResolveCandidates(matches.Select(x => x.Pid).ToArray(), "TCP full tuple");
    }

    public AttributionResult ResolveUdpOwner(UdpKey key, IEnumerable<UdpOwnerRow> rows)
    {
        // The Windows UDP owner table has no remote address/port. A wildcard and a
        // specific bind on the same port are both candidates; never guess between them.
        var matches = rows.Where(x => x.LocalPort == key.LocalPort &&
            (x.LocalIp.Equals(key.LocalIp) || x.LocalIp.Equals(IPAddress.Any))).ToArray();
        return ResolveCandidates(matches.Select(x => x.Pid).ToArray(),
            "UDP local endpoint only; remote endpoint unavailable");
    }

    private AttributionResult ResolveCandidates(int[] pids, string reason)
    {
        if (pids.Length == 0) return AttributionResult.Unknown("No ownership row");
        if (pids.Length != 1) return AttributionResult.Ambiguous($"{pids.Length} matching ownership rows");
        var identity = identities.Resolve(pids[0]);
        if (identity is null) return AttributionResult.Unknown("PID/path/start-time unavailable");
        if (SharedHosts.Contains(identity.Value.Name))
            return AttributionResult.Unknown("Shared host process; service owner unknown");
        return new AttributionResult(Confidence.Confident, identity.Value.Pid,
            identity.Value.Path, identity.Value.Name, identity.Value.StartTicks, reason);
    }
}

internal interface IFlowTableSource
{
    IReadOnlyList<TcpOwnerRow> Tcp();
    IReadOnlyList<UdpOwnerRow> Udp();
}

internal sealed class SafeAttributionLookup(IFlowTableSource tables, AttributionResolver resolver)
{
    public AttributionResult ResolveTcpOwner(TcpKey key)
    {
        try { return resolver.ResolveTcpOwner(key, tables.Tcp()); }
        catch (Exception ex) { return AttributionResult.Unknown($"TCP lookup failed: {ex.GetType().Name}"); }
    }
    public AttributionResult ResolveUdpOwner(UdpKey key)
    {
        try { return resolver.ResolveUdpOwner(key, tables.Udp()); }
        catch (Exception ex) { return AttributionResult.Unknown($"UDP lookup failed: {ex.GetType().Name}"); }
    }
}

internal sealed class OwnerTableReader : IFlowTableSource
{
    private const uint InsufficientBuffer = 122;
    private const uint AfInet = 2;
    private const uint TcpOwnerPidAll = 5;
    private const uint UdpOwnerPid = 1;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool order,
        uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, bool order,
        uint family, uint tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpNative
    {
        public uint State, LocalIp, LocalPort, RemoteIp, RemotePort, Pid;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UdpNative
    {
        public uint LocalIp, LocalPort, Pid;
    }

    private delegate uint ReadTable(IntPtr buffer, ref uint size);
    private static List<T> ReadRows<T>(ReadTable read) where T : struct
    {
        uint size = 0;
        uint code = read(IntPtr.Zero, ref size);
        if (code != InsufficientBuffer && code != 0)
            throw new InvalidOperationException($"Owner table size failed: {code}");
        // Bound allocation: a broken API response must never consume unbounded memory.
        if (size < 4 || size > 16 * 1024 * 1024) throw new InvalidOperationException("Invalid owner table size");
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            code = read(buffer, ref size);
            if (code != 0) throw new InvalidOperationException($"Owner table read failed: {code}");
            uint count = checked((uint)Marshal.ReadInt32(buffer));
            int rowSize = Marshal.SizeOf<T>();
            if ((ulong)count * (uint)rowSize + 4 > size) throw new InvalidOperationException("Owner table truncated");
            var rows = new List<T>(checked((int)count));
            for (int i = 0; i < count; i++)
                rows.Add(Marshal.PtrToStructure<T>(IntPtr.Add(buffer, 4 + i * rowSize)));
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int Port(uint value) => (int)(((value & 0xff) << 8) | ((value >> 8) & 0xff));
    private static IPAddress Ip(uint value) => new(value);

    public IReadOnlyList<TcpOwnerRow> Tcp() => ReadRows<TcpNative>((IntPtr p, ref uint n) =>
        GetExtendedTcpTable(p, ref n, false, AfInet, TcpOwnerPidAll, 0))
        .Select(x => new TcpOwnerRow(new TcpKey(Ip(x.LocalIp), Port(x.LocalPort),
            Ip(x.RemoteIp), Port(x.RemotePort)), x.State, checked((int)x.Pid))).ToArray();

    public IReadOnlyList<UdpOwnerRow> Udp() => ReadRows<UdpNative>((IntPtr p, ref uint n) =>
        GetExtendedUdpTable(p, ref n, false, AfInet, UdpOwnerPid, 0))
        .Select(x => new UdpOwnerRow(Ip(x.LocalIp), Port(x.LocalPort), checked((int)x.Pid))).ToArray();
}

// Memory-only cache prototype. A hit is valid only while its PID creation time
// and path still match; table changes may still occur within the short TTL.
internal sealed class BoundedAttributionCache(int capacity, TimeSpan ttl, IProcessIdentity identities)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (AttributionResult Result, DateTime Deadline)> _items = [];
    public int Count { get { lock (_gate) return _items.Count; } }
    // A cache hit is never sufficient by itself for a blocking decision. The
    // caller must revalidate current endpoint ownership or return UNKNOWN.
    public bool TryGet(string key, Func<AttributionResult, bool> ownerIsCurrent,
        out AttributionResult result)
    {
        lock (_gate)
        {
            result = AttributionResult.Unknown("Cache miss");
            if (!_items.TryGetValue(key, out var entry)) return false;
            if (entry.Deadline <= DateTime.UtcNow || entry.Result.Status != Confidence.Confident)
            { _items.Remove(key); return false; }
            var current = identities.Resolve(entry.Result.Pid);
            if (current is null || current.Value.StartTicks != entry.Result.StartTicks ||
                !string.Equals(current.Value.Path, entry.Result.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !ownerIsCurrent(entry.Result))
            { _items.Remove(key); return false; }
            result = entry.Result;
            return true;
        }
    }
    public void Put(string key, AttributionResult result)
    {
        if (result.Status != Confidence.Confident || capacity <= 0) return;
        lock (_gate)
        {
            foreach (var expired in _items.Where(x => x.Value.Deadline <= DateTime.UtcNow)
                         .Select(x => x.Key).ToArray()) _items.Remove(expired);
            if (!_items.ContainsKey(key) && _items.Count >= capacity)
                _items.Remove(_items.OrderBy(x => x.Value.Deadline).First().Key);
            _items[key] = (result, DateTime.UtcNow + ttl);
        }
    }
}
