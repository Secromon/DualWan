using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using HAlign = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace DualWAN.Dashboard;

public partial class MainWindow
{
    private async void NetworkActivity_Click(object sender, RoutedEventArgs e)
    {
        var scan = new NetworkActivityScanWindow(this);
        if (scan.ShowDialog() != true || scan.Results.Count == 0)
        {
            if (scan.Results.Count == 0 && scan.Completed)
                MessageBox.Show(this, _loc.Text("networkDetect.none"), _loc.Text("networkDetect.title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var snapshots = _groups.Select(x => new PackageSnapshot(x.Name, x.Wan, x.Mode, x.Enabled, x.Applications)).ToArray();
        var proposals = ApplicationDetection.ProposeNetwork(scan.Results, snapshots);
        var preview = new NetworkActivityPreviewWindow(this, proposals);
        if (preview.ShowDialog() != true) return;
        var chosen = preview.SelectedProcesses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recognized = proposals.Where(x => x.Category != "Unclassified")
            .Select(x => new PackageSuggestion(new DetectedApplication(x.Application.ProcessName,
                x.Application.DisplayName, x.Application.ExecutablePath ?? "", "network"),
                x.Category, x.ConflictGroup, x.AlreadyMember)).ToArray();
        var plan = ApplicationDetection.Plan(recognized, snapshots, chosen);
        foreach (var planned in plan.GroupsToUpsert)
        {
            var group = new GroupRow(planned.Name, planned.Wan, FriendlyWan(planned.Wan), planned.Mode,
                planned.Enabled, planned.Applications, string.Format(_loc.Text("groups.appCount"), planned.Applications.Count),
                _loc.Text(planned.Enabled ? "common.enabled" : "common.disabled"));
            if (!await ApplyGroupAsync(group)) return;
            foreach (var app in plan.AcceptedApplications.Where(a => recognized.Any(s =>
                s.Application.ProcessName.Equals(a.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                s.Category.Equals(planned.Name, StringComparison.OrdinalIgnoreCase))))
                _loc.UpsertApplication(new(app.ProcessName, string.IsNullOrWhiteSpace(app.ExecutablePath) ? null : app.ExecutablePath,
                    app.DisplayName));
        }
        foreach (var item in proposals.Where(x => x.Category == "Unclassified" && x.ConflictGroup is null &&
            !x.AlreadyMember && chosen.Contains(x.Application.ProcessName)))
        {
            var app = item.Application;
            var old = _loc.FindApplication(app.ProcessName);
            _loc.UpsertApplication(new(app.ProcessName, app.ExecutablePath ?? old?.ExecutablePath, app.DisplayName));
        }
        RefreshApplications();
    }

    private sealed class NetworkActivityScanWindow : Window
    {
        private readonly MainWindow _parent;
        private readonly ComboBox _duration = new();
        private readonly TextBlock _status = new();
        private readonly Button _start = new();
        private readonly Button _cancel = new();
        private CancellationTokenSource? _stop;
        private bool _running;
        public bool Completed { get; private set; }
        public IReadOnlyList<DetectedNetworkApplication> Results { get; private set; } = [];

        public NetworkActivityScanWindow(MainWindow parent)
        {
            _parent = parent; Owner = parent; Title = parent._loc.Text("networkDetect.title");
            Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            var root = new StackPanel { Margin = new Thickness(24) };
            root.Children.Add(new TextBlock { Text = parent._loc.Text("networkDetect.description"), TextWrapping = TextWrapping.Wrap });
            root.Children.Add(new TextBlock { Text = parent._loc.Text("networkDetect.duration"), Margin = new Thickness(0, 16, 0, 5) });
            foreach (int seconds in NetworkActivityDetector.Durations)
                _duration.Items.Add(new ComboBoxItem { Content = $"{seconds} s", Tag = seconds });
            _duration.SelectedIndex = 0;
            _duration.Width = 120; _duration.HorizontalAlignment = HAlign.Left; root.Children.Add(_duration);
            _status.Margin = new Thickness(0, 14, 0, 0);
            _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            root.Children.Add(_status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right,
                Margin = new Thickness(0, 22, 0, 0) };
            _cancel.Content = parent._loc.Text("common.cancel"); _cancel.IsCancel = true;
            _cancel.Click += (_, _) => _stop?.Cancel();
            _start.Content = parent._loc.Text("networkDetect.start");
            _start.IsDefault = true; _start.Margin = new Thickness(10, 0, 0, 0);
            _start.SetResourceReference(StyleProperty, "PrimaryButton");
            _start.Click += StartOrStop_Click;
            actions.Children.Add(_cancel); actions.Children.Add(_start); root.Children.Add(actions);
            Content = root;
            Closing += (_, _) => _stop?.Cancel();
        }

        private async void StartOrStop_Click(object sender, RoutedEventArgs e)
        {
            if (_running) { _stop?.Cancel(); _start.IsEnabled = false; return; }
            int seconds = (int)((ComboBoxItem)_duration.SelectedItem).Tag;
            _running = true; _duration.IsEnabled = false;
            _start.Content = _parent._loc.Text("networkDetect.stop");
            _stop = new CancellationTokenSource();
            var progress = new Progress<NetworkScanProgress>(state =>
                _status.Text = $"{_parent._loc.Text("networkDetect.progress")}  " +
                    string.Format(_parent._loc.Text("networkDetect.remaining"), state.RemainingSeconds) + "  ·  " +
                    string.Format(_parent._loc.Text("networkDetect.found"), state.ApplicationsFound));
            try
            {
                Results = await Task.Run(() => new NetworkActivityDetector().ScanAsync(seconds, _stop.Token, progress));
                Completed = true;
                if (IsVisible) DialogResult = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                if (IsVisible) MessageBox.Show(this, _parent._loc.Text("common.error"), Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _stop.Dispose(); _stop = null; }
        }
    }

    private sealed class NetworkActivityPreviewWindow : Window
    {
        private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> SelectedProcesses => _choices.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToArray();

        public NetworkActivityPreviewWindow(MainWindow parent, IReadOnlyList<NetworkActivityProposal> proposals)
        {
            Owner = parent; Title = parent._loc.Text("detect.preview"); Width = 560; Height = 590;
            MinWidth = 450; MinHeight = 370; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            var root = new DockPanel { Margin = new Thickness(18) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right,
                Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom);
            actions.Children.Add(new Button { Content = parent._loc.Text("common.cancel"), IsCancel = true });
            var apply = new Button { Content = parent._loc.Text("detect.apply"), IsDefault = true,
                Margin = new Thickness(10, 0, 0, 0) };
            apply.SetResourceReference(StyleProperty, "PrimaryButton");
            apply.Click += (_, _) => DialogResult = true;
            actions.Children.Add(apply); root.Children.Add(actions);
            var list = new StackPanel();
            foreach (string category in new[] { "Browsers", "Gaming", "Cloud & Sync", "Unclassified" })
            {
                var members = proposals.Where(x => x.Category == category).ToArray();
                if (members.Length == 0) continue;
                list.Children.Add(new TextBlock { Text = category == "Unclassified" ? parent._loc.Text("networkDetect.unclassified") : category,
                    FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8) });
                foreach (var proposal in members)
                {
                    var row = new StackPanel { Margin = new Thickness(6, 0, 0, 10) };
                    var choice = new CheckBox { Content = $"{proposal.Application.DisplayName}  ·  {proposal.Application.ProcessName}",
                        IsChecked = proposal.ConflictGroup is null && !proposal.AlreadyMember,
                        IsEnabled = proposal.ConflictGroup is null && !proposal.AlreadyMember };
                    choice.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
                    row.Children.Add(choice);
                    if (proposal.ConflictGroup is not null || proposal.AlreadyMember)
                    {
                        var note = new TextBlock { Text = proposal.ConflictGroup is null ? parent._loc.Text("detect.already")
                            : string.Format(parent._loc.Text("detect.conflict"), proposal.ConflictGroup),
                            FontSize = 12, Margin = new Thickness(20, 2, 0, 0) };
                        note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                        row.Children.Add(note);
                    }
                    if (choice.IsEnabled) _choices[proposal.Application.ProcessName] = choice;
                    list.Children.Add(row);
                }
            }
            root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
        }
    }
}
