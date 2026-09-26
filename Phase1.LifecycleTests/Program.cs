using System.Net;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using NetBinder.Service.NativeInterop;
using NetBinder.Service.Services;

// Every handle below is synthetic. This executable never opens WinDivert or
// changes the installed Service, adapter state, or routing table.
TestNormalStop();
TestFatalLoop();
TestRepeatedReceiveFailure();
TestTransientReceiveFailure();
TestRepeatedSendFailure();
TestFailedOpen();
TestOverlappingCleanup();
TestFatalStopRace();
TestRuntimeStatus();
Console.WriteLine("Lifecycle tests PASS (simulated WinDivert only)");

static RedirectorService Create(FakeIo io, Action<RedirectorService>? failed = null) =>
    new(new UdpRelay(), new LocalDestinationClassifier(new EmptyRoutes()), io, failed);

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Wait(Func<bool> condition, string message) =>
    Check(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(3)), message);

static void TestNormalStop()
{
    var io = new FakeIo(FakeMode.Wait);
    int failures = 0;
    using var service = Create(io, _ => Interlocked.Increment(ref failures));
    Check(service.Start(32000), "normal start failed");
    Wait(() => io.ReceiveCount > 0, "receive loop did not start");
    Check(service.IsRunning && io.OpenCount == 1, "normal start state incorrect");
    service.Stop(); service.Stop();
    Check(!service.IsRunning && io.CloseCount == 1 && failures == 0, "normal stop did not close exactly once");
}

static void TestFatalLoop()
{
    var io = new FakeIo(FakeMode.Throw);
    int failures = 0;
    using var service = Create(io, _ => Interlocked.Increment(ref failures));
    Check(service.Start(32001), "fatal-path start failed");
    Wait(() => !service.IsRunning && Volatile.Read(ref failures) == 1, "fatal path did not release interception");
    service.Stop(); service.Stop();
    Check(io.CloseCount == 1, "fatal path double-closed handle");
}

static void TestRepeatedReceiveFailure()
{
    var io = new FakeIo(FakeMode.ReceiveError);
    int failures = 0;
    using var service = Create(io, _ => Interlocked.Increment(ref failures));
    service.Start(32002);
    Wait(() => !service.IsRunning && Volatile.Read(ref failures) == 1, "receive failures did not fail open");
    Check(io.ReceiveCount == 3 && io.CloseCount == 1, "receive failure threshold or cleanup incorrect");
}

static void TestTransientReceiveFailure()
{
    var io = new FakeIo(FakeMode.TransientReceive);
    using var service = Create(io);
    Check(service.Start(32008), "transient receive start failed");
    Wait(() => io.ReceiveCount >= 3, "transient receive did not retry");
    Check(service.IsRunning, "transient receive failure stopped interception");
    service.Stop();
    Check(io.CloseCount == 1, "transient receive cleanup incorrect");
}

static void TestRepeatedSendFailure()
{
    var io = new FakeIo(FakeMode.SendError);
    int failures = 0;
    using var service = Create(io, _ => Interlocked.Increment(ref failures));
    service.Start(32003);
    Wait(() => !service.IsRunning && Volatile.Read(ref failures) == 1, "send failures did not fail open");
    Check(io.SendCount == 3 && io.CloseCount == 1, "send failure threshold or cleanup incorrect");
}

static void TestFailedOpen()
{
    var io = new FakeIo(FakeMode.OpenError);
    using var service = Create(io);
    Check(!service.Start(32004), "failed open reported success");
    service.Stop(); service.Stop();
    Check(!service.IsRunning && io.CloseCount == 0, "failed open retained a handle");
}

static void TestOverlappingCleanup()
{
    var io = new FakeIo(FakeMode.Wait);
    using var service = Create(io);
    Check(service.Start(32005), "overlap start failed");
    Wait(() => io.ReceiveCount > 0, "overlap receive did not start");
    Parallel.Invoke(service.Stop, service.Stop);
    Check(!service.IsRunning && io.CloseCount == 1, "overlapping stop double-closed handle");
}

static void TestFatalStopRace()
{
    var io = new FakeIo(FakeMode.DelayedThrow);
    int failures = 0;
    using var service = Create(io, _ => Interlocked.Increment(ref failures));
    Check(service.Start(32006), "fatal/stop race start failed");
    Wait(() => io.ReceiveCount > 0, "fatal/stop race receive did not start");
    Parallel.Invoke(io.ReleaseFailure, service.Stop);
    Wait(() => !service.IsRunning, "fatal/stop race left interception active");
    Check(io.CloseCount == 1 && failures <= 1, "fatal/stop race double-closed or double-notified");
}

