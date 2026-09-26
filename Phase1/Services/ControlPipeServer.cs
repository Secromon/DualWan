using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

[assembly: SupportedOSPlatform("windows")]

namespace NetBinder.Service.Services;

public sealed record ControlRule(string Process, string Wan, string Mode, bool Enabled);
public sealed record ControlGroup(string Name, string Wan, string Mode, bool Enabled,
    IReadOnlyList<string> Applications, string? OriginalName = null);
/// <summary>Stable adapter identity and a display-only label requested by the Dashboard.</summary>
public sealed record ControlWanSelection(string InterfaceId, string FriendlyName);
public sealed record ControlResult(bool Success, object? Data = null, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static ControlResult Ok(object? data = null) => new(true, data);
    public static ControlResult Error(string code, string message) => new(false, null, code, message);
}

/// <summary>Service-owned operations exposed through the versioned local pipe contract.</summary>
public interface IDualWanControlPlane
{
    object GetStatus();
    object GetWans();
    object GetAvailableInterfaces();
    object GetWanConfiguration();
    object GetRules();
    object GetGroups();
    object GetTelemetry();
    Task<ControlResult> SetRuleAsync(ControlRule rule, CancellationToken cancellationToken);
    Task<ControlResult> UpsertRuleAsync(ControlRule rule, CancellationToken cancellationToken);
    Task<ControlResult> DeleteRuleAsync(string process, CancellationToken cancellationToken);
    Task<ControlResult> UpsertGroupAsync(ControlGroup group, CancellationToken cancellationToken);
    Task<ControlResult> DeleteGroupAsync(string name, CancellationToken cancellationToken);
    Task<ControlResult> ApplyPresetAsync(string presetId, CancellationToken cancellationToken);
    Task<ControlResult> GetTelemetryHistoryAsync(string wan, DateTimeOffset from, DateTimeOffset to, string resolution);
    Task<ControlResult> GetTelemetryStorageStatusAsync();
    Task<ControlResult> SetTelemetryStoragePolicyAsync(int? retentionDays, long? maximumBytes);
    Task<ControlResult> CleanTelemetryAsync();
    Task<ControlResult> ReloadConfigAsync(CancellationToken cancellationToken);
    Task<ControlResult> SetWanConfigurationAsync(ControlWanSelection wan1, ControlWanSelection wan2,
        CancellationToken cancellationToken);
}

/// <summary>
/// Serves newline-delimited JSON requests on a local named pipe. Read commands are
/// available to the user Dashboard; mutating commands require an administrator token.
/// </summary>
public sealed class ControlPipeServer : IAsyncDisposable
{
    public const string PipeName = "DualWAN.Control";
    private const int ApiVersion = 1;
    private const int MaxRequestCharacters = 65536;
    private readonly IDualWanControlPlane _controlPlane;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private readonly ConcurrentDictionary<int, NamedPipeServerStream> _pipes = new();
    private Task? _acceptLoop;
    private int _nextClientId;

    public ControlPipeServer(IDualWanControlPlane controlPlane, string pipeName = PipeName)
    {
        _controlPlane = controlPlane;
        _pipeName = pipeName;
    }

