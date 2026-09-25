using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Polyline = System.Windows.Shapes.Polyline;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using System.Reflection;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace DualWAN.Dashboard;

public partial class MainWindow : Window
{
    private const string PipeName = "DualWAN.Control";
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly LocalizationService _loc = new();
    private readonly ObservableCollection<RuleRow> _rules = [];
    private readonly ObservableCollection<GroupRow> _groups = [];
    private readonly List<WanChoice> _wans = [];
    private bool _wanSettingsSaving;
    private bool _refreshing;
    private bool _rulesRefreshing;
    private bool _groupsRefreshing;
    private bool _rulesEverLoaded;
    private bool _groupsEverLoaded;
    private bool _serviceOperation;
    private string? _currentPreset;
    private int? _historyPointCount;
    private JsonElement? _lastTelemetry;
    private bool _online;
    private readonly string[] _wanStates = ["UNKNOWN", "UNKNOWN"];
    private bool _exitRequested;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _trayOpen = new();
    private readonly Forms.ToolStripMenuItem _trayService = new();
    private readonly Forms.ToolStripMenuItem _trayExit = new();

    public MainWindow()
    {
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/DualWAN.ico"));
        var trayIcon = new Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "DualWAN.ico"));
        _tray = new Forms.NotifyIcon { Icon = trayIcon, Visible = true };
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.AddRange(new Forms.ToolStripItem[] { _trayOpen, _trayService, _trayExit });
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenFromTray);
        _trayOpen.Click += (_, _) => Dispatcher.Invoke(OpenFromTray);
        _trayService.Click += (_, _) => Dispatcher.Invoke(() => ServiceControl_Click(this, new RoutedEventArgs()));
        _trayExit.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _exitRequested = true;
            Close();
            Application.Current.Shutdown();
        }));
        StartupCheck.IsChecked = DashboardStartup.IsEnabled;
        AboutVersion.Text = $"DualWAN {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} — by GDM — GPLv3";
        VersionText.Text = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        RulesGrid.ItemsSource = _rules;
        GroupsGrid.ItemsSource = _groups;
        InitializeApplicationsView();
        ApplicationsList.ItemsSource = _applicationView;
        LanguageSelect.ItemsSource = _loc.Packs;
        LanguageSelect.SelectedValue = _loc.CurrentCode;
        ThemeSelect.SelectedValue = _loc.CurrentTheme;
        ApplyTheme();
        ApplyLanguage();
        MainTabs.SelectedIndex = 0;
        NavDashboard.IsChecked = true;
        UpdateNavigation();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            await RefreshAsync();
            if (!_timer.IsEnabled) _timer.Start();
        };
        Closing += (_, e) => { if (!_exitRequested) { e.Cancel = true; Dispatcher.BeginInvoke(new Action(() => { if (!_exitRequested) Hide(); })); } };
        Closed += (_, _) => { _timer.Stop(); _tray.Visible = false; _tray.Dispose(); trayMenu.Dispose(); trayIcon.Dispose(); };
    }

    public void OpenFromTray()
    {
        if (_exitRequested) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        DashboardStartup.IsEnabled = StartupCheck.IsChecked == true;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            JsonElement data = await SendReadAsync("getTelemetry");
            _lastTelemetry = data;
            Render(data);
            SetOnline(true);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or InvalidDataException)
        {
            _lastTelemetry = null;
            SetOnline(false);
        }
        finally { _refreshing = false; }
    }

    private static async Task<JsonElement> SendReadAsync(string command)
        => await SendRequestAsync(new { apiVersion = 1, requestId = Guid.NewGuid().ToString("N"), command });

    private static async Task<JsonElement> SendRequestAsync(object request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        string response = await reader.ReadLineAsync(timeout.Token)
            ?? throw new IOException("The service closed the control pipe.");
        using var document = JsonDocument.Parse(response);
        if (!document.RootElement.GetProperty("success").GetBoolean())
            throw new InvalidDataException("The service rejected the request.");
        return document.RootElement.GetProperty("data").Clone();
    }

    private void Render(JsonElement data)
    {
        var service = data.GetProperty("service");
        ServiceUptime.Text = $"{_loc.Text("dashboard.serviceUptime")} {FormatDuration(GetInt64(service, "uptimeSeconds"))}";
        FlowSummary.Text = $"{_loc.Text("dashboard.active")} {GetInt64(service, "activeFlows")}  •  {_loc.Text("dashboard.routed")} {GetInt64(service, "totalFlows")}  •  {_loc.Text("dashboard.failed")} {GetInt64(service, "failedFlows")}  •  {_loc.Text("dashboard.failover")} {GetInt64(service, "failoverCount")}  •  {_loc.Text("dashboard.failback")} {GetInt64(service, "failbackCount")}  •  {_loc.Text("dashboard.strictFailures")} {GetInt64(service, "strictFailures")}";

        foreach (var wan in data.GetProperty("wans").EnumerateArray())
        {
            string id = wan.GetProperty("id").GetString() ?? "";
            if (id.Equals("WAN1", StringComparison.OrdinalIgnoreCase)) RenderWan(wan, 1);
            if (id.Equals("WAN2", StringComparison.OrdinalIgnoreCase)) RenderWan(wan, 2);
        }
    }

    private void RenderWan(JsonElement wan, int number)
    {
        string id = wan.GetProperty("id").GetString() ?? $"WAN{number}";
        string name = wan.GetProperty("name").GetString() ?? _loc.Text("common.unknown");
        string state = wan.GetProperty("state").GetString() ?? "UNKNOWN";
        _wanStates[number - 1] = state;
        string ipv4 = wan.GetProperty("ipv4").GetString() ?? "—";
        long ifIndex = GetInt64(wan, "ifIndex");
        string current = FormatMilliseconds(GetDouble(wan, "latencyMs"));
        string minimum = FormatMilliseconds(GetDouble(wan, "latencyMinMs"), false);
        string average = FormatMilliseconds(GetDouble(wan, "latencyAvgMs"), false);
        string maximum = FormatMilliseconds(GetDouble(wan, "latencyMaxMs"), false);

        TextBlock title = number == 1 ? Wan1Title : Wan2Title;
        TextBlock address = number == 1 ? Wan1Address : Wan2Address;
        TextBlock stateText = number == 1 ? Wan1State : Wan2State;
        Border badge = number == 1 ? Wan1StateBadge : Wan2StateBadge;
        TextBlock rxRate = number == 1 ? Wan1RxRate : Wan2RxRate;
        TextBlock txRate = number == 1 ? Wan1TxRate : Wan2TxRate;
        TextBlock latency = number == 1 ? Wan1Latency : Wan2Latency;
        TextBlock latencyRange = number == 1 ? Wan1LatencyRange : Wan2LatencyRange;
        TextBlock loss = number == 1 ? Wan1Loss : Wan2Loss;
        TextBlock uptime = number == 1 ? Wan1Uptime : Wan2Uptime;
        TextBlock rxTotal = number == 1 ? Wan1RxTotal : Wan2RxTotal;
        TextBlock txTotal = number == 1 ? Wan1TxTotal : Wan2TxTotal;

        title.Text = $"{id}  /  {name}";
        (number == 1 ? HistoryWan1Title : HistoryWan2Title).Text = $"{id} — {name}";
        (number == 1 ? HistoryWan1Adapter : HistoryWan2Adapter).Text = AdapterName(ifIndex);
        address.Text = $"IPv4 {ipv4}  •  IfIndex {ifIndex}";
        stateText.Text = $"●  {WanStateText(state)}";
        badge.Background = StateBrush(state);
        rxRate.Text = FormatBytes(GetDouble(wan, "rxBytesPerSecond"), true);
        txRate.Text = FormatBytes(GetDouble(wan, "txBytesPerSecond"), true);
        latency.Text = $"{_loc.Text("dashboard.current")} {current}";
        latencyRange.Text = $"{_loc.Text("dashboard.min")} {minimum}  •  {_loc.Text("dashboard.avg")} {average}  •  {_loc.Text("dashboard.max")} {maximum}";
        double? lossValue = GetDouble(wan, "probeLossPercent");
        loss.Text = lossValue.HasValue ? $"{lossValue.Value:F1}%" : "—";
        long? upSeconds = GetNullableInt64(wan, "upSeconds");
        uptime.Text = upSeconds.HasValue ? FormatDuration(upSeconds.Value) : "—";
        rxTotal.Text = $"RX {FormatBytes(GetDouble(wan, "rxTotalBytes"), false)}";
        txTotal.Text = $"TX {FormatBytes(GetDouble(wan, "txTotalBytes"), false)}";
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs)) return;
        if (MainTabs.SelectedIndex == 1) { MainTabs.SelectedIndex = 6; return; }
        UpdateNavigation();
        if (MainTabs.SelectedIndex == 2) { await RefreshRulesAsync(); await RefreshGroupsAsync(); }
        if (MainTabs.SelectedIndex == 3 && !_groupsRefreshing) await RefreshGroupsAsync();
        if (MainTabs.SelectedIndex == 4) await LoadHistoryAsync();
        if (MainTabs.SelectedIndex == 5) { await LoadStorageStatusAsync(); await LoadWanSettingsAsync(); }
        if (MainTabs.SelectedIndex == 6) { await RefreshRulesAsync(); await RefreshGroupsAsync(); }
        if (MainTabs.SelectedIndex == 7) RefreshHelp();
    }

    private async void RefreshRules_Click(object sender, RoutedEventArgs e) => await RefreshRulesAsync();

    private async Task RefreshRulesAsync()
    {
        if (_rulesRefreshing) return;
        _rulesRefreshing = true;
        RefreshRulesButton.IsEnabled = false;
        RulesStatus.Text = _loc.Text("rules.loading");
        try
        {
            JsonElement wans = await SendReadAsync("getWans");
            JsonElement rules = await SendReadAsync("getRules");
            _wans.Clear();
            foreach (var wan in wans.GetProperty("wans").EnumerateArray())
            {
                string id = wan.GetProperty("id").GetString() ?? "";
                string name = wan.GetProperty("name").GetString() ?? id;
                _wans.Add(new WanChoice(id, $"{id} — {name}"));
            }

            _rules.Clear();
            foreach (var rule in rules.GetProperty("rules").EnumerateArray())
            {
                string process = rule.GetProperty("process").GetString() ?? "";
                string wan = rule.GetProperty("wan").GetString() ?? "";
                string mode = rule.GetProperty("mode").GetString() ?? "";
                bool enabled = rule.GetProperty("enabled").GetBoolean();
                string wanDisplay = _wans.FirstOrDefault(x => x.Id.Equals(wan, StringComparison.OrdinalIgnoreCase))?.Display ?? wan;
                _rules.Add(new RuleRow(process, wan, wanDisplay, mode, enabled, _loc.Text(enabled?"common.enabled":"common.disabled")));
            }
            RulesStatus.Text = string.Format(_loc.Text("rules.loaded"),_rules.Count);
            RulesGrid.IsEnabled = true;
            AddRuleButton.IsEnabled = true;
            _rulesEverLoaded = true;
            RefreshApplications();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or InvalidDataException)
        {
            _rules.Clear();
            RulesStatus.Text = _loc.Text("rules.offline");
            RulesGrid.IsEnabled = false;
            AddRuleButton.IsEnabled = false;
            RefreshApplications();
        }
        finally
        {
            RefreshRulesButton.IsEnabled = true;
            _rulesRefreshing = false;
        }
    }

    private async void RefreshGroups_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRulesAsync();
        await RefreshGroupsAsync();
    }

    private async Task RefreshGroupsAsync()
    {
        if (_groupsRefreshing) return;
        _groupsRefreshing = true;
        RefreshGroupsButton.IsEnabled = false;
        GroupsStatus.Text = _loc.Text("groups.loading");
        try
        {
            JsonElement wans = await SendReadAsync("getWans");
            JsonElement data = await SendReadAsync("getGroups");
            _wans.Clear();
            foreach (var wan in wans.GetProperty("wans").EnumerateArray())
            {
                string id = wan.GetProperty("id").GetString() ?? "";
                string name = wan.GetProperty("name").GetString() ?? id;
                _wans.Add(new WanChoice(id, $"{id} — {name}"));
            }
            _groups.Clear();
            foreach (var group in data.GetProperty("groups").EnumerateArray())
            {
                string name = group.GetProperty("name").GetString() ?? "";
                string wan = group.GetProperty("wan").GetString() ?? "";
                string mode = group.GetProperty("mode").GetString() ?? "";
                bool enabled = group.GetProperty("enabled").GetBoolean();
                string[] apps = group.GetProperty("applications").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                string wanDisplay = _wans.FirstOrDefault(x => x.Id.Equals(wan, StringComparison.OrdinalIgnoreCase))?.Display ?? wan;
                _groups.Add(new GroupRow(name, wan, wanDisplay, mode, enabled, apps,
                    string.Format(_loc.Text("groups.appCount"), apps.Length),
                    _loc.Text(enabled ? "common.enabled" : "groups.disabled"))
                    { PolicyDisplay = GroupPolicyLabel(wan, mode, enabled) });
            }
            _currentPreset = data.GetProperty("currentPreset").GetString();
            string preset = PresetName(_currentPreset);
            CurrentPresetText.Text = $"{_loc.Text("presets.current")}: {preset}";
            GroupsStatus.Text = string.Format(_loc.Text("groups.loaded"),_groups.Count,preset);
            GroupsGrid.IsEnabled = true;
            AddGroupButton.IsEnabled = true;
            _groupsEverLoaded = true;
            RefreshApplications();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or InvalidDataException)
        {
            _currentPreset = null;
            _groups.Clear();
            GroupsStatus.Text = _loc.Text("groups.offline");
            CurrentPresetText.Text = $"{_loc.Text("presets.current")}: {_loc.Text("common.unavailable")}";
            GroupsGrid.IsEnabled = false;
            AddGroupButton.IsEnabled = false;
            RefreshApplications();
        }
        finally
        {
            RefreshGroupsButton.IsEnabled = true;
            _groupsRefreshing = false;
        }
    }

    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var editor = new RuleEditorWindow(_loc, _wans, null) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
            await ApplyRuleAsync(editor.Result);
    }

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RuleRow rule) return;
        var editor = new RuleEditorWindow(_loc, _wans, rule) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
            await ApplyRuleAsync(editor.Result);
    }

    private async void ToggleRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RuleRow rule) return;
        await ApplyRuleAsync(rule with { Enabled = !rule.Enabled });
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RuleRow rule) return;
        if (MessageBox.Show(this, string.Format(_loc.Text("rules.deleteConfirm"), rule.Process), "DualWAN",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunWriteAsync(new
        {
            apiVersion = 1,
            requestId = Guid.NewGuid().ToString("N"),
            command = "deleteRule",
            process = rule.Process
        });
    }

    private async void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var editor = new GroupEditorWindow(this, _wans, _groups, null) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null) await ApplyGroupAsync(editor.Result);
    }

    private async void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GroupRow group) return;
        var editor = new GroupEditorWindow(this, _wans, _groups, group) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null) await ApplyGroupAsync(editor.Result);
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GroupRow group) return;
        if (MessageBox.Show(this, string.Format(_loc.Text("groups.deleteConfirm"), group.Name), "DualWAN",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunWriteAsync(new
        {
            apiVersion = 1, requestId = Guid.NewGuid().ToString("N"), command = "deleteGroup", name = group.Name
        });
    }

    private async Task<bool> ApplyGroupAsync(GroupRow group)
    {
        return await RunWriteAsync(new
        {
            apiVersion = 1,
            requestId = Guid.NewGuid().ToString("N"),
            command = "upsertGroup",
            group = new { name = group.Name, wan = group.Wan, mode = group.Mode, enabled = group.Enabled, applications = group.Applications }
        });
    }

    private async void Preset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string presetId) return;
        string name = PresetName(presetId);
        string previewKey = presetId switch
        {
            "5g-priority" => "presets.priority.preview",
            "save-5g" => "presets.save.preview",
            "only-5g" => "presets.only.preview",
            _ => "presets.custom.preview"
        };
        if (MessageBox.Show(this, string.Format(_loc.Text("presets.applyConfirm"), name, _loc.Text(previewKey)),
                _loc.Text("presets.previewTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunWriteAsync(new
        {
            apiVersion = 1, requestId = Guid.NewGuid().ToString("N"), command = "applyPreset", presetId
        });
    }

    private string PresetName(string? value) => value?.ToLowerInvariant() switch
    {
        "5g-priority" or "5g priority" => _loc.Text("presets.priority.name"),
        "save-5g" or "save 5g" => _loc.Text("presets.save.name"),
        "only-5g" or "only 5g" => _loc.Text("presets.only.name"),
        "custom" or null => _loc.Text("presets.custom.name"),
        _ => value
    };

    private async Task<bool> ApplyRuleAsync(RuleRow rule)
    {
        return await RunWriteAsync(new
        {
            apiVersion = 1,
            requestId = Guid.NewGuid().ToString("N"),
            command = "upsertRule",
            rule = new { process = rule.Process, wan = rule.Wan, mode = rule.Mode, enabled = rule.Enabled }
        });
    }

    private async Task<bool> RunWriteAsync(object request, bool wanConfiguration = false)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException(_loc.Text("error.applicationPath")),
                UseShellExecute = true,
                Verb = "runas"
            };
            start.ArgumentList.Add("--ipc-write");
            start.ArgumentList.Add(encoded);
            using var process = Process.Start(start) ?? throw new InvalidOperationException(_loc.Text("error.helperStart"));
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                if (MainTabs.SelectedIndex == 6) await RefreshApplicationsAfterWriteAsync();
                else if (MainTabs.SelectedIndex is 2 or 3) await RefreshGroupsAsync();
                else if (MainTabs.SelectedIndex == 5 && !wanConfiguration) await LoadStorageStatusAsync();
                return true;
            }
            string message = process.ExitCode switch
            {
                2 => _loc.Text("error.serviceOffline"),
                3 => _loc.Text(wanConfiguration ? "settings.wan.error" : "error.invalidRule"),
                4 => _loc.Text("error.permissionDenied"),
                5 => _loc.Text(wanConfiguration ? "settings.wan.error" : "error.saveFailed"),
                _ => _loc.Text("common.error")
            };
            MessageBox.Show(this, message, "DualWAN", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            RulesStatus.Text = _loc.Text("common.uacCancelled");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            MessageBox.Show(this, _loc.Text("common.error"), "DualWAN", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async void HistoryRefresh_Click(object sender, RoutedEventArgs e) => await LoadHistoryAsync();

    private async void HistoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs is not null && MainTabs.SelectedIndex == 4 && HistoryRange.SelectedItem is not null)
            await LoadHistoryAsync();
    }

    private async Task RefreshApplicationsAfterWriteAsync()
    {
        while (_rulesRefreshing || _groupsRefreshing) await Task.Delay(25);
        await RefreshRulesAsync();
        await RefreshGroupsAsync();
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            string range = ((ComboBoxItem)HistoryRange.SelectedItem).Content.ToString()!;
            TimeSpan span = range switch { "1h" => TimeSpan.FromHours(1), "24h" => TimeSpan.FromHours(24), "7d" => TimeSpan.FromDays(7), _ => TimeSpan.FromDays(30) };
            long to = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), from = to - (long)span.TotalSeconds;
            var series = new List<(string Wan, JsonElement[] Points)>();
            HistoryStatus.Text = _loc.Text("history.loading");
            foreach (string wan in new[] { "WAN1", "WAN2" })
            {
                try
                {
                    JsonElement data = await SendRequestAsync(new { apiVersion=1,requestId=Guid.NewGuid().ToString("N"),command="getTelemetryHistory",wan,from,to,resolution="auto" });
                    series.Add((wan, data.GetProperty("points").EnumerateArray().Select(x => x.Clone()).ToArray()));
                }
                catch (Exception ex) { Debug.WriteLine(ex); series.Add((wan, [])); }
            }
            DrawHistoryPanels(series, from, to);
            _historyPointCount = series.Sum(x => x.Points.Length);
            HistoryStatus.Text = string.Format(_loc.Text("history.points"), _historyPointCount);
        }
        catch (Exception ex) { Debug.WriteLine(ex); _historyPointCount = null; HistoryStatus.Text = _loc.Text("history.empty"); ClearHistoryPanels(); }
    }

    private static void DrawLine(Canvas canvas, IReadOnlyList<double> values, Brush brush, double maximum)
    {
        if (values.Count < 2 || maximum <= 0) return;
        double width=Math.Max(100,canvas.ActualWidth),height=Math.Max(45,canvas.ActualHeight);
        var line=new Polyline{Stroke=brush,StrokeThickness=2};
        for(int i=0;i<values.Count;i++)line.Points.Add(new Point(i*width/(values.Count-1),height-Math.Min(height,values[i]/maximum*height)));
        canvas.Children.Add(line);
    }

    private async Task LoadStorageStatusAsync()
    {
        try
        {
            JsonElement data=await SendReadAsync("getTelemetryStorageStatus");
            long size=data.GetProperty("currentSizeBytes").GetInt64();
            string oldest=data.GetProperty("oldestRecord").ValueKind==JsonValueKind.Number?DateTimeOffset.FromUnixTimeSeconds(data.GetProperty("oldestRecord").GetInt64()).LocalDateTime.ToString("g"):"—";
            StorageStatus.Text=$"{_loc.Text("settings.currentSize")}: {FormatBytes(size,false)}   •   {_loc.Text("settings.oldest")}: {oldest}";
        }
        catch { StorageStatus.Text=_loc.Text("common.offline"); }
    }

    private async Task LoadWanSettingsAsync()
    {
        if (_wanSettingsSaving) return;
        try
        {
            JsonElement available = await SendReadAsync("getAvailableInterfaces");
            JsonElement config = await SendReadAsync("getWanConfiguration");
            var choices = available.GetProperty("interfaces").EnumerateArray()
                .Select(x => new WanInterfaceChoice(
                    x.GetProperty("id").GetString() ?? "",
                    x.GetProperty("name").GetString() ?? "",
                    $"{x.GetProperty("name").GetString()}  •  IPv4 {x.GetProperty("ipv4").GetString()}  •  {x.GetProperty("gateway").GetString()}"))
                .ToList();
            AddUnavailableChoice(config.GetProperty("wan1"), choices);
            AddUnavailableChoice(config.GetProperty("wan2"), choices);
            Wan1InterfaceSelect.ItemsSource = choices.ToArray();
            Wan2InterfaceSelect.ItemsSource = choices.ToArray();
            SetWanSelection(Wan1InterfaceSelect, Wan1FriendlyName, config.GetProperty("wan1"), choices);
            SetWanSelection(Wan2InterfaceSelect, Wan2FriendlyName, config.GetProperty("wan2"), choices);
            WanConfigurationStatus.Text = config.GetProperty("active").GetBoolean()
                ? _loc.Text("settings.wan.active") : _loc.Text("settings.wan.unconfigured");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or InvalidDataException)
        {
            WanConfigurationStatus.Text = _loc.Text("common.offline");
        }
    }

    private void AddUnavailableChoice(JsonElement configured, List<WanInterfaceChoice> choices)
    {
        string id = configured.GetProperty("interfaceId").GetString() ?? "";
        string name = configured.GetProperty("interfaceName").GetString() ?? "";
        if (id.Length > 0 && choices.All(x => !x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            choices.Add(new WanInterfaceChoice(id, name, $"{name} — {_loc.Text("settings.wan.unavailable")}"));
    }

    private static void SetWanSelection(ComboBox selector, TextBox friendly, JsonElement configured,
        List<WanInterfaceChoice> choices)
    {
        string id = configured.GetProperty("interfaceId").GetString() ?? "";
        string name = configured.GetProperty("interfaceName").GetString() ?? "";
        if (id.Length == 0 && name.Length > 0)
            id = choices.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id ?? "";
        selector.SelectedValue = id.Length == 0 ? null : id;
        friendly.Text = configured.GetProperty("friendlyName").GetString() ?? "";
    }

    private async void SaveWanConfiguration_Click(object sender, RoutedEventArgs e)
    {
        string first = Wan1InterfaceSelect.SelectedValue as string ?? "";
        string second = Wan2InterfaceSelect.SelectedValue as string ?? "";
        if (first.Length == 0 || second.Length == 0)
        {
            WanConfigurationStatus.Text = _loc.Text("settings.wan.required");
            return;
        }
        if (first.Equals(second, StringComparison.OrdinalIgnoreCase))
        {
            WanConfigurationStatus.Text = _loc.Text("settings.wan.different");
            return;
        }
        _wanSettingsSaving = true;
        SaveWanConfigurationButton.IsEnabled = false;
        try
        {
            bool saved = await RunWriteAsync(new
            {
                apiVersion = 1,
                requestId = Guid.NewGuid().ToString("N"),
                command = "setWanConfiguration",
                wan1 = new { interfaceId = first, friendlyName = Wan1FriendlyName.Text.Trim() },
                wan2 = new { interfaceId = second, friendlyName = Wan2FriendlyName.Text.Trim() }
            }, wanConfiguration: true);
            await LoadWanSettingsAfterSaveAsync(saved);
        }
        finally
        {
            _wanSettingsSaving = false;
            SaveWanConfigurationButton.IsEnabled = true;
        }
    }

    private async Task LoadWanSettingsAfterSaveAsync(bool saved)
    {
        _wanSettingsSaving = false;
        await LoadWanSettingsAsync();
        WanConfigurationStatus.Text = _loc.Text(saved ? "settings.wan.saved" : "settings.wan.error");
        _wanSettingsSaving = true;
    }

    private async void SaveSettings_Click(object sender,RoutedEventArgs e)
    {
        string d=((ComboBoxItem)RetentionSelect.SelectedItem).Tag.ToString()!,b=((ComboBoxItem)MaximumSizeSelect.SelectedItem).Tag.ToString()!;
        if(d=="custom"&&(!int.TryParse(CustomRetentionDays.Text,out int custom)||custom<1)){MessageBox.Show(this,_loc.Text("settings.invalidDays"),"DualWAN",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        int? days=d=="unlimited"?null:d=="custom"?int.Parse(CustomRetentionDays.Text):int.Parse(d);
        await RunWriteAsync(new{apiVersion=1,requestId=Guid.NewGuid().ToString("N"),command="setTelemetryStoragePolicy",retentionDays=days,maximumBytes=b=="unlimited"?(long?)null:long.Parse(b)});
    }
    private async void CleanHistory_Click(object sender,RoutedEventArgs e)=>await RunWriteAsync(new{apiVersion=1,requestId=Guid.NewGuid().ToString("N"),command="cleanTelemetry"});
    private void LanguageSelect_SelectionChanged(object sender,SelectionChangedEventArgs e){if(LanguageSelect.SelectedValue is string code&&code!=_loc.CurrentCode){_loc.Select(code);ApplyLanguage();}}
    private void ThemeSelect_SelectionChanged(object sender,SelectionChangedEventArgs e){if(ThemeSelect.SelectedValue is string theme&&theme!=_loc.CurrentTheme){_loc.SelectTheme(theme);ApplyTheme();}}
    private void Navigate_Click(object sender,RoutedEventArgs e){if(MainTabs is null)return;if(sender is FrameworkElement element&&int.TryParse(element.Tag?.ToString(),out int index))MainTabs.SelectedIndex=index;}
    private void Window_SizeChanged(object sender,SizeChangedEventArgs e)
    {
        bool compact=e.NewSize.Width<1020;SidebarColumn.Width=new GridLength(compact?68:220);
        Visibility visibility=compact?Visibility.Collapsed:Visibility.Visible;
        BrandText.Visibility=VersionText.Visibility=NavDashboardText.Visibility=NavApplicationsText.Visibility=NavHistoryText.Visibility=NavGroupsText.Visibility=NavPresetsText.Visibility=NavSettingsText.Visibility=NavHelpText.Visibility=visibility;
    }
    private void UpdateNavigation()
    {
        RadioButton selected=MainTabs.SelectedIndex switch{2=>NavGroups,3=>NavPresets,4=>NavHistory,5=>NavSettings,6=>NavApplications,7=>NavHelp,_=>NavDashboard};selected.IsChecked=true;
        HeaderTitle.Text=MainTabs.SelectedIndex switch{2=>_loc.Text("tab.groups"),3=>_loc.Text("tab.presets"),4=>_loc.Text("tab.history"),5=>_loc.Text("tab.settings"),6=>_loc.Text("tab.applications"),7=>_loc.Text("tab.help"),_=>_loc.Text("tab.dashboard")};
    }
    private void ApplyTheme()
    {
        string requested=_loc.CurrentTheme;
        bool dark=requested=="Dark"||(requested=="System"&&IsWindowsDarkTheme());
        string selected = dark ? "Dark" : "Light";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        int index = -1;
        for (int i = 0; i < dictionaries.Count; i++)
        {
            string? source = dictionaries[i].Source?.OriginalString;
            if (source?.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                source?.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                index = i;
                break;
            }
        }
        if (index < 0) throw new InvalidOperationException("Theme resource dictionary is missing.");
        if (!dictionaries[index].Source!.OriginalString.EndsWith($"Themes/{selected}.xaml", StringComparison.OrdinalIgnoreCase))
            dictionaries[index] = new ResourceDictionary
            {
                Source = new Uri($"/DualWAN.Dashboard;component/Themes/{selected}.xaml", UriKind.Relative)
            };
        RedrawHistoryPanels();
    }
    private static bool IsWindowsDarkTheme()
    {
        try{return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is int value&&value==0;}catch{return false;}
    }
    private void ApplyLanguage()
    {
        DashboardTab.Header=_loc.Text("tab.dashboard");RulesTab.Header=_loc.Text("tab.rules");GroupsTab.Header=_loc.Text("tab.groups");PresetsTab.Header=_loc.Text("tab.presets");HistoryTab.Header=_loc.Text("tab.history");SettingsTab.Header=_loc.Text("tab.settings");ApplicationsTab.Header=_loc.Text("tab.applications");HelpTab.Header=_loc.Text("tab.help");
        HistoryTitle.Text=_loc.Text("history.title");HistoryRangeLabel.Text=_loc.Text("history.range");HistoryRefreshButton.Content=_loc.Text("history.refresh");HistoryWan1TrafficTitle.Text=HistoryWan2TrafficTitle.Text=_loc.Text("history.traffic");HistoryWan1QualityTitle.Text=HistoryWan2QualityTitle.Text=_loc.Text("history.quality");HistoryWan1Empty.Text=HistoryWan2Empty.Text=_loc.Text("history.emptyWan");RedrawHistoryPanels();
        SettingsTitle.Text=_loc.Text("settings.title");HistoricalSettingsTitle.Text=_loc.Text("settings.historical");RetentionLabel.Text=_loc.Text("settings.retention");CustomRetentionLabel.Text=_loc.Text("settings.customDays");MaximumSizeLabel.Text=_loc.Text("settings.maximumSize");LanguageLabel.Text=_loc.Text("settings.language");SaveSettingsButton.Content=_loc.Text("settings.save");CleanHistoryButton.Content=_loc.Text("settings.clean");
        WanConfigurationTitle.Text=_loc.Text("settings.wan.title");WanConfigurationDescription.Text=_loc.Text("settings.wan.description");Wan1InterfaceLabel.Text=Wan2InterfaceLabel.Text=_loc.Text("settings.wan.interface");Wan1FriendlyLabel.Text=Wan2FriendlyLabel.Text=_loc.Text("settings.wan.friendly");SaveWanConfigurationButton.Content=_loc.Text("settings.wan.save");
        NavDashboardText.Text=_loc.Text("tab.dashboard");NavApplicationsText.Text=_loc.Text("tab.applications");NavHistoryText.Text=_loc.Text("tab.history");NavGroupsText.Text=_loc.Text("tab.groups");NavPresetsText.Text=_loc.Text("tab.presets");NavSettingsText.Text=_loc.Text("tab.settings");NavHelpText.Text=_loc.Text("tab.help");RefreshHelp();
        ApplicationsTitle.Text=_loc.Text("tab.applications");ApplicationsSubtitle.Text=_loc.Text("apps.subtitle");AddApplicationButton.Content=_loc.Text("apps.add");ApplicationsStatus.Text=_loc.Text("apps.configured");ApplicationsEmpty.Text=_loc.Text("apps.empty");ApplicationsSearchHint.Text=_loc.Text("apps.search");ApplicationsSearchEmpty.Text=_loc.Text("apps.searchEmpty");Application.Current.Resources["apps.edit"]=_loc.Text("common.edit");Application.Current.Resources["apps.remove"]=_loc.Text("apps.remove");Application.Current.Resources["apps.openFileLocation"]=_loc.Text("apps.openFileLocation");RefreshApplications();
        DashboardTitle.Text=_loc.Text("dashboard.title");DashboardSubtitle.Text=_loc.Text("dashboard.subtitle");Wan1RxLabel.Text=Wan2RxLabel.Text=_loc.Text("dashboard.download");Wan1TxLabel.Text=Wan2TxLabel.Text=_loc.Text("dashboard.upload");Wan1PingLabel.Text=Wan2PingLabel.Text=_loc.Text("dashboard.ping");Wan1LossLabel.Text=Wan2LossLabel.Text=_loc.Text("dashboard.loss");Wan1UptimeLabel.Text=Wan2UptimeLabel.Text=_loc.Text("dashboard.uptime");Wan1TotalsLabel.Text=Wan2TotalsLabel.Text=_loc.Text("dashboard.totals");
        RulesTitle.Text=_loc.Text("rules.title");RulesSubtitle.Text=_loc.Text("rules.subtitle");RefreshRulesButton.Content=_loc.Text("common.refresh");AddRuleButton.Content=_loc.Text("rules.add");RulesHelp.Text=_loc.Text("rules.help");RuleApplicationColumn.Header=_loc.Text("rules.application");RuleModeColumn.Header=_loc.Text("common.mode");RuleStatusColumn.Header=_loc.Text("common.status");RuleActionsColumn.Header=_loc.Text("common.actions");
        GroupsTitle.Text=_loc.Text("groups.title");GroupsSubtitle.Text=_loc.Text("groups.subtitle");RefreshGroupsButton.Content=_loc.Text("common.refresh");AddGroupButton.Content=_loc.Text("groups.add");GroupsHelp.Text=_loc.Text("groups.help");GroupNameColumn.Header=_loc.Text("groups.name");GroupModeColumn.Header=_loc.Text("groups.policy");GroupStatusColumn.Header=_loc.Text("common.status");GroupApplicationsColumn.Header=_loc.Text("groups.applications");GroupActionsColumn.Header=_loc.Text("common.actions");
        PresetsTitle.Text=_loc.Text("presets.title");PresetsSubtitle.Text=_loc.Text("presets.subtitle");Preset1Description.Text=_loc.Text("presets.priority.desc");Preset2Description.Text=_loc.Text("presets.save.desc");Preset3Description.Text=_loc.Text("presets.only.desc");Preset4Description.Text=_loc.Text("presets.custom.desc");Preset1Apply.Content=Preset2Apply.Content=Preset3Apply.Content=Preset4Apply.Content=_loc.Text("common.apply");
        Preset1Title.Text=_loc.Text("presets.priority.name");Preset2Title.Text=_loc.Text("presets.save.name");Preset3Title.Text=_loc.Text("presets.only.name");Preset4Title.Text=_loc.Text("presets.custom.name");
        AppearanceTitle.Text=_loc.Text("settings.appearance");AppearanceDescription.Text=_loc.Text("settings.appearance.desc");ThemeLabel.Text=_loc.Text("settings.theme");LanguageSectionTitle.Text=_loc.Text("settings.language");LanguageDescription.Text=_loc.Text("settings.language.desc");AboutTitle.Text=_loc.Text("settings.about");AboutText.Text=_loc.Text("settings.about.text");StartupCheck.Content=_loc.Text("settings.startup");_trayOpen.Text=_loc.Text("tray.open");_trayExit.Text=_loc.Text("tray.exit");
        AboutVersion.Text=$"DualWAN {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} — by GDM — GPLv3";
        ThemeSystem.Content=_loc.Text("theme.system");ThemeLight.Content=_loc.Text("theme.light");ThemeDark.Content=_loc.Text("theme.dark");
        foreach(ComboBoxItem item in RetentionSelect.Items){string key=item.Tag?.ToString() switch{"custom"=>"settings.custom","unlimited"=>"settings.keepIndefinitely",var days=>"settings.days."+days};item.Content=_loc.Text(key);}
        foreach(ComboBoxItem item in MaximumSizeSelect.Items)if(item.Tag?.ToString()=="unlimited")item.Content=_loc.Text("settings.unlimited");
        Application.Current.Resources["tooltip.edit"]=_loc.Text("common.edit");Application.Current.Resources["tooltip.delete"]=_loc.Text("common.delete");Application.Current.Resources["tooltip.toggle"]=_loc.Text("rules.toggleTip");
        for(int i=0;i<_rules.Count;i++)_rules[i]=_rules[i] with{EnabledLabel=_loc.Text(_rules[i].Enabled?"common.enabled":"common.disabled")};
        for(int i=0;i<_groups.Count;i++)_groups[i]=_groups[i] with{EnabledLabel=_loc.Text(_groups[i].Enabled?"common.enabled":"groups.disabled"),ApplicationsLabel=string.Format(_loc.Text("groups.appCount"),_groups[i].Applications.Count),PolicyDisplay=GroupPolicyLabel(_groups[i].Wan,_groups[i].Mode,_groups[i].Enabled)};
        RulesStatus.Text=_rulesRefreshing||(!_rulesEverLoaded&&RulesGrid.IsEnabled)?_loc.Text("rules.loading"):RulesGrid.IsEnabled?string.Format(_loc.Text("rules.loaded"),_rules.Count):_loc.Text("rules.offline");
        string preset=_currentPreset is null?_loc.Text("common.unavailable"):PresetName(_currentPreset);
        CurrentPresetText.Text=$"{_loc.Text("presets.current")}: {preset}";
        GroupsStatus.Text=_groupsRefreshing||(!_groupsEverLoaded&&GroupsGrid.IsEnabled)?_loc.Text("groups.loading"):GroupsGrid.IsEnabled?string.Format(_loc.Text("groups.loaded"),_groups.Count,preset):_loc.Text("groups.offline");
        if(HistoryStatus.Text.Length>0)HistoryStatus.Text=_historyPointCount.HasValue?string.Format(_loc.Text("history.points"),_historyPointCount):_loc.Text("history.empty");
        Wan1State.Text=_online?$"●  {WanStateText(_wanStates[0])}":_loc.Text("common.unavailable");Wan2State.Text=_online?$"●  {WanStateText(_wanStates[1])}":_loc.Text("common.unavailable");
        if(_online&&_lastTelemetry.HasValue)Render(_lastTelemetry.Value);
        else{FlowSummary.Text=_loc.Text("dashboard.waiting");ServiceUptime.Text=_loc.Text("dashboard.serviceUptime")+" —";}
        UpdateServiceState(WindowsServiceControl.Query());
        ApplyContextTooltips();
        UpdateNavigation();
    }

    private async void ServiceControl_Click(object sender,RoutedEventArgs e)
    {
        if(_serviceOperation)return;
        DualWanServiceState current=WindowsServiceControl.Query();
        bool start=current!=DualWanServiceState.Running;
        if(!start&&ShowStopConfirmation()!=true)return;
        _serviceOperation=true;ServiceActionMessage.Text="";ServiceControlButton.IsEnabled=false;
        ServiceControlButton.Content=_loc.Text(start?"service.starting":"service.stopping");
        ServiceState.Text=_loc.Text(start?"service.starting":"service.stopping");
        ServiceDot.Fill=Application.Current.Resources["StatusRecoveringBrush"] as Brush??Brushes.SteelBlue;
        try
        {
            var info=new ProcessStartInfo{FileName=Environment.ProcessPath??throw new InvalidOperationException(),UseShellExecute=true,Verb="runas"};
            info.ArgumentList.Add(start?"--service-start":"--service-stop");
            using var process=Process.Start(info)??throw new InvalidOperationException();
            await process.WaitForExitAsync();
            if(process.ExitCode==0)ServiceActionMessage.Text=_loc.Text(start?"service.started":"service.stopped");
            else ServiceActionMessage.Text=_loc.Text(start?"service.startFailed":"service.stopFailed");
        }
        catch(Win32Exception ex) when(ex.NativeErrorCode==1223){ServiceActionMessage.Text=_loc.Text("service.cancelled");}
        catch(Exception ex){Debug.WriteLine(ex);ServiceActionMessage.Text=_loc.Text(start?"service.startFailed":"service.stopFailed");}
        finally
        {
            _serviceOperation=false;
            UpdateServiceState(WindowsServiceControl.Query());
            await RefreshAsync();
        }
    }

    private bool? ShowStopConfirmation()
    {
        var dialog=new Window{Title=_loc.Text("service.confirmTitle"),Owner=this,Width=460,Height=230,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=(Brush)Application.Current.Resources["SurfaceBrush"],Foreground=(Brush)Application.Current.Resources["TextPrimaryBrush"],ShowInTaskbar=false};
        var panel=new StackPanel{Margin=new Thickness(24)};
        panel.Children.Add(new TextBlock{Text=_loc.Text("service.confirmTitle"),FontSize=20,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{Text=_loc.Text("service.confirmBody"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,22),Foreground=(Brush)Application.Current.Resources["TextSecondaryBrush"]});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Name="CancelStopButton",Content=_loc.Text("common.cancel"),IsCancel=true,IsDefault=true,MinWidth=90};
        var stop=new Button{Name="ConfirmStopButton",Content=_loc.Text("service.stop"),MinWidth=110,Margin=new Thickness(10,0,0,0)};stop.SetResourceReference(StyleProperty,"DangerButton");stop.Click+=(_,_)=>{dialog.DialogResult=true;};
        actions.Children.Add(cancel);actions.Children.Add(stop);panel.Children.Add(actions);dialog.Content=panel;
        return dialog.ShowDialog();
    }

    private void UpdateServiceState(DualWanServiceState state)
    {
        string stateKey=state switch{DualWanServiceState.Running=>"common.running",DualWanServiceState.Stopped=>"service.stopped",DualWanServiceState.StartPending=>"service.starting",DualWanServiceState.StopPending=>"service.stopping",_=>"common.offline"};
        ServiceState.Text=_loc.Text(stateKey);
        string brushKey=state switch{DualWanServiceState.Running=>"StatusUpBrush",DualWanServiceState.StartPending or DualWanServiceState.StopPending=>"StatusRecoveringBrush",_=>"StatusDownBrush"};
        ServiceDot.Fill=Application.Current.Resources[brushKey] as Brush??Brushes.Gray;
        ServiceControlButton.Content=_loc.Text(state==DualWanServiceState.Running?"service.stop":"service.start");
        ServiceControlButton.IsEnabled=!_serviceOperation&&state is not (DualWanServiceState.StartPending or DualWanServiceState.StopPending);
        _trayService.Text = _loc.Text(state==DualWanServiceState.Running?"service.stop":"service.start");
        _trayService.Enabled = ServiceControlButton.IsEnabled;
        _tray.Text = _loc.Text(state==DualWanServiceState.Running?"tray.running":"tray.stopped");
    }

    private void SetOnline(bool online)
    {
        _online = online;
        UpdateServiceState(online?DualWanServiceState.Running:WindowsServiceControl.Query());
        if (online) return;
        FlowSummary.Text = _loc.Text("dashboard.waiting");
        ServiceUptime.Text = _loc.Text("dashboard.serviceUptime")+" —";
        Wan1State.Text = Wan2State.Text = _loc.Text("common.unavailable");
        Wan1StateBadge.Background = Wan2StateBadge.Background = (Brush)Application.Current.Resources["StatusUnknownBrush"];
        Wan1RxRate.Text = Wan1TxRate.Text = Wan2RxRate.Text = Wan2TxRate.Text = "—";
        Wan1Latency.Text = Wan1LatencyRange.Text = Wan2Latency.Text = Wan2LatencyRange.Text = "—";
        Wan1Loss.Text = Wan1Uptime.Text = Wan2Loss.Text = Wan2Uptime.Text = "—";
        Wan1RxTotal.Text = Wan1TxTotal.Text = Wan2RxTotal.Text = Wan2TxTotal.Text = "—";
    }

    private string WanStateText(string state) => state is "UP" or "DOWN" or "SUSPECT" or "RECOVERING"
        ? _loc.Text("wan.state." + state) : _loc.Text("common.unknown");

    private static Brush StateBrush(string state) => Brush(state switch
    {
        "UP" => "#166534",
        "SUSPECT" => "#A16207",
        "DOWN" => "#991B1B",
        "RECOVERING" => "#1D4ED8",
        _ => "#334155"
    });

    private static SolidColorBrush Brush(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));

    private static double? GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : null;

    private static long GetInt64(JsonElement element, string name)
        => GetNullableInt64(element, name) ?? 0;

    private static long? GetNullableInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64() : null;

    private static string FormatMilliseconds(double? value, bool suffix = true)
        => value.HasValue ? $"{value.Value:F0}{(suffix ? " ms" : "")}" : "—";

    private static string FormatBytes(double? value, bool perSecond)
    {
        if (!value.HasValue) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double scaled = Math.Max(0, value.Value);
        int unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1) { scaled /= 1024; unit++; }
        string number = unit == 0 ? scaled.ToString("F0") : scaled.ToString("F1");
        return $"{number} {units[unit]}{(perSecond ? "/s" : "")}";
    }

    private static string FormatDuration(long seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalDays >= 1
            ? $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m"
            : value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m {value.Seconds}s"
            : $"{value.Minutes}m {value.Seconds}s";
    }

    public sealed record WanChoice(string Id, string Display);
    private sealed record WanInterfaceChoice(string Id, string Name, string Display);
    public sealed record RuleRow(string Process, string Wan, string WanDisplay, string Mode, bool Enabled, string EnabledLabel)
    {
        public string EnabledText => EnabledLabel;
    }
    public sealed record GroupRow(string Name, string Wan, string WanDisplay, string Mode, bool Enabled,
        IReadOnlyList<string> Applications, string ApplicationsLabel, string EnabledLabel)
    {
        public string EnabledText => EnabledLabel;
        public string ApplicationsDisplay => ApplicationsLabel;
        public string PolicyDisplay { get; init; } = "";
    }

    private string GroupPolicyLabel(string wan, string mode, bool enabled)
    {
        if (!ApplicationPolicyView.HasActiveGroupPolicy(enabled, wan, mode)) return _loc.Text("groups.disabled");
        return string.Format(_loc.Text(mode.Equals("STRICT", StringComparison.OrdinalIgnoreCase) ? "apps.only" : "apps.prefer"),
            FriendlyWan(wan));
    }

    private sealed class GroupEditorWindow : Window
    {
        private readonly MainWindow _parent;
        private readonly LocalizationService _loc;
        private readonly TextBox _name = new();
        private readonly ComboBox _wan = new();
        private readonly ComboBox _mode = new();
        private readonly CheckBox _enabled = new();
        private readonly TextBox _applications = new() { AcceptsReturn = true, Height = 95, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBlock _memberCount = new();
        private readonly StackPanel _memberPreview = new();
        private readonly HashSet<string> _otherApplications;
        public GroupRow? Result { get; private set; }

        private sealed record ModeChoice(string Id, string Label);

        public GroupEditorWindow(MainWindow parent, IReadOnlyList<WanChoice> wans, IEnumerable<GroupRow> groups, GroupRow? existing)
        {
            _parent = parent;
            _loc = parent._loc;
            Title = existing is null ? _loc.Text("groups.add") : string.Format(_loc.Text("groups.editTitle"), existing.Name);
            Width = 570; Height = 700; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            _otherApplications = groups.Where(x => existing is null || !x.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase))
                .SelectMany(x => x.Applications).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _name.Text = existing?.Name ?? ""; _name.IsEnabled = existing is null;
            _wan.ItemsSource = wans; _wan.DisplayMemberPath = nameof(WanChoice.Display); _wan.SelectedValuePath = nameof(WanChoice.Id);
            _wan.SelectedValue = existing?.Wan ?? wans.FirstOrDefault()?.Id;
            _mode.ItemsSource = new[] { new ModeChoice("STRICT", _loc.Text("groups.onlyMode")),
                new ModeChoice("FAILOVER", _loc.Text("groups.preferMode")) };
            _mode.DisplayMemberPath = nameof(ModeChoice.Label); _mode.SelectedValuePath = nameof(ModeChoice.Id);
            _mode.SelectedValue = existing?.Mode ?? "FAILOVER";
            _enabled.IsChecked = existing?.Enabled ?? true;
            _enabled.Content = _loc.Text("common.enabled");
            _applications.Text = existing is null ? "" : string.Join(Environment.NewLine, existing.Applications);
            SetContextTooltip(_enabled, _loc.Text("context.groupEnabled"));
            SetContextTooltip(_wan, _loc.Text("context.groupPolicy"));
            SetContextTooltip(_mode, _loc.Text("context.groupPolicy"));
            SetContextTooltip(_applications, _loc.Text("context.groupMembership"));

            var form = new StackPanel { Margin = new Thickness(24) };
            form.Children.Add(FieldLabel(_loc.Text("groups.name"))); form.Children.Add(_name);
            form.Children.Add(FieldLabel(_loc.Text("history.wan"))); form.Children.Add(_wan);
            form.Children.Add(FieldLabel(_loc.Text("groups.policy"))); form.Children.Add(_mode);
            var policyNote = new TextBlock { Text = _loc.Text("groups.policyNote"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0) };
            policyNote.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            form.Children.Add(policyNote);
            form.Children.Add(FieldLabel(_loc.Text("groups.applicationsHint"))); form.Children.Add(_applications);
            _memberCount.Margin = new Thickness(0, 12, 0, 6);
            _memberCount.FontWeight = FontWeights.SemiBold;
            form.Children.Add(_memberCount);
            var members = new ScrollViewer { Content = _memberPreview, MaxHeight = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            form.Children.Add(members);
            form.Children.Add(_enabled);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            var cancel = new Button { Content = _loc.Text("common.cancel"), Padding = new Thickness(16, 7, 16, 7), IsCancel = true };
            var save = new Button { Content = _loc.Text("common.save"), Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(10, 0, 0, 0), IsDefault = true };
            save.SetResourceReference(StyleProperty, "PrimaryButton");
            save.Click += Save_Click; buttons.Children.Add(cancel); buttons.Children.Add(save); form.Children.Add(buttons);
            Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _applications.TextChanged += (_, _) => RefreshMemberPreview();
            _wan.SelectionChanged += (_, _) => RefreshMemberPreview();
            _mode.SelectionChanged += (_, _) => RefreshMemberPreview();
            _enabled.Checked += (_, _) => RefreshMemberPreview();
            _enabled.Unchecked += (_, _) => RefreshMemberPreview();
            RefreshMemberPreview();
        }

        private void RefreshMemberPreview()
        {
            string[] processes = _applications.Text.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string wan = _wan.SelectedValue as string ?? "";
            string mode = _mode.SelectedValue as string ?? "FAILOVER";
            var members = GroupMemberPresentation.Build(processes.Select(process =>
            {
                ApplicationCatalogueEntry? known = _loc.FindApplication(process);
                RuleRow? rule = _parent._rules.FirstOrDefault(x => x.Process.Equals(process, StringComparison.OrdinalIgnoreCase));
                return new GroupMemberInput(process, known?.DisplayName,
                    rule is null ? null : new PolicyInput(rule.Wan, rule.Mode, rule.Enabled));
            }), new PolicyInput(wan, mode, _enabled.IsChecked == true));
            _memberCount.Text = string.Format(_loc.Text("groups.appCount"), members.Count);
            _memberPreview.Children.Clear();
            foreach (GroupMemberState member in members)
            {
                string state = member.Effective.Source switch
                {
                    EffectivePolicySource.Individual => _loc.Text("packages.individualRule"),
                    EffectivePolicySource.Group => _loc.Text("groups.inherits"),
                    _ => _loc.Text("groups.disabled")
                };
                string policy = member.Effective.Source == EffectivePolicySource.Windows ? "" :
                    _parent.GroupPolicyLabel(member.Effective.Wan!, member.Effective.Mode!, true);
                var item = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                item.Children.Add(new TextBlock { Text = member.DisplayName, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis });
                var process = new TextBlock { Text = member.Process };
                process.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                item.Children.Add(process);
                var effective = new TextBlock { Text = policy.Length == 0 ? state : $"{state} · {policy}",
                    TextWrapping = TextWrapping.Wrap };
                effective.SetResourceReference(TextBlock.ForegroundProperty,
                    member.Effective.Source == EffectivePolicySource.Windows ? "TextSecondaryBrush" : "AccentBrush");
                item.Children.Add(effective);
                _memberPreview.Children.Add(item);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = _name.Text.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.IndexOfAny(['\\', '/', '\0']) >= 0)
            { ShowError(_loc.Text("groups.invalidName")); return; }
            string[] apps = _applications.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string app in apps)
            {
                if (!IsValidProcess(app)) { ShowError(string.Format(_loc.Text("groups.invalidExecutable"), app)); return; }
                if (!unique.Add(app)) { ShowError(string.Format(_loc.Text("groups.duplicateApplication"), app)); return; }
                if (_otherApplications.Contains(app)) { ShowError(string.Format(_loc.Text("groups.applicationInOtherGroup"), app)); return; }
            }
            if (_wan.SelectedItem is not WanChoice wan || _mode.SelectedValue is not string mode) return;
            bool enabled = _enabled.IsChecked == true;
            Result = new GroupRow(name, wan.Id, wan.Display, mode, enabled, apps,
                string.Format(_loc.Text("groups.appCount"), apps.Length), _loc.Text(enabled ? "common.enabled" : "groups.disabled"))
                { PolicyDisplay = _parent.GroupPolicyLabel(wan.Id, mode, enabled) };
            DialogResult = true;
        }

        private static bool IsValidProcess(string process) => !string.IsNullOrWhiteSpace(process) && process.Length <= 260 &&
            process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(process).Equals(process, StringComparison.Ordinal) &&
            process.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        private void ShowError(string message) => MessageBox.Show(this, message, "DualWAN", MessageBoxButton.OK, MessageBoxImage.Warning);
        private static TextBlock FieldLabel(string text)
        {
            var label = new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 5) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return label;
        }
    }

    private sealed class RuleEditorWindow : Window
    {
        private readonly LocalizationService _loc;
        private readonly TextBox _process = new();
        private readonly ComboBox _wan = new();
        private readonly ComboBox _mode = new();
        private readonly CheckBox _enabled = new();
        public RuleRow? Result { get; private set; }

        public RuleEditorWindow(LocalizationService loc, IReadOnlyList<WanChoice> wans, RuleRow? existing)
        {
            _loc = loc;
            Title = existing is null ? _loc.Text("rules.add") : string.Format(_loc.Text("rules.editTitle"), existing.Process);
            Width = 500;
            MinHeight = 390;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Brush("#0B1220");
            Foreground = Brush("#E8EEF7");

            _process.Text = existing?.Process ?? "";
            _process.IsEnabled = existing is null;
            _wan.ItemsSource = wans;
            _wan.DisplayMemberPath = nameof(WanChoice.Display);
            _wan.SelectedValuePath = nameof(WanChoice.Id);
            _wan.SelectedValue = existing?.Wan ?? wans.FirstOrDefault()?.Id;
            _mode.ItemsSource = new[] { "STRICT", "FAILOVER" };
            _mode.SelectedItem = existing?.Mode ?? "STRICT";
            _enabled.IsChecked = existing?.Enabled ?? true;
            _enabled.Content = _loc.Text("common.enabled");

            var form = new StackPanel { Margin = new Thickness(24) };
            form.Children.Add(FieldLabel(_loc.Text("rules.executable")));
            form.Children.Add(_process);
            form.Children.Add(FieldLabel(_loc.Text("history.wan")));
            form.Children.Add(_wan);
            form.Children.Add(FieldLabel(_loc.Text("common.mode")));
            form.Children.Add(_mode);
            form.Children.Add(new TextBlock
            {
                Text = _loc.Text("rules.help"),
                Foreground = Brush("#91A3BA"), Margin = new Thickness(0, 6, 0, 14), TextWrapping = TextWrapping.Wrap
            });
            form.Children.Add(_enabled);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            var cancel = new Button { Content = _loc.Text("common.cancel"), Padding = new Thickness(16, 7, 16, 7), IsCancel = true };
            var save = new Button { Content = _loc.Text("common.save"), Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(10, 0, 0, 0), IsDefault = true };
            save.Click += Save_Click;
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            form.Children.Add(buttons);
            Content = form;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string process = _process.Text.Trim();
            if (string.IsNullOrWhiteSpace(process) || process.Length > 260 ||
                !process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(process).Equals(process, StringComparison.Ordinal) ||
                process.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, _loc.Text("rules.invalidExecutable"), "DualWAN",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_wan.SelectedItem is not WanChoice wan || _mode.SelectedItem is not string mode) return;
            bool enabled = _enabled.IsChecked == true;
            Result = new RuleRow(process, wan.Id, wan.Display, mode, enabled,
                _loc.Text(enabled ? "common.enabled" : "common.disabled"));
            DialogResult = true;
        }

        private static TextBlock FieldLabel(string text) => new()
        {
            Text = text, Foreground = Brush("#91A3BA"), Margin = new Thickness(0, 12, 0, 5)
        };
    }
}