static void TestRuntimeStatus()
{
    // Construct the control-plane runtime without starting its hosted Service.
    // This checks that a failed redirector cannot be reported as READY.
    Assembly assembly = typeof(RedirectorService).Assembly;
    Type pathsType = assembly.GetType("RuntimePaths", true)!;
    Type optionsType = assembly.GetType("RuntimeOptions", true)!;
    Type runtimeType = assembly.GetType("DualWanRuntime", true)!;
    string root = Path.Combine(Path.GetTempPath(), "DualWAN-LifecycleTests-" + Guid.NewGuid().ToString("N"));
    object paths = Activator.CreateInstance(pathsType, root, Path.Combine(root, "config.json"),
        Path.Combine(root, "logs"), Path.Combine(root, "state.json"), Path.Combine(root, "history.db"))!;
    object options = Activator.CreateInstance(optionsType, paths, false, false, "DualWAN.Test")!;
    object runtime = Activator.CreateInstance(runtimeType, options)!;
    var io = new FakeIo(FakeMode.Wait);
    using var redirector = Create(io);
    Check(redirector.Start(32007), "status test redirector did not start");
    runtimeType.GetField("_redirector", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, redirector);
    runtimeType.GetField("_engineReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, true);
    string Engine() => JsonSerializer.SerializeToElement(runtimeType.GetMethod("GetStatus")!.Invoke(runtime, null))
        .GetProperty("engine").GetString()!;
    Check(Engine() == "READY", "healthy redirector was not reported ready");
    runtimeType.GetMethod("OnRedirectorFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(runtime, [redirector]);
    Check(Engine() == "INACTIVE", "failed redirector was reported ready");
    Wait(() => runtimeType.GetField("_redirector", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(runtime) is null, "runtime did not finish failure cleanup");
}

sealed class EmptyRoutes : ILocalIpv4RouteSource
{
    public IReadOnlyList<LocalIpv4Route> ReadRoutes() => [];
}

enum FakeMode { Wait, Throw, DelayedThrow, ReceiveError, TransientReceive, SendError, OpenError }

sealed class FakeIo(FakeMode mode) : IWinDivertIo
{
    private readonly ManualResetEventSlim _closed = new(false);
    private readonly ManualResetEventSlim _releaseFailure = new(false);
    private int _openCount, _receiveCount, _sendCount, _closeCount;
    public int OpenCount => Volatile.Read(ref _openCount);
    public int ReceiveCount => Volatile.Read(ref _receiveCount);
    public int SendCount => Volatile.Read(ref _sendCount);
    public int CloseCount => Volatile.Read(ref _closeCount);
    public int LastError => 123;
    public void ReleaseFailure() => _releaseFailure.Set();
    public IntPtr Open(string filter)
    {
        Interlocked.Increment(ref _openCount);
        return mode == FakeMode.OpenError ? new IntPtr(-1) : new IntPtr(101);
    }
    public bool Receive(IntPtr handle, IntPtr packet, uint length, out uint received,
        ref WINDIVERT_ADDRESS address)
    {
        int call = Interlocked.Increment(ref _receiveCount);
        received = 0;
        if (mode == FakeMode.Throw) throw new InvalidOperationException("synthetic packet-loop failure");
        if (mode == FakeMode.DelayedThrow)
        {
            _releaseFailure.Wait(TimeSpan.FromSeconds(3));
            throw new InvalidOperationException("synthetic concurrent failure");
        }
        if (mode == FakeMode.ReceiveError) return false;
        if (mode == FakeMode.TransientReceive && call <= 2) return false;
        if (mode == FakeMode.SendError)
        {
            Marshal.WriteByte(packet, 0, 0x50); // Unsupported IP version; safe passthrough path.
            received = 20;
            return true;
        }
        _closed.Wait(TimeSpan.FromSeconds(3));
        return false;
    }
    public bool Send(IntPtr handle, IntPtr packet, uint length, out uint sent,
        ref WINDIVERT_ADDRESS address)
    {
        Interlocked.Increment(ref _sendCount);
        sent = 0;
        return false;
    }
    public bool Close(IntPtr handle)
    {
        Interlocked.Increment(ref _closeCount);
        _closed.Set();
        return true;
    }
}
