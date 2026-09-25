using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Application = System.Windows.Application;

namespace DualWAN.Dashboard;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activationEvent;
    private System.Windows.Threading.DispatcherTimer? _activationTimer;
    private const string ActivationEvent = @"Local\DualWAN.Dashboard.Activate";

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) => LogCrash("Dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => LogCrash("TaskScheduler", args.Exception);
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0].Equals("--ipc-write", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(await RunWriteHelperAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] is "--service-start" or "--service-stop")
        {
            bool start = e.Args[0].Equals("--service-start", StringComparison.OrdinalIgnoreCase);
            Shutdown(await Task.Run(() => WindowsServiceControl.SetRunning(start)));
            return;
        }

        _instanceMutex = new Mutex(true, @"Local\DualWAN.Dashboard", out bool firstInstance);
        _ownsMutex = firstInstance;
        if (!firstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            try
            {
                using var activation = EventWaitHandle.OpenExisting(ActivationEvent);
                activation.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(0);
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEvent);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        _activationTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _activationTimer.Tick += (_, _) => { if (_activationEvent?.WaitOne(0) == true) window.OpenFromTray(); };
        _activationTimer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationTimer?.Stop();
        _activationEvent?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DualWAN", "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "dashboard.log"),
                $"[{DateTimeOffset.Now:O}] {source} PID={Environment.ProcessId}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch { /* Logging must never hide the original exception. */ }
    }

    private static async Task<int> RunWriteHelperAsync(string encodedRequest)
    {
        try
        {
            string request = Encoding.UTF8.GetString(Convert.FromBase64String(encodedRequest));
            using var requestDocument = JsonDocument.Parse(request);
            string command = requestDocument.RootElement.GetProperty("command").GetString() ?? "";
            if (command is not ("upsertRule" or "deleteRule" or "upsertGroup" or "deleteGroup" or "applyPreset" or "setTelemetryStoragePolicy" or "cleanTelemetry" or "setWanConfiguration")) return 3;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var pipe = new NamedPipeClientStream(".", "DualWAN.Control", PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            await writer.WriteLineAsync(request);
            string response = await reader.ReadLineAsync(timeout.Token) ?? "";
            using var responseDocument = JsonDocument.Parse(response);
            if (responseDocument.RootElement.GetProperty("success").GetBoolean()) return 0;
            string code = responseDocument.RootElement.GetProperty("error").GetProperty("code").GetString() ?? "";
            return code switch
            {
                "UNAUTHORIZED" => 4,
                "PERSIST_FAILED" => 5,
                "ENGINE_NOT_READY" => 2,
                _ => 3
            };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            return 2;
        }
        catch { return 6; }
    }
}

internal enum DualWanServiceState { Unknown=0, Stopped=1, StartPending=2, StopPending=3, Running=4 }

internal static class WindowsServiceControl
{
    private const string ServiceName="DualWANService";
    private const uint ScManagerConnect=0x0001, ServiceQueryStatus=0x0004, ServiceStart=0x0010, ServiceStop=0x0020, ControlStop=0x00000001;

    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint ServiceType,CurrentState,ControlsAccepted,Win32ExitCode,ServiceSpecificExitCode,CheckPoint,WaitHint; }
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern IntPtr OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern IntPtr OpenService(IntPtr manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool QueryServiceStatus(IntPtr service,out ServiceStatus status);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool StartService(IntPtr service,int argumentCount,IntPtr arguments);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool ControlService(IntPtr service,uint control,out ServiceStatus status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);

    internal static DualWanServiceState Query()
    {
        IntPtr manager=IntPtr.Zero,service=IntPtr.Zero;
        try
        {
            manager=OpenSCManager(null,null,ScManagerConnect);if(manager==IntPtr.Zero)return DualWanServiceState.Unknown;
            service=OpenService(manager,ServiceName,ServiceQueryStatus);if(service==IntPtr.Zero)return DualWanServiceState.Unknown;
            return QueryServiceStatus(service,out var status)?(DualWanServiceState)status.CurrentState:DualWanServiceState.Unknown;
        }
        finally { if(service!=IntPtr.Zero)CloseServiceHandle(service);if(manager!=IntPtr.Zero)CloseServiceHandle(manager); }
    }

    internal static int SetRunning(bool start)
    {
        IntPtr manager=IntPtr.Zero,service=IntPtr.Zero;
        try
        {
            manager=OpenSCManager(null,null,ScManagerConnect);if(manager==IntPtr.Zero)return 2;
            uint access=ServiceQueryStatus|(start?ServiceStart:ServiceStop);
            service=OpenService(manager,ServiceName,access);if(service==IntPtr.Zero)return 2;
            if(!QueryServiceStatus(service,out var status))return 2;
            if(start&&status.CurrentState!=4&&!StartService(service,0,IntPtr.Zero)&&Marshal.GetLastWin32Error()!=1056)return 3;
            if(!start&&status.CurrentState!=1&&!ControlService(service,ControlStop,out status)&&Marshal.GetLastWin32Error()!=1062)return 3;
            uint target=start?4u:1u;
            for(int i=0;i<120;i++){if(QueryServiceStatus(service,out status)&&status.CurrentState==target)return 0;Thread.Sleep(250);}
            return 4;
        }
        catch(Exception ex){System.Diagnostics.Debug.WriteLine(ex);return 5;}
        finally { if(service!=IntPtr.Zero)CloseServiceHandle(service);if(manager!=IntPtr.Zero)CloseServiceHandle(manager); }
    }
}