    public Task StartAsync()
    {
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Console.WriteLine($@"IPC server started: \\.\pipe\{_pipeName}; apiVersion={ApiVersion}");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken);
                int clientId = Interlocked.Increment(ref _nextClientId);
                _pipes[clientId] = pipe!;
                var task = HandleClientAsync(clientId, pipe!, cancellationToken);
                _clients[clientId] = task;
                _ = task.ContinueWith(completedTask =>
                {
                    _clients.TryRemove(clientId, out _);
                    _pipes.TryRemove(clientId, out _);
                }, TaskScheduler.Default);
                pipe = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.WriteLine($"IPC accept error: {ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(250, cancellationToken); } catch (OperationCanceledException) { }
            }
            finally { pipe?.Dispose(); }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        // Pipe access lets normal users read status. Authorization for writes is
        // checked separately by impersonating the client on each mutating request.
        var security = new PipeSecurity();
        const PipeAccessRights localClientRights = PipeAccessRights.ReadWrite |
            PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize;
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), localClientRights,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), localClientRights,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            4096, 4096, security, HandleInheritability.None);
    }

    private async Task HandleClientAsync(int clientId, NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        if (!IsLocalClient(pipe, out string clientComputer))
        {
            Console.WriteLine($"IPC invalid request: remote client rejected; id={clientId}; computer={clientComputer}");
            pipe.Dispose();
            return;
        }
        Console.WriteLine($"IPC client connected: id={clientId}");
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
            {
                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null) break;
                    object response = await ProcessRequestAsync(pipe, line, cancellationToken);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        catch (Exception ex) { Console.WriteLine($"IPC client error: id={clientId}; {ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task<object> ProcessRequestAsync(NamedPipeServerStream pipe, string line, CancellationToken cancellationToken)
    {
        // Every response echoes requestId and apiVersion, including validation errors.
        // Invalid client input stays on the pipe; Service operation failures are
        // returned by the control plane as stable error codes.
        string requestId = "";
        try
        {
            if (line.Length > MaxRequestCharacters) return ErrorResponse(requestId, "REQUEST_TOO_LARGE", "Request exceeds 65536 characters.");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            requestId = RequiredString(root, "requestId");
            if (!root.TryGetProperty("apiVersion", out var versionElement) || versionElement.GetInt32() != ApiVersion)
                return ErrorResponse(requestId, "UNSUPPORTED_API_VERSION", "apiVersion must be 1.");
            string command = RequiredString(root, "command");
            ControlResult result = command.ToLowerInvariant() switch
            {
                "ping" => ControlResult.Ok(new { service = "DualWANService", version = ServiceVersion() }),
                "getstatus" => ControlResult.Ok(_controlPlane.GetStatus()),
                "getwans" => ControlResult.Ok(_controlPlane.GetWans()),
                "getavailableinterfaces" => ControlResult.Ok(_controlPlane.GetAvailableInterfaces()),
                "getwanconfiguration" => ControlResult.Ok(_controlPlane.GetWanConfiguration()),
                "getrules" => ControlResult.Ok(_controlPlane.GetRules()),
                "getgroups" => ControlResult.Ok(_controlPlane.GetGroups()),
                "gettelemetry" => ControlResult.Ok(_controlPlane.GetTelemetry()),
                "setrule" => await SetRuleAsync(pipe, root, cancellationToken),
                "upsertrule" => await UpsertRuleAsync(pipe, root, cancellationToken),
                "deleterule" => await DeleteRuleAsync(pipe, root, cancellationToken),
                "upsertgroup" => await UpsertGroupAsync(pipe, root, cancellationToken),
                "deletegroup" => await DeleteGroupAsync(pipe, root, cancellationToken),
                "applypreset" => await ApplyPresetAsync(pipe, root, cancellationToken),
                "gettelemetryhistory" => await GetTelemetryHistoryAsync(root),
                "gettelemetrystoragestatus" => await _controlPlane.GetTelemetryStorageStatusAsync(),
                "settelemetrystoragepolicy" => await SetTelemetryStoragePolicyAsync(pipe, root),
                "cleantelemetry" => await MutateAsync(pipe, _controlPlane.CleanTelemetryAsync),
                "reloadconfig" => await MutateAsync(pipe, () => _controlPlane.ReloadConfigAsync(cancellationToken)),
                "setwanconfiguration" => await SetWanConfigurationAsync(pipe, root, cancellationToken),
                _ => ControlResult.Error("UNKNOWN_COMMAND", $"Unsupported command: {command}.")
            };
            return result.Success
                ? new { apiVersion = ApiVersion, requestId, success = true, data = result.Data }
                : ErrorResponse(requestId, result.ErrorCode!, result.ErrorMessage!);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or FormatException)
        {
            Console.WriteLine($"IPC invalid request: {ex.Message}");
            return ErrorResponse(requestId, "INVALID_REQUEST", ex.Message);
        }
    }

    private async Task<ControlResult> SetRuleAsync(NamedPipeServerStream pipe, JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!IsMutationAuthorized(pipe)) return ControlResult.Error("UNAUTHORIZED", "Administrator rights are required.");
        return await _controlPlane.SetRuleAsync(ReadRule(root), cancellationToken);
    }

    private async Task<ControlResult> UpsertRuleAsync(NamedPipeServerStream pipe, JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!IsMutationAuthorized(pipe)) return ControlResult.Error("UNAUTHORIZED", "Administrator rights are required.");
        return await _controlPlane.UpsertRuleAsync(ReadRule(root), cancellationToken);
    }

    private async Task<ControlResult> DeleteRuleAsync(NamedPipeServerStream pipe, JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!IsMutationAuthorized(pipe)) return ControlResult.Error("UNAUTHORIZED", "Administrator rights are required.");
        return await _controlPlane.DeleteRuleAsync(RequiredString(root, "process"), cancellationToken);
    }

    private static ControlRule ReadRule(JsonElement root)
    {
        if (!root.TryGetProperty("rule", out var ruleElement) || ruleElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("rule object is required.");
        bool enabledValue = true;
        if (ruleElement.TryGetProperty("enabled", out var enabled))
        {
            if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("enabled must be a boolean.");
            enabledValue = enabled.GetBoolean();
        }
        return new ControlRule(RequiredString(ruleElement, "process"), RequiredString(ruleElement, "wan"),
            RequiredString(ruleElement, "mode"), enabledValue);
    }

    private async Task<ControlResult> UpsertGroupAsync(NamedPipeServerStream pipe, JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!IsMutationAuthorized(pipe)) return ControlResult.Error("UNAUTHORIZED", "Administrator rights are required.");
        if (!root.TryGetProperty("group", out var element) || element.ValueKind != JsonValueKind.Object)
            return ControlResult.Error("INVALID_GROUP", "group object is required.");
        bool enabled = true;
        bool enabledProvided = element.TryGetProperty("enabled", out var enabledElement);
        if (enabledProvided)
        {
            if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return ControlResult.Error("INVALID_GROUP", "enabled must be a boolean.");
            enabled = enabledElement.GetBoolean();
        }
        if (!element.TryGetProperty("applications", out var apps) || apps.ValueKind != JsonValueKind.Array)
            return ControlResult.Error("INVALID_GROUP", "applications must be an array.");
        var applications = new List<string>();
        foreach (var app in apps.EnumerateArray())
        {
            if (app.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(app.GetString()))
                return ControlResult.Error("INVALID_GROUP", "applications must contain process names.");
            applications.Add(app.GetString()!);
        }
        string? originalName = null;
        if (element.TryGetProperty("originalName", out var original))
        {
            if (original.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(original.GetString()))
                return ControlResult.Error("INVALID_GROUP", "originalName must be a non-empty string.");
            originalName = original.GetString()!.Trim();
        }
        if (originalName is not null && !enabledProvided)
            return ControlResult.Error("INVALID_GROUP", "enabled is required when editing a group.");
        var group = new ControlGroup(RequiredString(element, "name"), RequiredString(element, "wan"),
            RequiredString(element, "mode"), enabled, applications, originalName);
        return await _controlPlane.UpsertGroupAsync(group, cancellationToken);
    }

    private async Task<ControlResult> DeleteGroupAsync(NamedPipeServerStream pipe, JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!IsMutationAuthorized(pipe)) return ControlResult.Error("UNAUTHORIZED", "Administrator rights are required.");
        return await _controlPlane.DeleteGroupAsync(RequiredString(root, "name"), cancellationToken);
    }

    private async Task<ControlResult> ApplyPresetAsync(NamedPipeServerStream pipe, JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!IsMutationAuthorized(pipe)) return ControlResult.Error("UNAUTHORIZED", "Administrator rights are required.");
        return await _controlPlane.ApplyPresetAsync(RequiredString(root, "presetId"), cancellationToken);
    }

    private Task<ControlResult> GetTelemetryHistoryAsync(JsonElement root)
    {
        string wan = RequiredString(root, "wan");
        string resolution = root.TryGetProperty("resolution", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "auto";
        if (!root.TryGetProperty("from", out var fromElement) || !fromElement.TryGetInt64(out long from) ||
            !root.TryGetProperty("to", out var toElement) || !toElement.TryGetInt64(out long to))
            throw new InvalidDataException("from and to Unix timestamps are required.");
        return _controlPlane.GetTelemetryHistoryAsync(wan, DateTimeOffset.FromUnixTimeSeconds(from),
            DateTimeOffset.FromUnixTimeSeconds(to), resolution);
    }

    private async Task<ControlResult> SetTelemetryStoragePolicyAsync(NamedPipeServerStream pipe, JsonElement root)
    {
        if (!IsMutationAuthorized(pipe)) return ControlResult.Error("UNAUTHORIZED", "Administrator rights are required.");
        int? days = null; long? bytes = null;
        if (root.TryGetProperty("retentionDays", out var d) && d.ValueKind != JsonValueKind.Null) days = d.GetInt32();
        if (root.TryGetProperty("maximumBytes", out var b) && b.ValueKind != JsonValueKind.Null) bytes = b.GetInt64();
        return await _controlPlane.SetTelemetryStoragePolicyAsync(days, bytes);
    }

    private async Task<ControlResult> SetWanConfigurationAsync(NamedPipeServerStream pipe, JsonElement root,
        CancellationToken cancellationToken)
    {
        // A WAN write changes machine-wide routing and ProgramData configuration,
        // so accepting it from an unelevated Dashboard would cross the trust boundary.
        if (!IsMutationAuthorized(pipe)) return ControlResult.Error("UNAUTHORIZED", "Administrator rights are required.");
        if (!root.TryGetProperty("wan1", out var first) || first.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("wan2", out var second) || second.ValueKind != JsonValueKind.Object)
            return ControlResult.Error("INVALID_WAN", "wan1 and wan2 objects are required.");
        static ControlWanSelection ReadSelection(JsonElement element) => new(
            RequiredString(element, "interfaceId"),
            element.TryGetProperty("friendlyName", out var friendly) && friendly.ValueKind == JsonValueKind.String
                ? friendly.GetString() ?? "" : "");
        return await _controlPlane.SetWanConfigurationAsync(ReadSelection(first), ReadSelection(second), cancellationToken);
    }

    private static async Task<ControlResult> MutateAsync(NamedPipeServerStream pipe,
        Func<Task<ControlResult>> mutation)
        => IsMutationAuthorized(pipe)
            ? await mutation()
            : ControlResult.Error("UNAUTHORIZED", "Administrator rights are required.");

    private static bool IsMutationAuthorized(NamedPipeServerStream pipe)
    {
        // Inspect the connected caller, not the LocalSystem identity of this Service.
        bool authorized = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                authorized = identity.IsSystem || principal.IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch { authorized = false; }
        return authorized;
    }

    private static bool IsLocalClient(NamedPipeServerStream pipe, out string clientComputer)
    {
        var name = new StringBuilder(256);
        if (!GetNamedPipeClientComputerName(pipe.SafePipeHandle, name, (uint)name.Capacity))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 229) // ERROR_PIPE_LOCAL
            {
                clientComputer = Environment.MachineName;
                return true;
            }
            clientComputer = $"UNKNOWN(error={error})";
            return false;
        }
        string client = name.ToString().TrimStart('\\');
        clientComputer = client;
        return client.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            client.Equals(".", StringComparison.OrdinalIgnoreCase) ||
            client.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerName(SafePipeHandle pipe, StringBuilder clientComputerName,
        uint clientComputerNameLength);

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"{name} is required.");
        return property.GetString()!;
    }

    private static object ErrorResponse(string requestId, string code, string message)
        => new { apiVersion = ApiVersion, requestId, success = false, error = new { code, message } };

    private static string ServiceVersion()
        => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    public async ValueTask DisposeAsync()
    {
        // Closing active pipes releases readers blocked on I/O before awaiting clients.
        _cts.Cancel();
        foreach (var pipe in _pipes.Values) pipe.Dispose();
        if (_acceptLoop is not null)
            try { await _acceptLoop; } catch (OperationCanceledException) { }
        Task[] clients = _clients.Values.ToArray();
        if (clients.Length > 0)
            try { await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _cts.Dispose();
        Console.WriteLine("IPC server stopped");
    }
}
