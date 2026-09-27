using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Path = System.IO.Path;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Application = System.Windows.Application;
using Point = System.Windows.Point;

namespace DualWAN.Dashboard;

// Presentation only: the Service owns query ranges, byte accounting and WAN attribution.
public partial class MainWindow
{
    private sealed record StatisticsRow(string AppKey, string Name, string TrafficText,
        string TotalText, ImageSource? Icon);

    private readonly ObservableCollection<StatisticsRow> _statsRows = [];
    private JsonElement[] _statsSeries = [];
    private int _statsRetentionMinutes = 60;
    private string _statsPeriod = "1h";
    private bool _statsLoading, _statsSettingRetention, _statsPendingReload;
    private int _statsGeneration;
    private readonly ImageSource _statsGenericIcon = BitmapFrame.Create(
        new Uri("pack://application:,,,/Assets/DualWAN.ico"));

    private static long StatsNumber(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt64() : 0;

    private string StatsBytes(long bytes) => FormatBytes(bytes, false);
    private string StatsTime(long timestamp, bool full = false) =>
        DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString(
            full ? "g" : _statsPeriod is "1h" or "24h" ? "HH:mm" : "d MMM", CultureInfo.GetCultureInfo(_loc.CurrentCode));

    private async Task LoadStatisticsAsync()
    {
        if (_statsLoading) return;
        _statsLoading = true;
        int generation = ++_statsGeneration;
        StatsStatus.Text = _loc.Text("stats.loading");
        StatsRefresh.IsEnabled = false;
        try
        {
            JsonElement retention = await SendReadAsync("getAppStatsRetention");
            if (generation != _statsGeneration) return;
            _statsRetentionMinutes = retention.GetProperty("retentionMinutes").GetInt32();
            UpdateStatsRetention();
            if (PeriodMinutes(_statsPeriod) > _statsRetentionMinutes) _statsPeriod = "1h";
            UpdateStatsPeriods();
            JsonElement summary = await SendRequestAsync(new { apiVersion = 1, requestId = Guid.NewGuid().ToString("N"),
                command = "getAppStatisticsSummary", period = _statsPeriod, limit = 20 });
            if (generation != _statsGeneration) return;
            string? previous = (StatsTopList.SelectedItem as StatisticsRow)?.AppKey;
            _statsRows.Clear();
            foreach (JsonElement app in summary.GetProperty("applications").EnumerateArray())
            {
                string key = app.GetProperty("appKey").GetString() ?? "";
                string basename = Path.GetFileName(key);
                ApplicationCatalogueEntry? entry = _loc.Applications.FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(x.ExecutablePath) &&
                    string.Equals(x.ExecutablePath, key, StringComparison.OrdinalIgnoreCase));
                string name = entry?.DisplayName ?? (string.IsNullOrWhiteSpace(basename) ? key : basename);
                ImageSource icon = _statsGenericIcon;
                if (entry is not null)
                {
                    string visualKey = VisualKey(entry.ProcessName, entry.ExecutablePath);
                    if (_applicationVisualCache.TryGetValue(visualKey, out var cached) && cached.Icon is not null)
                        icon = cached.Icon;
                    else if (entry.ExecutablePath is string path)
                    {
                        var resolved = await Task.Run(() => ApplicationIconResolver.Resolve(entry.ProcessName, path));
                        if (generation != _statsGeneration) return;
                        if (resolved.Icon is not null) icon = resolved.Icon;
                    }
                }
                _statsRows.Add(new(key, name,
                    $"{_loc.Text("stats.download")} {StatsBytes(StatsNumber(app, "downloadBytes"))}  ·  {_loc.Text("stats.upload")} {StatsBytes(StatsNumber(app, "uploadBytes"))}",
                    StatsBytes(StatsNumber(app, "totalBytes")), icon));
            }
            StatsTopList.ItemsSource = _statsRows;
            StatsEmpty.Visibility = _statsRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatsStatus.Text = _statsRows.Count == 0 ? "" : string.Format(_loc.Text("stats.appsCount"), _statsRows.Count);
            StatsTopList.SelectedItem = _statsRows.FirstOrDefault(x => x.AppKey == previous) ?? _statsRows.FirstOrDefault();
            if (_statsRows.Count == 0) ClearStatisticsDetail();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or InvalidDataException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            _statsRows.Clear();
            StatsTopList.ItemsSource = _statsRows;
            ClearStatisticsDetail();
            StatsEmpty.Visibility = Visibility.Collapsed;
            StatsStatus.Text = _loc.Text("stats.unavailable");
        }
        finally
        {
            _statsLoading = false;
            StatsRefresh.IsEnabled = true;
            if (_statsPendingReload)
            {
                _statsPendingReload = false;
                await LoadStatisticsAsync();
            }
        }
    }

