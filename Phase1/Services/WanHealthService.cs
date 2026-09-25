using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetBinder.Shared.Models;

namespace NetBinder.Service.Services;

public enum WanHealthState { Checking, Up, Suspect, Down, Recovering }
public enum FailoverMode { Strict, Failover }
public sealed record ProcessRoutingPolicy(string ProcessName, string PrimaryWan, FailoverMode Mode,
    string Match = "INDIVIDUAL");
public sealed record HealthSettings(int IntervalSeconds, int FailureThreshold, int RecoverySuccessThreshold,
    IReadOnlyList<string> Targets);
public sealed record WanTelemetrySnapshot(WanHealthState State, double? RxBytesPerSecond,
    double? TxBytesPerSecond, long? RxTotalBytes, long? TxTotalBytes, double? LatencyCurrentMs,
    double? LatencyMinMs, double? LatencyAverageMs, double? LatencyMaxMs, long ProbeSuccesses,
    long ProbeFailures, double? ProbeLossPercent, long? UpSeconds);

public sealed class WanHealthService : IDisposable
{
    private sealed class Status
    {
        public readonly object Gate = new();
        public WanHealthState State = WanHealthState.Checking;
        public int Failures;
        public int Successes;
        public int UpEvents;
        public int DownEvents;
        public int Recoveries;
        public long ProbeSuccesses;
        public long ProbeFailures;
        public double? LatencyCurrentMs;
        public double? LatencyMinMs;
        public double? LatencyMaxMs;
        public double LatencySumMs;
        public long LatencySamples;
        public DateTimeOffset? UpSince;
        public long? LastRx;
        public long? LastTx;
        public DateTimeOffset? LastTrafficSample;
        public long RxTotal;
        public long TxTotal;
        public double? RxRate;
        public double? TxRate;
    }

    private readonly IReadOnlyDictionary<string, BindingMapping> _wans;
    private readonly HealthSettings _settings;
    private readonly bool _debug;
    private readonly ConcurrentDictionary<string, Status> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private Task? _healthWorker;
    private Task? _trafficWorker;

    public WanHealthService(IReadOnlyDictionary<string, BindingMapping> wans, HealthSettings settings, bool debug = false)
    {
        _wans = wans;
        _settings = settings;
        _debug = debug;
        foreach (var wan in wans.Keys) _status[wan] = new Status();
    }

    public async Task StartAsync()
    {
        SampleTraffic();
        await ProbeCycleAsync(_cts.Token);
        _healthWorker = Task.Run(() => RunHealthAsync(_cts.Token));
        _trafficWorker = Task.Run(() => RunTrafficAsync(_cts.Token));
    }

    public WanHealthState GetState(string logicalWan)
        => _status.TryGetValue(logicalWan, out var value) ? value.State : WanHealthState.Checking;

    public IReadOnlyDictionary<string, WanHealthState> GetStates()
        => _status.ToDictionary(x => x.Key, x => x.Value.State, StringComparer.OrdinalIgnoreCase);

    public WanTelemetrySnapshot? GetTelemetry(string logicalWan)
    {
        if (!_status.TryGetValue(logicalWan, out var value)) return null;
        lock (value.Gate)
        {
            long probes = value.ProbeSuccesses + value.ProbeFailures;
            return new WanTelemetrySnapshot(value.State, value.RxRate, value.TxRate,
                value.LastRx.HasValue ? value.RxTotal : null, value.LastTx.HasValue ? value.TxTotal : null,
                value.LatencyCurrentMs, value.LatencyMinMs,
                value.LatencySamples == 0 ? null : value.LatencySumMs / value.LatencySamples,
                value.LatencyMaxMs, value.ProbeSuccesses, value.ProbeFailures,
                probes == 0 ? null : value.ProbeFailures * 100d / probes,
                value.UpSince.HasValue ? Math.Max(0, (long)(DateTimeOffset.Now - value.UpSince.Value).TotalSeconds) : null);
        }
    }

