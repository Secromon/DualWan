using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace DualWAN.Dashboard;

public partial class MainWindow
{
    // Keep the last queried series for redraws after resize, theme or language
    // changes; hover overlays belong to this window and its Dispatcher timer.
    private readonly Dictionary<string, JsonElement[]> _historySeries = new();
    private long _historyFrom, _historyTo;
    private readonly Dictionary<Canvas, (Border Border, TextBlock Text)> _historyTipOverlays = new();
    private readonly DispatcherTimer _historyTipTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Canvas? _historyTipCanvas;
    private Point _historyTipPosition;
    private bool _historyTipTimerReady;

    private static string AdapterName(long ifIndex)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.GetIPProperties().GetIPv4Properties()?.Index == ifIndex)?.Name
                ?? $"IfIndex {ifIndex}";
        }
        catch { return $"IfIndex {ifIndex}"; }
    }

    private void DrawHistoryPanels(IReadOnlyList<(string Wan, JsonElement[] Points)> series, long from, long to)
    {
        HideHistoryTip();
        _historyFrom = from; _historyTo = to;
        _historySeries.Clear();
        foreach (var item in series) _historySeries[item.Wan] = item.Points;
        RedrawHistoryPanels();
    }

    private void ClearHistoryPanels()
    {
        HideHistoryTip();
        _historySeries.Clear();
        RedrawHistoryPanels();
    }

    private void HistoryChart_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawHistoryPanels();

    private void RedrawHistoryPanels()
    {
        if (HistoryWan1Traffic is null) return;
        DrawWan("WAN1", HistoryWan1Traffic, HistoryWan1Quality, HistoryWan1Empty);
        DrawWan("WAN2", HistoryWan2Traffic, HistoryWan2Quality, HistoryWan2Empty);
    }

    private void DrawWan(string wan, Canvas traffic, Canvas quality, TextBlock empty)
    {
        traffic.Children.Clear(); quality.Children.Clear();
        var stats = wan == "WAN1" ? HistoryWan1Stats : HistoryWan2Stats;
        _historySeries.TryGetValue(wan, out var points);
        DrawStatistics(stats, points ?? []);
        if (points is null || points.Length == 0)
        {
            empty.Visibility = Visibility.Visible;
            return;
        }
        empty.Visibility = Visibility.Collapsed;
        DrawChart(traffic, points, true);
        DrawChart(quality, points, false);
    }

    private Brush HistoryBrush(string key) => (Brush)System.Windows.Application.Current.Resources[key];
    private CultureInfo HistoryCulture => CultureInfo.GetCultureInfo(_loc.CurrentCode);
    private static double Value(JsonElement point, string key) => point.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    private static double? ValidValue(JsonElement point, string key)
    {
        if (!point.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        double number = value.GetDouble();
        return double.IsFinite(number) && number >= 0 ? number : null;
    }
    private static long Timestamp(JsonElement point) => point.GetProperty("Timestamp").GetInt64();
    private static double Loss(JsonElement point)
    {
        double ok = Value(point, "ProbeSuccesses"), failed = Value(point, "ProbeFailures");
        return ok + failed > 0 ? 100 * failed / (ok + failed) : 0;
    }

    private void DrawStatistics(UniformGrid host, JsonElement[] points)
    {
        if (host is null) return;
        host.Children.Clear();
        var visible = points.Where(p => Timestamp(p) >= _historyFrom && Timestamp(p) <= _historyTo).ToArray();
        AddStatistic(host, _loc.Text("dashboard.download"), Metric(visible, "RxAverage"), FormatStatisticRate);
        AddStatistic(host, _loc.Text("dashboard.upload"), Metric(visible, "TxAverage"), FormatStatisticRate);
        AddStatistic(host, _loc.Text("history.latencyName"), Metric(visible, "LatencyAverage"),
            v => v.ToString("0.#", HistoryCulture) + " ms");
        var measured = visible.Where(p => ValidValue(p, "ProbeSuccesses") is not null && ValidValue(p, "ProbeFailures") is not null
            && Value(p, "ProbeSuccesses") + Value(p, "ProbeFailures") > 0).ToArray();
        var losses = measured.Select(Loss).ToArray();
        double probes = measured.Sum(p => Value(p, "ProbeSuccesses") + Value(p, "ProbeFailures"));
        (double? Min, double? Average, double? Max) lossMetric = losses.Length == 0 ? (null, null, null)
            : ((double?)losses.Min(), (double?)(100 * measured.Sum(p => Value(p, "ProbeFailures")) / probes), (double?)losses.Max());
        AddStatistic(host, _loc.Text("history.lossName"), lossMetric,
            v => v.ToString("0.#", HistoryCulture) + " %");
    }

    private static (double? Min, double? Average, double? Max) Metric(JsonElement[] points, string key)
    {
        var values = points.Select(p => ValidValue(p, key)).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return values.Length == 0 ? (null, null, null) : (values.Min(), values.Average(), values.Max());
    }

    private void AddStatistic(UniformGrid host, string title, (double? Min, double? Average, double? Max) metric, Func<double, string> format)
    {
        var content = new StackPanel();
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        content.Children.Add(heading);
        foreach (var item in new[] { (_loc.Text("dashboard.min"), metric.Min), (_loc.Text("history.average"), metric.Average), (_loc.Text("dashboard.max"), metric.Max) })
        {
            var line = new TextBlock { Text = $"{item.Item1}: {(item.Item2.HasValue ? format(item.Item2.Value) : "—")}",
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            line.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            content.Children.Add(line);
        }
        var card = new Border { Child = content, Padding = new Thickness(10), Margin = new Thickness(0, 0, 8, 0),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        host.Children.Add(card);
    }

    private string FormatStatisticRate(double value) => value >= 1073741824
        ? (value / 1073741824).ToString("0.##", HistoryCulture) + " GB/s"
        : value >= 1048576 ? (value / 1048576).ToString("0.##", HistoryCulture) + " MB/s"
        : value >= 1024 ? (value / 1024).ToString("0.##", HistoryCulture) + " KB/s"
        : value.ToString("0.##", HistoryCulture) + " B/s";

    private void DrawChart(Canvas canvas, JsonElement[] points, bool traffic)
    {
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        if (width < 180 || height < 100) return;
        const double left = 58, right = 47, top = 22, bottom = 34;
        double plotWidth = width - left - right, plotHeight = height - top - bottom;
        double max = traffic
            ? Math.Max(1, points.SelectMany(p => new[] { Value(p, "RxAverage"), Value(p, "TxAverage") }).Max())
            : Math.Max(1, points.Max(p => Value(p, "LatencyAverage")));
        double scale = traffic ? max >= 1048576 ? 1048576 : max >= 1024 ? 1024 : 1 : 1;
        string unit = traffic ? scale == 1048576 ? "MB/s" : scale == 1024 ? "KB/s" : "B/s" : "ms";
        Brush grid = HistoryBrush("BorderBrush"), caption = HistoryBrush("TextSecondaryBrush");
        for (int i = 0; i <= 3; i++)
        {
            double y = top + plotHeight * i / 3;
            var line = new Line { X1 = left, X2 = width - right, Y1 = y, Y2 = y, Stroke = grid, StrokeThickness = 1 };
            canvas.Children.Add(line);
            Label(canvas, (max * (3 - i) / 3 / scale).ToString("0.#", HistoryCulture), 3, y - 8, caption, 49);
            if (!traffic) Label(canvas, (100 * (3 - i) / 3).ToString("0", HistoryCulture) + "%", width - right + 5, y - 8, caption, right - 4);
        }
        Label(canvas, unit, 3, 1, caption, 55);
        if (!traffic) Label(canvas, "%", width - right + 5, 1, caption, right - 4);
        for (int i = 0; i <= 4; i++)
        {
            long ts = _historyFrom + (_historyTo - _historyFrom) * i / 4;
            string format = _historyTo - _historyFrom <= 86400 ? "HH:mm" : _historyTo - _historyFrom <= 604800 ? "ddd d" : "d MMM";
            Label(canvas, DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString(format, HistoryCulture), left + plotWidth * i / 4 - 26, height - bottom + 7, caption, 58);
        }
        double X(JsonElement p) => left + Math.Clamp((Timestamp(p) - _historyFrom) / (double)Math.Max(1, _historyTo - _historyFrom), 0, 1) * plotWidth;
        double Y(double v, double maximum) => top + plotHeight * (1 - Math.Clamp(v / maximum, 0, 1));
        if (traffic)
        {
            DrawArea(canvas, points, p => Value(p, "RxAverage"), max, X, Y, HistoryBrush("HistoryDownload"), top + plotHeight);
            DrawArea(canvas, points, p => Value(p, "TxAverage"), max, X, Y, HistoryBrush("HistoryUpload"), top + plotHeight);
            Legend(canvas, _loc.Text("dashboard.download"), HistoryBrush("HistoryDownload"), left + 8);
            Legend(canvas, _loc.Text("dashboard.upload"), HistoryBrush("HistoryUpload"), left + 115);
        }
        else
        {
            DrawLine(canvas, points, p => Value(p, "LatencyAverage"), max, X, Y, HistoryBrush("HistoryLatency"));
            DrawLine(canvas, points, Loss, 100, X, Y, HistoryBrush("HistoryPacketLoss"));
            Legend(canvas, _loc.Text("history.latencyName"), HistoryBrush("HistoryLatency"), left + 8);
            Legend(canvas, _loc.Text("history.lossName"), HistoryBrush("HistoryPacketLoss"), left + 115);
        }
        AttachHistoryTip(canvas);
    }

    private void AttachHistoryTip(Canvas canvas)
    {
        if (!_historyTipOverlays.TryGetValue(canvas, out var overlay))
        {
            var content = new TextBlock { FontSize = 12, IsHitTestVisible = false };
            content.SetResourceReference(TextBlock.ForegroundProperty, "PopupForegroundBrush");
            var border = new Border
            {
                Child = content, Padding = new Thickness(9, 6, 9, 6), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Visibility = Visibility.Collapsed, IsHitTestVisible = false
            };
            border.SetResourceReference(Border.BackgroundProperty, "PopupBackgroundBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
            overlay = (border, content);
            _historyTipOverlays[canvas] = overlay;
        }
        canvas.Children.Add(overlay.Border);
    }

    private static void DrawArea(Canvas canvas, JsonElement[] points, Func<JsonElement, double> value, double maximum,
        Func<JsonElement, double> x, Func<double, double, double> y, Brush brush, double baseline)
    {
        if (points.Length == 0) return;
        var polygon = new Polygon { Fill = brush.Clone(), Stroke = brush, StrokeThickness = 2, Opacity = .75, IsHitTestVisible = false };
        polygon.Points.Add(new Point(x(points[0]), baseline));
        foreach (var p in points) polygon.Points.Add(new Point(x(p), y(value(p), maximum)));
        polygon.Points.Add(new Point(x(points[^1]), baseline));
        canvas.Children.Add(polygon);
    }

    private static void DrawLine(Canvas canvas, JsonElement[] points, Func<JsonElement, double> value, double maximum,
        Func<JsonElement, double> x, Func<double, double, double> y, Brush brush)
    {
        var line = new Polyline { Stroke = brush, StrokeThickness = 2, IsHitTestVisible = false };
        foreach (var p in points) line.Points.Add(new Point(x(p), y(value(p), maximum)));
        canvas.Children.Add(line);
    }

    private static void Label(Canvas canvas, string text, double x, double y, Brush brush, double width)
    {
        var label = new TextBlock { Text = text, Foreground = brush, FontSize = 11, Width = width, IsHitTestVisible = false };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); canvas.Children.Add(label);
    }

    private static void Legend(Canvas canvas, string text, Brush brush, double x)
    {
        var swatch = new Rectangle { Width = 8, Height = 8, Fill = brush, IsHitTestVisible = false };
        Canvas.SetLeft(swatch, x); Canvas.SetTop(swatch, 7); canvas.Children.Add(swatch);
        Label(canvas, text, x + 12, 2, brush, 105);
    }

    private void HistoryChart_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        var position = e.GetPosition(canvas);
        if (_historyTipCanvas == canvas && Math.Abs(position.X - _historyTipPosition.X) <= 4 && Math.Abs(position.Y - _historyTipPosition.Y) <= 4) return;
        HideHistoryTip();
        string wan = canvas == HistoryWan1Traffic || canvas == HistoryWan1Quality ? "WAN1" : "WAN2";
        if (!_historySeries.TryGetValue(wan, out var points) || points.Length == 0) return;
        _historyTipCanvas = canvas;
        _historyTipPosition = position;
        if (!_historyTipTimerReady) { _historyTipTimer.Tick += HistoryTipTimer_Tick; _historyTipTimerReady = true; }
        _historyTipTimer.Start();
    }

    private void HistoryTipTimer_Tick(object? sender, EventArgs e)
    {
        _historyTipTimer.Stop();
        var canvas = _historyTipCanvas;
        if (canvas is null || !canvas.IsVisible || !canvas.IsMouseOver || !_historyTipOverlays.TryGetValue(canvas, out var overlay)) return;
        string wan = canvas == HistoryWan1Traffic || canvas == HistoryWan1Quality ? "WAN1" : "WAN2";
        if (!_historySeries.TryGetValue(wan, out var points) || points.Length == 0) return;
        double ratio = Math.Clamp((_historyTipPosition.X - 58) / Math.Max(1, canvas.ActualWidth - 105), 0, 1);
        long target = _historyFrom + (long)((_historyTo - _historyFrom) * ratio);
        var p = points.MinBy(p => Math.Abs(Timestamp(p) - target));
        string time = DateTimeOffset.FromUnixTimeSeconds(Timestamp(p)).ToLocalTime().ToString("g", HistoryCulture);
        overlay.Text.Text = canvas == HistoryWan1Traffic || canvas == HistoryWan2Traffic
            ? $"{time}\n{_loc.Text("dashboard.download")}: {FormatHistoryRate(Value(p, "RxAverage"))}\n{_loc.Text("dashboard.upload")}: {FormatHistoryRate(Value(p, "TxAverage"))}"
            : $"{time}\n{_loc.Text("history.latencyName")}: {Value(p, "LatencyAverage").ToString("0.#", HistoryCulture)} ms\n{_loc.Text("history.lossName")}: {Loss(p).ToString("0.#", HistoryCulture)}%";
        overlay.Border.Visibility = Visibility.Visible;
        overlay.Border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = Math.Clamp(_historyTipPosition.X + 12, 0, Math.Max(0, canvas.ActualWidth - overlay.Border.DesiredSize.Width));
        double y = Math.Clamp(_historyTipPosition.Y + 12, 0, Math.Max(0, canvas.ActualHeight - overlay.Border.DesiredSize.Height));
        Canvas.SetLeft(overlay.Border, x); Canvas.SetTop(overlay.Border, y);
    }

    private void HideHistoryTip()
    {
        _historyTipTimer.Stop();
        if (_historyTipCanvas is not null && _historyTipOverlays.TryGetValue(_historyTipCanvas, out var overlay))
            overlay.Border.Visibility = Visibility.Collapsed;
        _historyTipCanvas = null;
    }

    private void HistoryChart_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender == _historyTipCanvas) HideHistoryTip();
    }

    private void HistoryChart_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender == _historyTipCanvas && e.NewValue is false) HideHistoryTip();
    }

    private void HistoryTooltip_Closed(object? sender, EventArgs e)
    {
        HideHistoryTip();
        _historyTipOverlays.Clear();
    }

    private string FormatHistoryRate(double value) => value >= 1048576
        ? (value / 1048576).ToString("0.#", HistoryCulture) + " MB/s"
        : value >= 1024 ? (value / 1024).ToString("0.#", HistoryCulture) + " KB/s"
        : value.ToString("0.#", HistoryCulture) + " B/s";
}