    private async Task LoadStatisticsDetailAsync(StatisticsRow row)
    {
        int generation = ++_statsGeneration;
        ClearStatisticsDetail();
        StatsDetailTitle.Text = row.Name;
        StatsDetailPath.Text = row.AppKey;
        try
        {
            JsonElement detail = await SendRequestAsync(new { apiVersion = 1, requestId = Guid.NewGuid().ToString("N"),
                command = "getAppStatisticsDetail", appKey = row.AppKey, period = _statsPeriod });
            if (generation != _statsGeneration || (StatsTopList.SelectedItem as StatisticsRow)?.AppKey != row.AppKey) return;
            StatsTotals.Text = $"{_loc.Text("stats.total")}: {StatsBytes(StatsNumber(detail, "totalBytes"))}   ·   " +
                $"{_loc.Text("stats.download")}: {StatsBytes(StatsNumber(detail, "downloadBytes"))}   ·   " +
                $"{_loc.Text("stats.upload")}: {StatsBytes(StatsNumber(detail, "uploadBytes"))}";
            StatsFlows.Text = $"{_loc.Text("stats.tcp")}: {StatsNumber(detail, "tcpFlows")}   ·   " +
                $"{_loc.Text("stats.udp")}: {StatsNumber(detail, "udpSessions")}";
            var wanRows = new List<string>();
            foreach (JsonElement wan in detail.GetProperty("wanBreakdown").EnumerateArray())
            {
                string id = wan.GetProperty("wan").GetString() ?? "";
                wanRows.Add($"{StatisticsWanName(id)}  ·  {_loc.Text("stats.download")} {StatsBytes(StatsNumber(wan, "downloadBytes"))}  ·  " +
                    $"{_loc.Text("stats.upload")} {StatsBytes(StatsNumber(wan, "uploadBytes"))}");
            }
            StatsWanList.ItemsSource = wanRows;
            _statsSeries = detail.GetProperty("series").EnumerateArray().Select(x => x.Clone()).ToArray();
            StatsChartEmpty.Text = _statsSeries.Length == 0 ? _loc.Text("stats.noChart") : "";
            RedrawStatisticsChart();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or InvalidDataException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            StatsStatus.Text = _loc.Text("stats.unavailable");
            ClearStatisticsDetail();
        }
    }

    private string StatisticsWanName(string id)
    {
        if (_lastTelemetry.HasValue && _lastTelemetry.Value.TryGetProperty("wans", out var wans))
            foreach (JsonElement wan in wans.EnumerateArray())
                if (string.Equals(wan.GetProperty("id").GetString(), id, StringComparison.OrdinalIgnoreCase))
                {
                    string? name = wan.GetProperty("name").GetString();
                    return string.IsNullOrWhiteSpace(name) || name == id ? id : $"{id} — {name}";
                }
        return id;
    }