    private async Task RunHealthAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.IntervalSeconds));
        try { while (await timer.WaitForNextTickAsync(ct)) await ProbeCycleAsync(ct); }
        catch (OperationCanceledException) { }
    }

    private async Task RunTrafficAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await timer.WaitForNextTickAsync(ct)) SampleTraffic(); }
        catch (OperationCanceledException) { }
    }

    private void SampleTraffic()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var now = DateTimeOffset.Now;
        foreach (var pair in _wans)
        {
            var nic = interfaces.FirstOrDefault(x => x.Name.Equals(pair.Value.InterfaceName, StringComparison.OrdinalIgnoreCase));
            if (nic is null) continue;
            try
            {
                var counters = nic.GetIPv4Statistics();
                var status = _status[pair.Key];
                lock (status.Gate)
                {
                    if (status.LastRx.HasValue && status.LastTx.HasValue && status.LastTrafficSample.HasValue)
                    {
                        double seconds = (now - status.LastTrafficSample.Value).TotalSeconds;
                        long rxDelta = Math.Max(0, counters.BytesReceived - status.LastRx.Value);
                        long txDelta = Math.Max(0, counters.BytesSent - status.LastTx.Value);
                        if (seconds > 0)
                        {
                            status.RxRate = rxDelta / seconds;
                            status.TxRate = txDelta / seconds;
                            status.RxTotal += rxDelta;
                            status.TxTotal += txDelta;
                        }
                    }
                    status.LastRx = counters.BytesReceived;
                    status.LastTx = counters.BytesSent;
                    status.LastTrafficSample = now;
                }
            }
            catch (NetworkInformationException) { }
        }
    }

    private async Task ProbeCycleAsync(CancellationToken ct)
    {
        foreach (var pair in _wans)
        {
            var result = await ProbeAsync(pair.Value, ct);
            RecordProbes(pair.Key, result.InterfaceUp, result.Targets);
            Apply(pair.Key, pair.Value.InterfaceName, result.InterfaceUp,
                result.InterfaceUp && result.GatewayUp && result.InternetUp);
            var status = _status[pair.Key];
            if (_debug)
                Console.WriteLine($"DEBUG {pair.Key} HEALTH | Interface={(result.InterfaceUp ? "PASS" : "FAIL")} | Gateway={(result.GatewayUp ? "PASS" : "FAIL")} {result.GatewayMs}ms | Internet={string.Join(',', result.Targets.Select(x => $"{x.Target}={(x.Success ? "PASS" : "FAIL")}:{x.LatencyMs}ms"))} | State={status.State.ToString().ToUpperInvariant()} | Failures={status.Failures}/{_settings.FailureThreshold} | Successes={status.Successes}/{_settings.RecoverySuccessThreshold}");
        }
    }

    private void RecordProbes(string logicalWan, bool interfaceUp,
        List<(string Target, bool Success, long LatencyMs)> targets)
    {
        var status = _status[logicalWan];
        lock (status.Gate)
        {
            if (!interfaceUp || targets.Count == 0)
            {
                status.ProbeFailures++;
                status.LatencyCurrentMs = null;
                return;
            }
            status.ProbeSuccesses += targets.LongCount(x => x.Success);
            status.ProbeFailures += targets.LongCount(x => !x.Success);
            var successful = targets.Where(x => x.Success).Select(x => (double)x.LatencyMs).ToArray();
            if (successful.Length == 0)
            {
                status.LatencyCurrentMs = null;
                return;
            }
            double current = successful.Average();
            status.LatencyCurrentMs = current;
            status.LatencyMinMs = status.LatencyMinMs.HasValue ? Math.Min(status.LatencyMinMs.Value, successful.Min()) : successful.Min();
            status.LatencyMaxMs = status.LatencyMaxMs.HasValue ? Math.Max(status.LatencyMaxMs.Value, successful.Max()) : successful.Max();
            status.LatencySumMs += successful.Sum();
            status.LatencySamples += successful.Length;
        }
    }

    private async Task<(bool InterfaceUp, bool GatewayUp, long GatewayMs,
        List<(string Target, bool Success, long LatencyMs)> Targets, bool InternetUp)> ProbeAsync(
        BindingMapping wan, CancellationToken ct)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(x => x.Name.Equals(wan.InterfaceName, StringComparison.OrdinalIgnoreCase));
        bool interfaceUp = nic?.OperationalStatus == OperationalStatus.Up;
        if (!interfaceUp) return (false, false, 0, [], false);

        var gatewayWatch = Stopwatch.StartNew();
        bool gatewayUp = await PingFromAsync(wan.InterfaceIpv4, wan.GatewayIpv4, ct);
        gatewayWatch.Stop();

        var targets = new List<(string, bool, long)>();
        foreach (string target in _settings.Targets)
        {
            var watch = Stopwatch.StartNew();
            bool success = await ConnectFromAsync(wan, target, ct);
            watch.Stop();
            targets.Add((target, success, watch.ElapsedMilliseconds));
        }
        return (true, gatewayUp, gatewayWatch.ElapsedMilliseconds, targets, targets.Any(x => x.Item2));
    }

    private static async Task<bool> PingFromAsync(string source, string destination, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
                    ArgumentList = { "-n", "1", "-w", "1000", "-S", source, destination },
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<bool> ConnectFromAsync(BindingMapping wan, string target, CancellationToken outerCt)
    {
        try
        {
            int separator = target.LastIndexOf(':');
            if (separator <= 0 || !IPAddress.TryParse(target[..separator], out var address) ||
                !int.TryParse(target[(separator + 1)..], out int port)) return false;
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31,
                IPAddress.HostToNetworkOrder(wan.InterfaceIndex));
            socket.Bind(new IPEndPoint(IPAddress.Parse(wan.InterfaceIpv4), 0));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            return true;
        }
        catch { return false; }
    }

    private void Apply(string logicalWan, string interfaceName, bool interfaceUp, bool healthy)
    {
        var status = _status[logicalWan];
        WanHealthState previous;
        lock (status.Gate)
        {
            previous = status.State;
            if (!interfaceUp)
            {
                status.Failures = _settings.FailureThreshold;
                status.Successes = 0;
                status.State = WanHealthState.Down;
            }
            else if (healthy)
            {
                status.Failures = 0;
                status.Successes++;
                status.State = previous switch
                {
                    WanHealthState.Checking => WanHealthState.Up,
                    WanHealthState.Suspect => WanHealthState.Up,
                    WanHealthState.Down => WanHealthState.Recovering,
                    WanHealthState.Recovering when status.Successes >= _settings.RecoverySuccessThreshold => WanHealthState.Up,
                    _ => previous
                };
            }
            else
            {
                status.Successes = 0;
                status.Failures++;
                status.State = previous switch
                {
                    WanHealthState.Checking => WanHealthState.Suspect,
                    WanHealthState.Up => WanHealthState.Suspect,
                    WanHealthState.Suspect when status.Failures >= _settings.FailureThreshold => WanHealthState.Down,
                    WanHealthState.Recovering => WanHealthState.Down,
                    _ => previous
                };
            }

            if (status.State == previous) return;
            if (status.State == WanHealthState.Up)
            {
                status.UpEvents++;
                status.UpSince = DateTimeOffset.Now;
            }
            if (status.State == WanHealthState.Down)
            {
                status.DownEvents++;
                status.UpSince = null;
            }
            if (previous == WanHealthState.Recovering && status.State == WanHealthState.Up) status.Recoveries++;
        }
        Console.WriteLine($"HEALTH | {DateTimeOffset.Now:O} | {logicalWan} | {interfaceName} | {previous.ToString().ToUpperInvariant()} -> {status.State.ToString().ToUpperInvariant()} | failures={status.Failures} | successes={status.Successes}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WhenAll(_healthWorker ?? Task.CompletedTask, _trafficWorker ?? Task.CompletedTask).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        foreach (var pair in _status.OrderBy(x => x.Key))
            Console.WriteLine($"INFO HEALTH SUMMARY {pair.Key}: UP events={pair.Value.UpEvents}, DOWN events={pair.Value.DownEvents}, recoveries={pair.Value.Recoveries}, final={pair.Value.State.ToString().ToUpperInvariant()}");
        _cts.Dispose();
    }
}
