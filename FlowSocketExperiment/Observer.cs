using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace DualWAN.FlowSocketExperiment;

internal enum Layer { Network = 0, Flow = 2, Socket = 3 }

internal readonly record struct EventRecord(Layer Layer, int Event, long NativeTimestamp,
    long ReceivedTick, int Pid, ulong EndpointId, ulong ParentEndpointId,
    byte Protocol, IPAddress LocalIp, ushort LocalPort, IPAddress RemoteIp,
    ushort RemotePort, bool Sniffed, bool Outbound)
{
    public string Tuple => $"{Protocol}:{LocalIp}:{LocalPort}>{RemoteIp}:{RemotePort}";
}

internal sealed class Observer : IDisposable
{
    private const ulong Sniff = 0x0001;
    private const ulong ReceiveOnly = 0x0004;
    private const ulong NoInstall = 0x0010;
    private readonly object _gate = new();
    private readonly List<EventRecord> _events = [];
    private readonly Layer _layer;
    private readonly string _filter;
    private Thread? _thread;
    private IntPtr _handle;
    private int _discarded;
    private string? _receiveError;
    private int _disposed;
    public int DroppedByBound => Volatile.Read(ref _discarded);
    public string? ReceiveError => _receiveError;

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern IntPtr WinDivertOpen([MarshalAs(UnmanagedType.LPStr)] string filter,
        int layer, short priority, ulong flags);
    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern bool WinDivertRecv(IntPtr handle, IntPtr packet, uint packetLength,
        out uint receivedLength, IntPtr address);
    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern bool WinDivertClose(IntPtr handle);
    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern bool WinDivertHelperFormatIPv6Address(IntPtr address,
        StringBuilder buffer, uint bufferLength);

    public Observer(Layer layer, string filter)
    {
        _layer = layer;
        _filter = filter;
    }

    public void Start()
    {
        if (_handle != IntPtr.Zero || Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("Observer already started/disposed");
        // Every layer uses copy-only observation. NO_INSTALL prevents this tool
        // from installing or starting a driver when DualWAN is not already using it.
        const ulong flags = Sniff | ReceiveOnly | NoInstall;
        _handle = WinDivertOpen(_filter, (int)_layer, -1000, flags);
        if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
        {
            _handle = IntPtr.Zero;
            throw new InvalidOperationException($"WinDivertOpen {_layer} failed: {Marshal.GetLastWin32Error()}");
        }
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"DualWAN-{_layer}-observer" };
        try { _thread.Start(); }
        catch { Dispose(); throw; }
    }

    public EventRecord[] Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }

    private void ReceiveLoop()
    {
        IntPtr address = Marshal.AllocHGlobal(80);
        IntPtr packet = _layer == Layer.Network ? Marshal.AllocHGlobal(65536) : IntPtr.Zero;
        try
        {
            while (Volatile.Read(ref _handle) != IntPtr.Zero)
            {
                var handle = Volatile.Read(ref _handle);
                if (handle == IntPtr.Zero) break;
                bool ok = WinDivertRecv(handle, packet, packet == IntPtr.Zero ? 0u : 65536u,
                    out uint size, address);
                if (!ok)
                {
                    if (Volatile.Read(ref _handle) != IntPtr.Zero)
                        _receiveError = $"WinDivertRecv {_layer}: {Marshal.GetLastWin32Error()}";
                    break;
                }
                long receivedTick = Stopwatch.GetTimestamp();
                var record = Parse(address, packet, size, receivedTick);
                if (record is null) continue;
                lock (_gate)
                {
                    if (_events.Count < 10000) _events.Add(record.Value);
                    else Interlocked.Increment(ref _discarded);
                }
            }
        }
        catch (Exception ex) { _receiveError = $"{_layer} observer error: {ex.GetType().Name}: {ex.Message}"; }
        finally
        {
            if (packet != IntPtr.Zero) Marshal.FreeHGlobal(packet);
            Marshal.FreeHGlobal(address);
            if (_receiveError is not null)
            {
                IntPtr failedHandle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (failedHandle != IntPtr.Zero && !WinDivertClose(failedHandle))
                    _receiveError += $"; close failed: {Marshal.GetLastWin32Error()}";
            }
        }
    }

    private EventRecord? Parse(IntPtr address, IntPtr packet, uint size, long receivedTick)
    {
        long timestamp = Marshal.ReadInt64(address, 0);
        uint flags = unchecked((uint)Marshal.ReadInt32(address, 8));
        int layer = (int)(flags & 0xff);
        if (layer != (int)_layer || (flags & (1u << 20)) != 0) return null; // IPv4 only
        int eventCode = (int)((flags >> 8) & 0xff);
        bool sniffed = (flags & (1u << 16)) != 0;
        bool outbound = (flags & (1u << 17)) != 0;
        if (_layer == Layer.Network)
        {
            if (size < 28 || Marshal.ReadByte(packet, 0) >> 4 != 4) return null;
            int ipLen = (Marshal.ReadByte(packet, 0) & 0xf) * 4;
            byte protocol = Marshal.ReadByte(packet, 9);
            if (protocol is not (6 or 17) || size < ipLen + (protocol == 6 ? 20 : 8)) return null;
            byte[] src = new byte[4], dst = new byte[4];
            Marshal.Copy(IntPtr.Add(packet, 12), src, 0, 4);
            Marshal.Copy(IntPtr.Add(packet, 16), dst, 0, 4);
            ushort srcPort = (ushort)((Marshal.ReadByte(packet, ipLen) << 8) | Marshal.ReadByte(packet, ipLen + 1));
            ushort dstPort = (ushort)((Marshal.ReadByte(packet, ipLen + 2) << 8) | Marshal.ReadByte(packet, ipLen + 3));
            if (protocol == 6)
            {
                byte tcpFlags = Marshal.ReadByte(packet, ipLen + 13);
                if ((tcpFlags & 0x02) == 0 || (tcpFlags & 0x10) != 0) return null; // first SYN only
            }
            return new EventRecord(_layer, eventCode, timestamp, receivedTick, 0, 0, 0, protocol,
                new IPAddress(src), srcPort, new IPAddress(dst), dstPort, sniffed, outbound);
        }
        return new EventRecord(_layer, eventCode, timestamp, receivedTick,
            Marshal.ReadInt32(address, 32),
            unchecked((ulong)Marshal.ReadInt64(address, 16)),
            unchecked((ulong)Marshal.ReadInt64(address, 24)),
            Marshal.ReadByte(address, 72),
            DecodeIp(IntPtr.Add(address, 36)), unchecked((ushort)Marshal.ReadInt16(address, 68)),
            DecodeIp(IntPtr.Add(address, 52)), unchecked((ushort)Marshal.ReadInt16(address, 70)),
            sniffed, outbound);
    }

    private static IPAddress DecodeIp(IntPtr raw)
    {
        var buffer = new StringBuilder(64);
        if (!WinDivertHelperFormatIPv6Address(raw, buffer, (uint)buffer.Capacity) ||
            !IPAddress.TryParse(buffer.ToString(), out var address))
            throw new InvalidOperationException("WinDivert IPv4-mapped address format failed");
        if (address.Equals(IPAddress.IPv6Any)) return IPAddress.Any;
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero && !WinDivertClose(handle))
            throw new InvalidOperationException($"WinDivertClose {_layer} failed: {Marshal.GetLastWin32Error()}");
        if (_thread is not null && _thread != Thread.CurrentThread &&
            !_thread.Join(TimeSpan.FromSeconds(3)))
            throw new TimeoutException($"{_layer} receive thread did not stop");
    }
}