    private void ClearStatisticsDetail()
    {
        StatsDetailTitle.Text = (StatsTopList.SelectedItem as StatisticsRow)?.Name ?? "";
        StatsDetailPath.Text = "";
        StatsTotals.Text = StatsFlows.Text = "";
        StatsWanList.ItemsSource = null;
        _statsSeries = [];
        StatsChartEmpty.Text = "";
        StatsChart.Children.Clear();
    }

    private static int PeriodMinutes(string period) => period switch
    {
        "1h" => 60, "24h" => 1440, "7d" => 10080, "30d" => 43200, _ => 60
    };

    private void UpdateStatsPeriods()
    {
        foreach (Button button in new[] { Stats1h, Stats24h, Stats7d, Stats30d })
        {
            button.IsEnabled = PeriodMinutes((string)button.Tag) <= _statsRetentionMinutes;
            button.FontWeight = (string)button.Tag == _statsPeriod ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    private void UpdateStatsRetention()
    {
        StatsRetention.SelectionChanged -= StatsRetention_SelectionChanged;
        StatsRetention.SelectedItem = StatsRetention.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(x => int.Parse((string)x.Tag) == _statsRetentionMinutes);
        StatsRetention.SelectionChanged += StatsRetention_SelectionChanged;
        StatsRetentionWarning.Text = _statsRetentionMinutes >= 10080 ? _loc.Text("stats.retentionWarning") : "";
    }

    private async void StatsPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string period || period == _statsPeriod) return;
        _statsPeriod = period;
        UpdateStatsPeriods();
        if (_statsLoading) { ++_statsGeneration; _statsPendingReload = true; return; }
        await LoadStatisticsAsync();
    }

