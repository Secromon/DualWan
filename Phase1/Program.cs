using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using NetBinder.Service.Services;
using NetBinder.Shared.Models;

const string ServiceName = "DualWANService";
var debug = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);
var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DualWAN");
var paths = new RuntimePaths(dataRoot, Path.Combine(dataRoot, "config.json"), Path.Combine(dataRoot, "logs"),
    Path.Combine(dataRoot, "state", "dualwan-state.json"), Path.Combine(dataRoot, "data", "telemetry.db"));
Directory.CreateDirectory(paths.Logs);
Directory.CreateDirectory(Path.GetDirectoryName(paths.State)!);

if (args.Contains("--cleanup", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("CLEANUP BEGIN");
    CleanupOwnedProcess(paths.State);
    string result = "CLEANUP PASS: state removed; WinDivert handles are process-scoped; no routes, metrics or WFP filters created";
    Console.WriteLine(result);
    File.AppendAllText(Path.Combine(paths.Logs, "dualwan.log"), $"[{DateTimeOffset.Now:O}] {result}{Environment.NewLine}");
    return 0;
}

string? configOverride = args.SkipWhile(x => !x.Equals("--config", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
if (!string.IsNullOrWhiteSpace(configOverride)) paths = paths with { Config = Path.GetFullPath(configOverride) };

using var logger = new ProductLogger(paths.Logs, Console.Out, debug);
Console.SetOut(logger);
Console.SetError(logger);
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
builder.Services.AddSingleton(new RuntimeOptions(paths, debug, WindowsServiceHelpers.IsWindowsService()));
builder.Services.AddHostedService<DualWanRuntime>();
await builder.Build().RunAsync();
return 0;

static void CleanupOwnedProcess(string statePath)
{
    if (!File.Exists(statePath)) return;
    var state = JsonSerializer.Deserialize<RunState>(File.ReadAllText(statePath));
    if (state is not null && state.Pid != Environment.ProcessId)
    {
        try
        {
            using var process = Process.GetProcessById(state.Pid);
            string? actualPath = process.MainModule?.FileName;
            if (!string.Equals(actualPath, state.Executable, StringComparison.OrdinalIgnoreCase) ||
                process.StartTime.ToUniversalTime().Ticks != state.StartTicksUtc)
                throw new InvalidOperationException("PID reused or executable mismatch; no process terminated.");
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000)) throw new TimeoutException("DualWAN did not stop within five seconds.");
            Console.WriteLine($"CLEANUP stopped owned process PID={state.Pid}");
        }
        catch (ArgumentException) { Console.WriteLine("CLEANUP process already absent"); }
    }
    File.Delete(statePath);
}

sealed class DualWanRuntime : IHostedService, IDualWanControlPlane
{
    private readonly RuntimeOptions _options;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly CancellationTokenSource _retryCancellation = new();
    private Task? _retryTask;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private WanHealthService? _health;
    private UdpRelay? _udpRelay;
    private RedirectorService? _redirector;
    private TransparentProxy? _relay;
    private ControlPipeServer? _ipc;
    private TelemetryHistoryStore? _history;
    private Dictionary<string, BindingMapping> _wanMap = new(StringComparer.OrdinalIgnoreCase);
    private List<ProcessRoutingPolicy> _policies = [];
    private Config? _config;
    private bool _engineReady;
    private bool _stopped;

    public DualWanRuntime(RuntimeOptions options) => _options = options;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        Console.WriteLine("DualWAN Service starting");
        Console.WriteLine($"Version={version}; PID={Environment.ProcessId}; Mode={(_options.IsService ? "SERVICE" : "CONSOLE")}");
        Console.WriteLine($"Configuration path={_options.Paths.Config}");
        PrepareStateFile();
        _ipc = new ControlPipeServer(this, _options.PipeName);
        await _ipc.StartAsync();
        if (File.Exists(_options.Paths.Config))
        {
            try
            {
                _config = ReadConfig();
                if (HasWanBindings(_config)) await ActivateRoutingAsync(_config);
                else Console.WriteLine("CONFIGURATION REQUIRED: WAN bindings are not set; routing engine inactive");
            }
            catch (Exception ex)
            {
                await StopRoutingAsync();
                Console.WriteLine($"CONFIGURATION ERROR: {ex.GetType().Name}: {ex.Message}; routing engine inactive");
            }
        }
        else Console.WriteLine("CONFIGURATION REQUIRED: routing engine inactive");
        _retryTask = Task.Run(() => RetryUnavailableWansAsync(_retryCancellation.Token));
        Console.WriteLine($"SERVICE READY: routing engine {(_engineReady ? "active" : "inactive")}");
    }

    private Config ReadConfig() => JsonSerializer.Deserialize<Config>(File.ReadAllText(_options.Paths.Config),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Empty JSON configuration.");

    private static bool HasWanBindings(Config config) =>
        config.Interfaces is not null && config.Interfaces.TryGetValue("WAN1", out var first) &&
        config.Interfaces.TryGetValue("WAN2", out var second) &&
        (!string.IsNullOrWhiteSpace(first.InterfaceId) || !string.IsNullOrWhiteSpace(first.Name)) &&
        (!string.IsNullOrWhiteSpace(second.InterfaceId) || !string.IsNullOrWhiteSpace(second.Name));

    private async Task<bool> ActivateRoutingAsync(Config config)
    {
        try
        {
            var wanMap = DiscoverWans(config);
            _config = config;
            _wanMap = wanMap;
            if (wanMap.Values.Any(x => !x.IsActive))
            {
                Console.WriteLine("WAN INTERFACE UNAVAILABLE: routing engine inactive until configured interfaces return");
                return false;
            }
            var policies = LoadPolicies(config, wanMap);
            var hc = config.Health ?? new HealthConfig(3, 3, 3, ["1.1.1.1:443", "8.8.8.8:443"]);
            if (hc.IntervalSeconds is < 2 or > 30 || hc.FailureThreshold is < 1 or > 10 ||
                hc.RecoverySuccessThreshold is < 2 or > 20 || hc.Targets.Count < 2)
                throw new InvalidDataException("Invalid health configuration.");
            _health = new WanHealthService(wanMap,
                new HealthSettings(hc.IntervalSeconds, hc.FailureThreshold, hc.RecoverySuccessThreshold, hc.Targets),
                _options.Debug);
            await _health.StartAsync();
            Console.WriteLine("Health service started");
            _udpRelay = new UdpRelay();
            Console.WriteLine("UDP relay started");
            _redirector = new RedirectorService(_udpRelay);
            _relay = new TransparentProxy(_redirector.GetNATMapping);
            if (!_relay.Start()) throw new InvalidOperationException("TCP relay failed to start.");
            Console.WriteLine($"TCP relay started on 127.0.0.1:{_relay.ListenPort}");
            _redirector.UpdateBindings(wanMap.Values, policies, _health);
            if (!_redirector.Start(_relay.ListenPort)) throw new InvalidOperationException("WinDivert failed to start.");
            Console.WriteLine("WinDivert started");
            _policies = policies;
            _engineReady = true;
            try
            {
                _history = new TelemetryHistoryStore(_options.Paths.TelemetryDatabase, _wanMap, _health, _redirector);
                await _history.StartAsync();
                Console.WriteLine($"Historical telemetry started: {_options.Paths.TelemetryDatabase}");
            }
            catch (Exception ex)
            {
                _history = null;
                Console.WriteLine($"TELEMETRY DATABASE ERROR: {ex.GetType().Name}: {ex.Message}; routing continues");
            }
            var runState = new RunState(Environment.ProcessId, Environment.ProcessPath!,
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
            File.WriteAllText(_options.Paths.State, JsonSerializer.Serialize(runState));
            Console.WriteLine($"SERVICE READY: WANs={wanMap.Count}; rules={policies.Count}; WinDivert=2.2");
            return true;
        }
        catch (Exception ex)
        {
            await StopRoutingAsync();
            Console.WriteLine($"ROUTING START ERROR: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task RetryUnavailableWansAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _configurationGate.WaitAsync(cancellationToken);
                try
                {
                    if (!_engineReady && _config is not null && HasWanBindings(_config))
                        try { await ActivateRoutingAsync(_config); }
                        catch (Exception ex) { Console.WriteLine($"WAN retry deferred: {ex.Message}"); }
                    else if (_engineReady && _config is not null)
                    {
                        try
                        {
                            var current = DiscoverWans(_config);
                            if (current.Values.All(x => x.IsActive) && current.Any(x =>
                                !_wanMap.TryGetValue(x.Key, out var old) ||
                                old.InterfaceIndex != x.Value.InterfaceIndex ||
                                old.InterfaceIpv4 != x.Value.InterfaceIpv4 ||
                                old.GatewayIpv4 != x.Value.GatewayIpv4))
                            {
                                Console.WriteLine("WAN interface address changed; refreshing bindings for new flows");
                                await StopRoutingAsync();
                                await ActivateRoutingAsync(_config);
                            }
                        }
                        catch (Exception ex) { Console.WriteLine($"WAN refresh deferred: {ex.Message}"); }
                    }
                }
                finally { _configurationGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _retryCancellation.Cancel();
        if (_retryTask is not null) await _retryTask;
        await StopCoreAsync(true);
    }

    private async Task StopCoreAsync(bool requested)
    {
        if (_stopped) return;
        _stopped = true;
        if (requested) Console.WriteLine("SERVICE STOP REQUESTED");
        if (_ipc is not null) await _ipc.DisposeAsync();
        await StopRoutingAsync();
        Console.WriteLine("SERVICE STOPPED");
    }

    private async Task StopRoutingAsync()
    {
        if (_history is not null) { await _history.DisposeAsync(); _history = null; }
        _redirector?.Stop();
        _redirector = null;
        _relay?.Stop();
        _relay = null;
        _udpRelay?.Dispose();
        _udpRelay = null;
        _health?.Dispose();
        _health = null;
        _engineReady = false;
        if (File.Exists(_options.Paths.State)) File.Delete(_options.Paths.State);
        Console.WriteLine("Routing handles closed");
    }

    private void PrepareStateFile()
    {
        if (!File.Exists(_options.Paths.State)) return;
        var state = JsonSerializer.Deserialize<RunState>(File.ReadAllText(_options.Paths.State));
        if (state is null) { File.Delete(_options.Paths.State); return; }
        try
        {
            using var process = Process.GetProcessById(state.Pid);
            string? path = process.MainModule?.FileName;
            if (string.Equals(path, state.Executable, StringComparison.OrdinalIgnoreCase) &&
                process.StartTime.ToUniversalTime().Ticks == state.StartTicksUtc)
                throw new InvalidOperationException($"Another DualWAN instance is active (PID {state.Pid}).");
            File.Delete(_options.Paths.State);
        }
        catch (ArgumentException) { File.Delete(_options.Paths.State); }
    }

    private static Dictionary<string, BindingMapping> DiscoverWans(Config config)
    {
        if (config.Interfaces.Count != 2 || !config.Interfaces.ContainsKey("WAN1") ||
            !config.Interfaces.ContainsKey("WAN2"))
            throw new InvalidDataException("DualWAN requires exactly WAN1 and WAN2.");
        var nics = NetworkInterface.GetAllNetworkInterfaces();
        var result = new Dictionary<string, BindingMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in config.Interfaces)
        {
            var spec = requested.Value;
            var nic = !string.IsNullOrWhiteSpace(spec.InterfaceId)
                ? nics.SingleOrDefault(x => x.Id.Equals(spec.InterfaceId, StringComparison.OrdinalIgnoreCase))
                : nics.SingleOrDefault(x => x.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase));
            var binding = new BindingMapping
            {
                InterfaceName = nic?.Name ?? spec.Name,
                LogicalWan = requested.Key,
                RoutingMethod = RoutingMethod.Transparent,
                IsActive = false
            };
            if (nic is null || nic.OperationalStatus != OperationalStatus.Up)
            {
                result[requested.Key] = binding;
                continue;
            }
            var props = nic.GetIPProperties();
            var ip = props.UnicastAddresses.Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x));
            var gateway = props.GatewayAddresses.Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
            int index = props.GetIPv4Properties()?.Index ?? 0;
            if (ip is null || gateway is null || index <= 0)
            {
                result[requested.Key] = binding;
                continue;
            }
            Console.WriteLine($"{requested.Key}: {nic.Name}, index={index}, IPv4={ip}, gateway={gateway}");
            binding.InterfaceIndex = index;
            binding.InterfaceIpv4 = ip.ToString();
            binding.GatewayIpv4 = gateway.ToString();
            binding.IsActive = true;
            result[requested.Key] = binding;
        }
        if (result.Values.All(x => x.IsActive) &&
            result["WAN1"].InterfaceIndex == result["WAN2"].InterfaceIndex)
            throw new InvalidDataException("WAN1 and WAN2 must use different interfaces.");
        return result;
    }

    private static List<ProcessRoutingPolicy> LoadPolicies(Config config, Dictionary<string, BindingMapping> wans)
    {
        var result = new List<ProcessRoutingPolicy>();
        var activeIndividuals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in config.Rules)
        {
            ValidateProcessName(rule.Process);
            if (!wans.ContainsKey(rule.Wan)) throw new InvalidDataException($"Unknown WAN: {rule.Wan}");
            if (!Enum.TryParse<FailoverMode>(rule.Mode, true, out var mode))
                throw new InvalidDataException($"Unknown policy mode: {rule.Mode}");
            if (!rule.Enabled)
            {
                Console.WriteLine($"Rule disabled: {rule.Process}");
                continue;
            }
            if (!activeIndividuals.Add(rule.Process))
                throw new InvalidDataException($"Duplicate individual rule: {rule.Process}");
            result.Add(new ProcessRoutingPolicy(rule.Process, rule.Wan, mode, "INDIVIDUAL"));
            Console.WriteLine($"Rule loaded: {rule.Process} -> {rule.Wan} mode={mode.ToString().ToUpperInvariant()}");
        }

        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var memberships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in config.Groups ?? [])
        {
            ValidateGroupName(group.Name);
            if (!groupNames.Add(group.Name)) throw new InvalidDataException($"Duplicate group name: {group.Name}");
            if (!wans.ContainsKey(group.Wan)) throw new InvalidDataException($"Unknown WAN in group {group.Name}: {group.Wan}");
            if (!Enum.TryParse<FailoverMode>(group.Mode, true, out var mode))
                throw new InvalidDataException($"Unknown mode in group {group.Name}: {group.Mode}");
            var localMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string process in group.Applications)
            {
                ValidateProcessName(process);
                if (!localMembers.Add(process)) throw new InvalidDataException($"Duplicate application in group {group.Name}: {process}");
                if (!memberships.Add(process)) throw new InvalidDataException($"Application belongs to multiple groups: {process}");
                if (group.Enabled && !activeIndividuals.Contains(process))
                {
                    result.Add(new ProcessRoutingPolicy(process, group.Wan, mode, $"GROUP:{group.Name}"));
                    Console.WriteLine($"Group rule loaded: {process} -> {group.Wan} mode={mode.ToString().ToUpperInvariant()} group={group.Name}");
                }
            }
        }
        return result;
    }

    public object GetStatus() => new
    {
        service = "RUNNING",
        version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
        uptimeSeconds = (long)(DateTimeOffset.Now - _startedAt).TotalSeconds,
        engine = _engineReady ? "READY" : "INACTIVE",
        routingEngineReady = _engineReady,
        winDivert = _redirector?.IsRunning == true ? "RUNNING" : "STOPPED",
        tcpRelay = _relay is not null && _engineReady ? "RUNNING" : "STOPPED",
        udpRelay = _udpRelay is not null && _engineReady ? "RUNNING" : "STOPPED",
        healthWorker = _health is not null && _engineReady ? "RUNNING" : "STOPPED",
        activeFlows = _redirector?.ActiveFlowCount ?? 0
    };

    public object GetWans() => new
    {
        wans = _wanMap.OrderBy(x => x.Key).Select(x => new
        {
            id = x.Key,
            name = WanLabel(x.Key, x.Value.InterfaceName),
            ifIndex = x.Value.InterfaceIndex,
            ipv4 = x.Value.InterfaceIpv4,
            gateway = x.Value.GatewayIpv4,
            state = _engineReady && x.Value.IsActive
                ? (_health?.GetState(x.Key) ?? WanHealthState.Checking).ToString().ToUpperInvariant()
                : "DOWN",
            role = "POLICY_DEPENDENT"
        }).ToArray()
    };

    private string WanLabel(string id, string fallback) =>
        _config?.Interfaces.TryGetValue(id, out var spec) == true &&
        !string.IsNullOrWhiteSpace(spec.FriendlyName) ? spec.FriendlyName : fallback;

    public object GetAvailableInterfaces() => new
    {
        interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                x.OperationalStatus == OperationalStatus.Up)
            .Select(x => new { nic = x, details = GetInterfaceDetails(x) })
            .Where(x => x.details is not null)
            .Select(x => new
            {
                id = x.nic.Id,
                name = x.nic.Name,
                ipv4 = x.details!.Value.Ipv4,
                gateway = x.details.Value.Gateway
            }).OrderBy(x => x.name).ToArray()
    };

    public object GetWanConfiguration() => new
    {
        wan1 = WanConfiguration("WAN1"),
        wan2 = WanConfiguration("WAN2"),
        active = _engineReady
    };

    private object WanConfiguration(string key)
    {
        WanSpec? spec = null;
        if (_config is not null) _config.Interfaces.TryGetValue(key, out spec);
        return new
        {
            interfaceId = spec?.InterfaceId ?? "",
            interfaceName = spec?.Name ?? "",
            friendlyName = string.IsNullOrWhiteSpace(spec?.FriendlyName) ? key : spec.FriendlyName,
            state = _wanMap.TryGetValue(key, out var wan)
                ? _engineReady && wan.IsActive ? (_health?.GetState(key) ?? WanHealthState.Checking).ToString().ToUpperInvariant() : "DOWN"
                : "UNCONFIGURED"
        };
    }

    private static (string Ipv4, string Gateway, int Index)? GetInterfaceDetails(NetworkInterface nic)
    {
        try
        {
            var props = nic.GetIPProperties();
            var ip = props.UnicastAddresses.Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x));
            var gateway = props.GatewayAddresses.Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
            int index = props.GetIPv4Properties()?.Index ?? 0;
            return ip is null || gateway is null || index <= 0 ? null : (ip.ToString(), gateway.ToString(), index);
        }
        catch (NetworkInformationException) { return null; }
    }

    public async Task<ControlResult> SetWanConfigurationAsync(ControlWanSelection wan1, ControlWanSelection wan2,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(wan1.InterfaceId) || string.IsNullOrWhiteSpace(wan2.InterfaceId))
            return ControlResult.Error("INTERFACE_REQUIRED", "Select both WAN interfaces.");
        if (wan1.InterfaceId.Equals(wan2.InterfaceId, StringComparison.OrdinalIgnoreCase))
            return ControlResult.Error("DUPLICATE_INTERFACE", "WAN1 and WAN2 must use different interfaces.");
        if (wan1.FriendlyName.Length > 64 || wan2.FriendlyName.Length > 64)
            return ControlResult.Error("INVALID_WAN", "Friendly names must be 64 characters or fewer.");

        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            var available = NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.OperationalStatus == OperationalStatus.Up && GetInterfaceDetails(x) is not null)
                .ToArray();
            var first = available.SingleOrDefault(x => x.Id.Equals(wan1.InterfaceId, StringComparison.OrdinalIgnoreCase));
            var second = available.SingleOrDefault(x => x.Id.Equals(wan2.InterfaceId, StringComparison.OrdinalIgnoreCase));
            if (first is null || second is null)
                return ControlResult.Error("INTERFACE_UNAVAILABLE", "A selected interface is unavailable.");
            var previous = _config;
            bool wasReady = _engineReady;
            var interfaces = new Dictionary<string, WanSpec>(StringComparer.OrdinalIgnoreCase)
            {
                ["WAN1"] = new(first.Name, string.IsNullOrWhiteSpace(wan1.FriendlyName) ? "WAN1" : wan1.FriendlyName.Trim(), first.Id),
                ["WAN2"] = new(second.Name, string.IsNullOrWhiteSpace(wan2.FriendlyName) ? "WAN2" : wan2.FriendlyName.Trim(), second.Id)
            };
            var candidate = previous is null
                ? new Config(interfaces, [], null)
                : previous with { Interfaces = interfaces };
            try
            {
                var resolved = DiscoverWans(candidate);
                if (resolved.Values.Any(x => !x.IsActive))
                    return ControlResult.Error("INTERFACE_UNAVAILABLE", "A selected interface is unavailable.");
                var policies = LoadPolicies(candidate, resolved);
                if (wasReady)
                {
                    await ApplyActiveWanConfigurationAsync(candidate, resolved, policies);
                    return ControlResult.Ok(GetWanConfiguration());
                }
                if (!await ActivateRoutingAsync(candidate))
                    throw new InvalidDataException("A selected interface became unavailable.");
                PersistAtomically(candidate);
                return ControlResult.Ok(GetWanConfiguration());
            }
            catch (Exception ex)
            {
                if (!wasReady)
                {
                    await StopRoutingAsync();
                    _config = previous;
                    _wanMap = new(StringComparer.OrdinalIgnoreCase);
                }
                return ControlResult.Error("WAN_CONFIGURATION_FAILED", ex.Message);
            }
        }
        finally { _configurationGate.Release(); }
    }

    private async Task ApplyActiveWanConfigurationAsync(Config candidate,
        Dictionary<string, BindingMapping> resolved, List<ProcessRoutingPolicy> policies)
    {
        var hc = candidate.Health ?? new HealthConfig(3, 3, 3, ["1.1.1.1:443", "8.8.8.8:443"]);
        var replacement = new WanHealthService(resolved,
            new HealthSettings(hc.IntervalSeconds, hc.FailureThreshold, hc.RecoverySuccessThreshold, hc.Targets),
            _options.Debug);
        try
        {
            await replacement.StartAsync();
            PersistAtomically(candidate);
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
        try { _redirector!.UpdateBindings(resolved.Values, policies, replacement); }
        catch
        {
            replacement.Dispose();
            PersistAtomically(_config!);
            throw;
        }
        var oldHealth = _health;
        _health = replacement;
        _wanMap = resolved;
        _policies = policies;
        _config = candidate;
        oldHealth?.Dispose();
        if (_history is not null)
        {
            try
            {
                await _history.DisposeAsync();
                _history = new TelemetryHistoryStore(_options.Paths.TelemetryDatabase, resolved, replacement, _redirector);
                await _history.StartAsync();
            }
            catch (Exception ex)
            {
                _history = null;
                Console.WriteLine($"TELEMETRY DATABASE ERROR: {ex.Message}; routing continues");
            }
        }
    }

    public object GetRules() => new
    {
        rules = (_config?.Rules ?? []).Select(x => new
        {
            process = x.Process,
            wan = x.Wan,
            mode = x.Mode.ToUpperInvariant(),
            enabled = x.Enabled
        }).ToArray()
    };

    public object GetGroups() => new
    {
        currentPreset = _config?.CurrentPreset ?? "Custom",
        groups = (_config?.Groups ?? []).Select(x => new
        {
            name = x.Name,
            wan = x.Wan,
            mode = x.Mode.ToUpperInvariant(),
            enabled = x.Enabled,
            applications = x.Applications.ToArray()
        }).ToArray()
    };

    public async Task<ControlResult> GetTelemetryHistoryAsync(string wan, DateTimeOffset from,
        DateTimeOffset to, string resolution)
    {
        if (_history is null) return ControlResult.Error("HISTORY_UNAVAILABLE", "Historical telemetry is unavailable.");
        if (!_wanMap.ContainsKey(wan)) return ControlResult.Error("INVALID_WAN", $"Unknown WAN: {wan}");
        if (to <= from || to - from > TimeSpan.FromDays(366)) return ControlResult.Error("INVALID_RANGE", "Invalid history range.");
        try { return ControlResult.Ok(new { wan, resolution, points = await _history.QueryAsync(wan, from, to, resolution) }); }
        catch (InvalidDataException ex) { return ControlResult.Error("INVALID_RESOLUTION", ex.Message); }
        catch (Exception ex) { Console.WriteLine($"TELEMETRY QUERY ERROR: {ex.Message}"); return ControlResult.Error("HISTORY_UNAVAILABLE", "Historical telemetry query failed."); }
    }

    public async Task<ControlResult> GetTelemetryStorageStatusAsync()
    {
        if (_history is null) return ControlResult.Error("HISTORY_UNAVAILABLE", "Historical telemetry is unavailable.");
        try { return ControlResult.Ok(await _history.StatusAsync()); }
        catch (Exception ex) { Console.WriteLine($"TELEMETRY STATUS ERROR: {ex.Message}"); return ControlResult.Error("HISTORY_UNAVAILABLE", "Storage status unavailable."); }
    }

    public async Task<ControlResult> SetTelemetryStoragePolicyAsync(int? retentionDays, long? maximumBytes)
    {
        if (_history is null) return ControlResult.Error("HISTORY_UNAVAILABLE", "Historical telemetry is unavailable.");
        try { await _history.SetPolicyAsync(new StoragePolicy(retentionDays, maximumBytes)); return ControlResult.Ok(await _history.StatusAsync()); }
        catch (InvalidDataException ex) { return ControlResult.Error("INVALID_STORAGE_POLICY", ex.Message); }
        catch (Exception ex) { Console.WriteLine($"TELEMETRY SETTINGS ERROR: {ex.Message}"); return ControlResult.Error("PERSIST_FAILED", "Storage settings could not be saved."); }
    }

    public async Task<ControlResult> CleanTelemetryAsync()
    {
        if (_history is null) return ControlResult.Error("HISTORY_UNAVAILABLE", "Historical telemetry is unavailable.");
        try { await _history.MaintainAsync(); return ControlResult.Ok(await _history.StatusAsync()); }
        catch (Exception ex) { Console.WriteLine($"TELEMETRY CLEANUP ERROR: {ex.Message}"); return ControlResult.Error("CLEANUP_FAILED", "Historical maintenance failed."); }
    }

    public object GetTelemetry() => new
    {
        service = new
        {
            uptimeSeconds = (long)(DateTimeOffset.Now - _startedAt).TotalSeconds,
            activeFlows = _redirector?.ActiveFlowCount ?? 0,
            totalFlows = _redirector?.TotalRoutedFlowCount ?? 0,
            failedFlows = _redirector?.FailedFlowCount ?? 0,
            failoverCount = _redirector?.FailoverCount ?? 0,
            failbackCount = _redirector?.FailbackCount ?? 0,
            strictFailures = _redirector?.StrictFailureCount ?? 0
        },
        wans = _wanMap.OrderBy(x => x.Key).Select(x =>
        {
            WanTelemetrySnapshot? telemetry = _health?.GetTelemetry(x.Key);
            return new
            {
                id = x.Key,
                name = WanLabel(x.Key, x.Value.InterfaceName),
                state = _engineReady && x.Value.IsActive
                    ? (telemetry?.State ?? WanHealthState.Checking).ToString().ToUpperInvariant() : "DOWN",
                ipv4 = x.Value.InterfaceIpv4,
                ifIndex = x.Value.InterfaceIndex,
                rxBytesPerSecond = telemetry?.RxBytesPerSecond,
                txBytesPerSecond = telemetry?.TxBytesPerSecond,
                rxTotalBytes = telemetry?.RxTotalBytes,
                txTotalBytes = telemetry?.TxTotalBytes,
                latencyMs = telemetry?.LatencyCurrentMs,
                latencyMinMs = telemetry?.LatencyMinMs,
                latencyAvgMs = telemetry?.LatencyAverageMs,
                latencyMaxMs = telemetry?.LatencyMaxMs,
                probeSuccesses = telemetry?.ProbeSuccesses ?? 0,
                probeFailures = telemetry?.ProbeFailures ?? 0,
                probeLossPercent = telemetry?.ProbeLossPercent,
                upSeconds = telemetry?.UpSeconds
            };
        }).ToArray()
    };

    public async Task<ControlResult> SetRuleAsync(ControlRule rule, CancellationToken cancellationToken)
        => await SaveRuleAsync(rule, allowCreate: false, cancellationToken);

    public async Task<ControlResult> UpsertRuleAsync(ControlRule rule, CancellationToken cancellationToken)
        => await SaveRuleAsync(rule, allowCreate: true, cancellationToken);

    private async Task<ControlResult> SaveRuleAsync(ControlRule rule, bool allowCreate,
        CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_engineReady || _config is null || _redirector is null || _health is null)
                return ControlResult.Error("ENGINE_NOT_READY", "Routing engine is not ready.");
            try { ValidateProcessName(rule.Process); }
            catch (InvalidDataException ex) { return ControlResult.Error("INVALID_PROCESS", ex.Message); }
            if (!_wanMap.ContainsKey(rule.Wan)) return ControlResult.Error("INVALID_WAN", $"{rule.Wan} does not exist.");
            if (!Enum.TryParse<FailoverMode>(rule.Mode, true, out _))
                return ControlResult.Error("INVALID_MODE", "Mode must be STRICT or FAILOVER.");
            int index = _config.Rules.FindIndex(x => x.Process.Equals(rule.Process, StringComparison.OrdinalIgnoreCase));
            if (index < 0 && !allowCreate)
                return ControlResult.Error("RULE_NOT_FOUND", $"No rule exists for {rule.Process}.");

            var rules = _config.Rules.Select(x => x with { }).ToList();
            bool created = index < 0;
            var updated = new Rule(rule.Process, rule.Wan.ToUpperInvariant(), rule.Mode.ToUpperInvariant(), rule.Enabled);
            if (created) rules.Add(updated);
            else rules[index] = updated;
            var candidate = _config with { Rules = rules };
            var policies = LoadPolicies(candidate, _wanMap);
            PersistAtomically(candidate);
            _redirector.UpdateBindings(_wanMap.Values, policies, _health);
            _config = candidate;
            _policies = policies;
            Console.WriteLine($"IPC configuration {(created ? "created" : "changed")}: {rule.Process} -> {rule.Wan.ToUpperInvariant()} mode={rule.Mode.ToUpperInvariant()} enabled={rule.Enabled}");
            return ControlResult.Ok(new { created, rule = new { process = rule.Process, wan = rule.Wan.ToUpperInvariant(), mode = rule.Mode.ToUpperInvariant(), enabled = rule.Enabled } });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ControlResult.Error("PERSIST_FAILED", ex.Message);
        }
        finally { _configurationGate.Release(); }
    }

    public async Task<ControlResult> DeleteRuleAsync(string process, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_engineReady || _config is null || _redirector is null || _health is null)
                return ControlResult.Error("ENGINE_NOT_READY", "Routing engine is not ready.");
            try { ValidateProcessName(process); }
            catch (InvalidDataException ex) { return ControlResult.Error("INVALID_PROCESS", ex.Message); }
            int index = _config.Rules.FindIndex(x => x.Process.Equals(process, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return ControlResult.Error("RULE_NOT_FOUND", $"No rule exists for {process}.");

            var rules = _config.Rules.Select(x => x with { }).ToList();
            rules.RemoveAt(index);
            var candidate = _config with { Rules = rules };
            var policies = LoadPolicies(candidate, _wanMap);
            PersistAtomically(candidate);
            _redirector.UpdateBindings(_wanMap.Values, policies, _health);
            _config = candidate;
            _policies = policies;
            Console.WriteLine($"IPC configuration deleted: {process}");
            return ControlResult.Ok(new { process });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ControlResult.Error("PERSIST_FAILED", ex.Message);
        }
        finally { _configurationGate.Release(); }
    }

    public async Task<ControlResult> UpsertGroupAsync(ControlGroup group, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_engineReady || _config is null || _redirector is null || _health is null)
                return ControlResult.Error("ENGINE_NOT_READY", "Routing engine is not ready.");
            try
            {
                ValidateGroupName(group.Name);
                foreach (string process in group.Applications) ValidateProcessName(process);
                if (!_wanMap.ContainsKey(group.Wan)) throw new InvalidDataException($"Unknown WAN: {group.Wan}");
                if (!Enum.TryParse<FailoverMode>(group.Mode, true, out _))
                    throw new InvalidDataException("Mode must be STRICT or FAILOVER.");
                var groups = (_config.Groups ?? []).Select(x => x with { Applications = [.. x.Applications] }).ToList();
                int index = groups.FindIndex(x => x.Name.Equals(group.Name, StringComparison.OrdinalIgnoreCase));
                var updated = new Group(group.Name.Trim(), group.Wan.ToUpperInvariant(),
                    group.Mode.ToUpperInvariant(), group.Enabled, group.Applications.ToList());
                if (index < 0) groups.Add(updated); else groups[index] = updated;
                var candidate = _config with { Groups = groups, CurrentPreset = "Custom" };
                var policies = LoadPolicies(candidate, _wanMap);
                PersistAtomically(candidate);
                _redirector.UpdateBindings(_wanMap.Values, policies, _health);
                _config = candidate;
                _policies = policies;
                Console.WriteLine($"IPC group {(index < 0 ? "created" : "changed")}: {group.Name}; applications={group.Applications.Count}");
                return ControlResult.Ok(new { created = index < 0, currentPreset = "Custom" });
            }
            catch (InvalidDataException ex) { return ControlResult.Error("INVALID_GROUP", ex.Message); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ControlResult.Error("PERSIST_FAILED", ex.Message);
        }
        finally { _configurationGate.Release(); }
    }

    public async Task<ControlResult> DeleteGroupAsync(string name, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_engineReady || _config is null || _redirector is null || _health is null)
                return ControlResult.Error("ENGINE_NOT_READY", "Routing engine is not ready.");
            try { ValidateGroupName(name); }
            catch (InvalidDataException ex) { return ControlResult.Error("INVALID_GROUP", ex.Message); }
            var groups = (_config.Groups ?? []).Select(x => x with { Applications = [.. x.Applications] }).ToList();
            int removed = groups.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return ControlResult.Error("GROUP_NOT_FOUND", $"No group exists with name {name}.");
            var candidate = _config with { Groups = groups, CurrentPreset = "Custom" };
            var policies = LoadPolicies(candidate, _wanMap);
            PersistAtomically(candidate);
            _redirector.UpdateBindings(_wanMap.Values, policies, _health);
            _config = candidate;
            _policies = policies;
            Console.WriteLine($"IPC group deleted: {name}");
            return ControlResult.Ok(new { name, currentPreset = "Custom" });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ControlResult.Error("PERSIST_FAILED", ex.Message);
        }
        finally { _configurationGate.Release(); }
    }

    public async Task<ControlResult> ApplyPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_engineReady || _config is null || _redirector is null || _health is null)
                return ControlResult.Error("ENGINE_NOT_READY", "Routing engine is not ready.");
            string id = presetId.Trim().ToLowerInvariant();
            string display = id switch
            {
                "5g-priority" => "5G Priority",
                "save-5g" => "Save 5G",
                "only-5g" => "Only 5G",
                "custom" => "Custom",
                _ => ""
            };
            if (display.Length == 0) return ControlResult.Error("INVALID_PRESET", $"Unknown preset: {presetId}");
            var groups = (_config.Groups ?? []).Select(x => x with { Applications = [.. x.Applications] }).ToList();
            if (id != "custom")
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    (string wan, string mode) = id switch
                    {
                        "save-5g" => ("WAN1", "FAILOVER"),
                        "only-5g" => ("WAN2", "STRICT"),
                        _ when group.Name.Equals("Windows / System", StringComparison.OrdinalIgnoreCase) ||
                               group.Name.Equals("Windows", StringComparison.OrdinalIgnoreCase) => ("WAN1", "FAILOVER"),
                        _ when group.Name.Equals("Custom", StringComparison.OrdinalIgnoreCase) => (group.Wan, group.Mode),
                        _ => ("WAN2", "FAILOVER")
                    };
                    groups[i] = group with { Wan = wan, Mode = mode };
                }
            }
            var candidate = _config with { Groups = groups, CurrentPreset = display };
            var policies = LoadPolicies(candidate, _wanMap);
            PersistAtomically(candidate);
            _redirector.UpdateBindings(_wanMap.Values, policies, _health);
            _config = candidate;
            _policies = policies;
            Console.WriteLine($"IPC preset applied: {display}; groups={groups.Count}; individual rules preserved={candidate.Rules.Count}");
            return ControlResult.Ok(new { currentPreset = display, groupsChanged = groups.Count });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return ControlResult.Error("PRESET_FAILED", ex.Message);
        }
        finally { _configurationGate.Release(); }
    }

    public async Task<ControlResult> ReloadConfigAsync(CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_engineReady || _redirector is null || _health is null)
                return ControlResult.Error("ENGINE_NOT_READY", "Routing engine is not ready.");
            Config candidate;
            try
            {
                candidate = JsonSerializer.Deserialize<Config>(File.ReadAllText(_options.Paths.Config),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("Empty JSON configuration.");
                if (candidate.Interfaces.Count != _wanMap.Count || candidate.Interfaces.Any(x =>
                    !_wanMap.TryGetValue(x.Key, out var wan) || !wan.InterfaceName.Equals(x.Value.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("WAN definitions cannot change during runtime reload.");
                var policies = LoadPolicies(candidate, _wanMap);
                _redirector.UpdateBindings(_wanMap.Values, policies, _health);
                _config = candidate;
                _policies = policies;
                Console.WriteLine($"IPC configuration reloaded: rules={policies.Count}");
                return ControlResult.Ok(new { rulesLoaded = policies.Count });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                Console.WriteLine($"IPC invalid request: reloadConfig rejected: {ex.Message}");
                return ControlResult.Error("INVALID_CONFIG", ex.Message);
            }
        }
        finally { _configurationGate.Release(); }
    }

    private void PersistAtomically(Config config)
    {
        string directory = Path.GetDirectoryName(_options.Paths.Config)!;
        string temporary = Path.Combine(directory, $"config.{Guid.NewGuid():N}.tmp");
        try
        {
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporary, json);
            _ = JsonSerializer.Deserialize<Config>(File.ReadAllText(temporary),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Temporary configuration validation failed.");
            if (File.Exists(_options.Paths.Config)) File.Replace(temporary, _options.Paths.Config, null);
            else File.Move(temporary, _options.Paths.Config);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateProcessName(string process)
    {
        if (string.IsNullOrWhiteSpace(process) || process.Length > 260 ||
            !process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(process).Equals(process, StringComparison.Ordinal) ||
            process.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Process must be a valid executable file name ending in .exe.");
    }

    private static void ValidateGroupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64 || name.IndexOfAny(['\\', '/', '\0']) >= 0)
            throw new InvalidDataException("Group name must contain 1-64 characters and no path separators.");
    }
}

sealed class ProductLogger : TextWriter
{
    private readonly TextWriter _console;
    private readonly StreamWriter _all;
    private readonly StreamWriter _routing;
    private readonly bool _debug;
    private readonly Channel<(string? Value, string Line)> _queue = Channel.CreateUnbounded<(string?, string)>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _drain;

    public ProductLogger(string folder, TextWriter console, bool debug)
    {
        Directory.CreateDirectory(folder);
        _console = console;
        _debug = debug;
        _all = new StreamWriter(Path.Combine(folder, "dualwan.log"), true) { AutoFlush = true };
        _routing = new StreamWriter(Path.Combine(folder, "routing.log"), true) { AutoFlush = true };
        _drain = Task.Run(DrainAsync);
    }
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void WriteLine(string? value)
    {
        string line = $"[{DateTimeOffset.Now:O}] {value}";
        _queue.Writer.TryWrite((value, line));
    }
    private async Task DrainAsync()
    {
        await foreach (var (value, line) in _queue.Reader.ReadAllAsync())
        {
            if (_debug || value?.StartsWith("DEBUG ", StringComparison.Ordinal) != true) _console.WriteLine(line);
            _all.WriteLine(line);
            if (value?.StartsWith("ROUTING | ", StringComparison.Ordinal) == true) _routing.WriteLine(value[10..]);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _queue.Writer.TryComplete();
            _drain.GetAwaiter().GetResult();
            _all.Dispose();
            _routing.Dispose();
        }
        base.Dispose(disposing);
    }
}

sealed record RuntimePaths(string Root, string Config, string Logs, string State, string TelemetryDatabase);
sealed record RuntimeOptions(RuntimePaths Paths, bool Debug, bool IsService,
    string PipeName = ControlPipeServer.PipeName);
sealed record WanSpec(string Name, string? FriendlyName = null, string? InterfaceId = null);
sealed record Rule(string Process, string Wan, string Mode = "STRICT", bool Enabled = true);
sealed record Group(string Name, string Wan, string Mode, bool Enabled, List<string> Applications);
sealed record HealthConfig(int IntervalSeconds, int FailureThreshold, int RecoverySuccessThreshold, List<string> Targets);
sealed record Config(Dictionary<string, WanSpec> Interfaces, List<Rule> Rules, HealthConfig? Health = null,
    List<Group>? Groups = null, string CurrentPreset = "Custom");
sealed record RunState(int Pid, string Executable, long StartTicksUtc);
