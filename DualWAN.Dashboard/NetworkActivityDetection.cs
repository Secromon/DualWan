using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DualWAN.Dashboard;

public sealed record ConnectionOwner(int Pid, bool Tcp, bool Udp);
public sealed record DetectedNetworkApplication(int Pid, string ProcessName, string DisplayName,
    string? ExecutablePath, bool TcpDetected, bool UdpDetected);
public sealed record NetworkScanProgress(int RemainingSeconds, int ApplicationsFound);

public interface IConnectionOwnerSource
{
    IReadOnlyList<ConnectionOwner> Snapshot();
}

public sealed class WindowsConnectionOwnerSource : IConnectionOwnerSource
{
    private const uint InsufficientBuffer = 122;
    private const uint TcpOwnerPidAll = 5;
    private const uint UdpOwnerPid = 1;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, bool order, uint family, uint tableClass, uint reserved);

    public IReadOnlyList<ConnectionOwner> Snapshot()
    {
        var owners = new Dictionary<int, ConnectionOwner>();
        foreach (uint family in new uint[] { 2, 23 })
        {
            ReadTable(true, family, family == 2 ? 24 : 56, family == 2 ? 20 : 52,
                family == 2 ? 0 : 48, owners);
            ReadTable(false, family, family == 2 ? 12 : 28, family == 2 ? 8 : 24, 0, owners);
        }
        return owners.Values.ToArray();
    }

    private static void ReadTable(bool tcp, uint family, int rowSize, int pidOffset, int stateOffset,
        Dictionary<int, ConnectionOwner> owners)
    {
        uint size = 0;
        uint code = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpOwnerPidAll, 0)
            : GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UdpOwnerPid, 0);
        if (code != InsufficientBuffer && code != 0 || size < 4 || size > 16 * 1024 * 1024) return;
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            code = tcp ? GetExtendedTcpTable(buffer, ref size, false, family, TcpOwnerPidAll, 0)
                : GetExtendedUdpTable(buffer, ref size, false, family, UdpOwnerPid, 0);
            if (code != 0 || size < 4) return;
            int count = Marshal.ReadInt32(buffer);
            if (count < 0 || (long)count * rowSize > size - 4) return;
            for (int index = 0; index < count; index++)
            {
                int offset = 4 + index * rowSize;
                if (tcp)
                {
                    int state = Marshal.ReadInt32(buffer, offset + stateOffset);
                    if (state is 1 or 2 or 12) continue; // closed, listening, deleted
                }
                int pid = Marshal.ReadInt32(buffer, offset + pidOffset);
                if (pid <= 0) continue;
                if (owners.TryGetValue(pid, out var previous))
                    owners[pid] = previous with { Tcp = previous.Tcp || tcp, Udp = previous.Udp || !tcp };
                else owners[pid] = new(pid, tcp, !tcp);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

public sealed class NetworkActivityDetector
{
    public static readonly int[] Durations = [10, 30, 60, 90, 120];
    public const int DefaultDuration = 10;
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
        { "System", "Idle", "csrss", "wininit", "services", "winlogon", "svchost" };
    private readonly IConnectionOwnerSource _source;

    public NetworkActivityDetector(IConnectionOwnerSource? source = null) => _source = source ?? new WindowsConnectionOwnerSource();

    public async Task<IReadOnlyList<DetectedNetworkApplication>> ScanAsync(int durationSeconds,
        CancellationToken cancellation = default, IProgress<NetworkScanProgress>? progress = null)
    {
        if (!Durations.Contains(durationSeconds)) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        var found = new Dictionary<string, DetectedNetworkApplication>(StringComparer.OrdinalIgnoreCase);
        var clock = Stopwatch.StartNew();
        while (!cancellation.IsCancellationRequested && clock.Elapsed < TimeSpan.FromSeconds(durationSeconds))
        {
            try
            {
                var owners = await Task.Run(_source.Snapshot, cancellation);
                foreach (var owner in owners)
                {
                    var app = Resolve(owner);
                    if (app is null) continue;
                    string key = app.ExecutablePath ?? app.ProcessName;
                    if (found.TryGetValue(key, out var previous))
                        found[key] = previous with { TcpDetected = previous.TcpDetected || app.TcpDetected,
                            UdpDetected = previous.UdpDetected || app.UdpDetected };
                    else found[key] = app;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Debug.WriteLine(ex); }
            progress?.Report(new(Math.Max(0, durationSeconds - (int)clock.Elapsed.TotalSeconds), found.Count));
            try { await Task.Delay(TimeSpan.FromMilliseconds(750), cancellation); }
            catch (OperationCanceledException) { break; }
        }
        progress?.Report(new(0, found.Count));
        return found.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static DetectedNetworkApplication? Resolve(ConnectionOwner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.Pid);
            string name = process.ProcessName;
            if (SystemNames.Contains(name)) return null;
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            { Debug.WriteLine(ex); }
            string executable = path is null ? name + ".exe" : Path.GetFileName(path);
            string display = executable;
            if (path is not null)
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    display = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription
                        : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName : executable;
                }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }
            return new(owner.Pid, executable, display, path, owner.Tcp, owner.Udp);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Debug.WriteLine(ex); return null; }
    }
}