    private async void StatsRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_statsLoading) { _statsPendingReload = true; return; }
        await LoadStatisticsAsync();
    }

    private async void StatsTopList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatsTopList.SelectedItem is StatisticsRow row) await LoadStatisticsDetailAsync(row);
        else ClearStatisticsDetail();
    }

    private async void StatsRetention_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _statsSettingRetention || StatsRetention.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int minutes) || minutes == _statsRetentionMinutes) return;
        _statsSettingRetention = true;
        StatsRetention.IsEnabled = false;
        try
        {
            bool saved = await RunWriteAsync(new { apiVersion = 1, requestId = Guid.NewGuid().ToString("N"),
                command = "setAppStatsRetention", retentionMinutes = minutes }, appStatsRetention: true);
            if (saved) await LoadStatisticsAsync();
            else UpdateStatsRetention();
        }
        finally { _statsSettingRetention = false; StatsRetention.IsEnabled = true; }
    }

    private void ApplyStatisticsLanguage()
    {
        StatisticsTitle.Text = StatsTopTitle.Text = _loc.Text("tab.statistics");
        StatsCoverage.Text = _loc.Text("stats.coverage");
        StatsRetentionLabel.Text = _loc.Text("stats.retention");
        StatsRefresh.Content = _loc.Text("stats.refresh");
        StatsEmpty.Text = _loc.Text("stats.empty");
        StatsWanTitle.Text = _loc.Text("stats.wan");
        StatsChartTitle.Text = _loc.Text("stats.chart");
        StatsDownloadLegend.Text = _loc.Text("stats.download");
        StatsUploadLegend.Text = _loc.Text("stats.upload");
        string[] keys = ["stats.1h", "stats.24h", "stats.7d", "stats.30d"];
        for (int i = 0; i < 4; i++) ((ComboBoxItem)StatsRetention.Items[i]).Content = _loc.Text(keys[i]);
        StatsRetentionWarning.Text = _statsRetentionMinutes >= 10080 ? _loc.Text("stats.retentionWarning") : "";
        if (_statsRows.Count == 0 && StatsEmpty.Visibility == Visibility.Visible) StatsEmpty.Text = _loc.Text("stats.empty");
        RedrawStatisticsChart();
        if (MainTabs?.SelectedIndex == 8)
        {
            if (_statsLoading) _statsPendingReload = true;
            else _ = LoadStatisticsAsync();
        }
    }

    private void StatsChart_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawStatisticsChart();

    private void RedrawStatisticsChart()
    {
        if (StatsChart is null) return;
        Canvas chart = StatsChart;
        chart.Children.Clear();
        if (_statsSeries.Length == 0 || chart.ActualWidth < 180) return;
        double width = chart.ActualWidth, height = chart.ActualHeight;
        const double left = 60, right = 15, top = 16, bottom = 35;
        double plotWidth = width - left - right, plotHeight = height - top - bottom;
        double maximum = Math.Max(1, _statsSeries.Max(x => Math.Max(StatsNumber(x, "downloadBytes"), StatsNumber(x, "uploadBytes"))));
        Brush border = (Brush)Application.Current.Resources["BorderBrush"];
        Brush caption = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        for (int i = 0; i <= 3; i++)
        {
            double y = top + plotHeight * i / 3;
            chart.Children.Add(new Line { X1 = left, X2 = width - right, Y1 = y, Y2 = y,
                Stroke = border, StrokeThickness = 1 });
            var label = new TextBlock { Text = StatsBytes((long)(maximum * (3 - i) / 3)),
                Foreground = caption, FontSize = 10 };
            Canvas.SetLeft(label, 2); Canvas.SetTop(label, y - 8); chart.Children.Add(label);
        }
        long start = _statsSeries[0].GetProperty("timestamp").GetInt64();
        long end = _statsSeries[^1].GetProperty("timestamp").GetInt64();
        for (int i = 0; i <= 3; i++)
        {
            long time = start + (end - start) * i / 3;
            var label = new TextBlock { Text = StatsTime(time), Foreground = caption, FontSize = 10 };
            Canvas.SetLeft(label, left + plotWidth * i / 3 - 18);
            Canvas.SetTop(label, height - bottom + 8); chart.Children.Add(label);
        }
        for (int series = 0; series < 2; series++)
        {
            string field = series == 0 ? "downloadBytes" : "uploadBytes";
            Brush brush = (Brush)Application.Current.Resources[series == 0 ? "HistoryDownload" : "HistoryUpload"];
            var line = new Polyline { Stroke = brush, StrokeThickness = 2, IsHitTestVisible = false };
            for (int i = 0; i < _statsSeries.Length; i++)
            {
                long time = _statsSeries[i].GetProperty("timestamp").GetInt64();
                double x = left + (end == start ? 0.5 : (time - start) / (double)(end - start)) * plotWidth;
                double y = top + plotHeight * (1 - StatsNumber(_statsSeries[i], field) / maximum);
                line.Points.Add(new Point(x, y));
            }
            chart.Children.Add(line);
        }
    }

    private void StatsChart_MouseMove(object sender, MouseEventArgs e)
    {
        if (_statsSeries.Length == 0 || StatsChart.ActualWidth <= 75) return;
        double fraction = Math.Clamp((e.GetPosition(StatsChart).X - 60) / (StatsChart.ActualWidth - 75), 0, 1);
        long first = _statsSeries[0].GetProperty("timestamp").GetInt64();
        long last = _statsSeries[^1].GetProperty("timestamp").GetInt64();
        long target = first + (long)((last - first) * fraction);
        JsonElement point = _statsSeries.MinBy(x => Math.Abs(x.GetProperty("timestamp").GetInt64() - target));
        StatsChart.ToolTip = $"{StatsTime(point.GetProperty("timestamp").GetInt64(), true)}\n" +
            $"{_loc.Text("stats.download")}: {StatsBytes(StatsNumber(point, "downloadBytes"))}\n" +
            $"{_loc.Text("stats.upload")}: {StatsBytes(StatsNumber(point, "uploadBytes"))}";
    }

    private void StatsChart_MouseLeave(object sender, MouseEventArgs e) => StatsChart.ToolTip = null;
}
