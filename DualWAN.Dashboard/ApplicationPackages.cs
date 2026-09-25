using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using HAlign = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace DualWAN.Dashboard;

public partial class MainWindow
{
    private string PackagePreference(GroupRow group)
    {
        if (!group.Enabled) return _loc.Text("apps.windows");
        string name = FriendlyWan(group.Wan);
        return group.Mode.Equals("STRICT", StringComparison.OrdinalIgnoreCase)
            ? string.Format(_loc.Text("apps.only"), name)
            : string.Format(_loc.Text("apps.prefer"), name);
    }

    private string PackagePreferenceKey(GroupRow group) => !group.Enabled ? "none" :
        (group.Wan.ToUpperInvariant(), group.Mode.ToUpperInvariant()) switch
        {
            ("WAN1", "FAILOVER") => "prefer1",
            ("WAN2", "FAILOVER") => "prefer2",
            ("WAN1", "STRICT") => "only1",
            ("WAN2", "STRICT") => "only2",
            _ => "none"
        };

    private void RefreshPackageCards()
    {
        if (PackageCards is null) return;
        PackagesTitle.Text = _loc.Text("packages.title");
        CreatePackagesButton.Content = _loc.Text("detect.start");
        CreatePackagesButton.IsEnabled = _groupsEverLoaded && GroupsGrid.IsEnabled;
        NetworkActivityButton.Content = _loc.Text("networkDetect.title");
        NetworkActivityButton.IsEnabled = _groupsEverLoaded && GroupsGrid.IsEnabled;
        PackageCards.Children.Clear();
        foreach (var group in _groups.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var card = new Border { Margin = new Thickness(0, 0, 0, 10) };
            card.SetResourceReference(StyleProperty, "Card");
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var details = new StackPanel();
            details.Children.Add(new TextBlock { Text = group.Name, FontSize = 16, FontWeight = FontWeights.SemiBold });
            var count = new TextBlock { Text = string.Format(_loc.Text("groups.appCount"), group.Applications.Count), Margin = new Thickness(0, 4, 0, 0) };
            count.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            details.Children.Add(count);
            var preference = new TextBlock { Text = PackagePreference(group), Margin = new Thickness(0, 5, 0, 0) };
            preference.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            details.Children.Add(preference);
            row.Children.Add(details);
            var edit = new Button { Content = _loc.Text("common.edit"), Tag = group, VerticalAlignment = VerticalAlignment.Center };
            edit.Click += EditPackage_Click;
            Grid.SetColumn(edit, 1); row.Children.Add(edit);
            card.Child = row; PackageCards.Children.Add(card);
        }
    }

    private async void CreatePackages_Click(object sender, RoutedEventArgs e)
    {
        if (new DetectionConsentWindow(this).ShowDialog() != true) return;
        CreatePackagesButton.IsEnabled = false;
        CreatePackagesButton.Content = _loc.Text("detect.progress");
        try
        {
            IReadOnlyList<DetectedApplication> catalogue = [], running = [], installed = [];
            try { catalogue = ApplicationDetection.FromCatalogue(_loc.Applications); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            try { running = RunningApplications().Select(x => new DetectedApplication(x.Process, x.Name, x.Path, "running")).ToArray(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            try { installed = await Task.Run(ApplicationDetection.FromInstalledSoftware); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            var detected = ApplicationDetection.Merge(catalogue, running, installed);
            var snapshots = _groups.Select(x => new PackageSnapshot(x.Name, x.Wan, x.Mode, x.Enabled, x.Applications)).ToArray();
            var suggestions = ApplicationDetection.Propose(detected, snapshots);
            if (suggestions.Count == 0)
            {
                MessageBox.Show(this, _loc.Text("detect.none"), _loc.Text("detect.title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var preview = new DetectionPreviewWindow(this, suggestions);
            if (preview.ShowDialog() != true) return;
            var plan = ApplicationDetection.PlanOptIn(suggestions, snapshots, preview.SelectedProcesses);
            foreach (var planned in plan.GroupsToUpsert)
            {
                var group = new GroupRow(planned.Name, planned.Wan, FriendlyWan(planned.Wan), planned.Mode,
                    planned.Enabled, planned.Applications, string.Format(_loc.Text("groups.appCount"), planned.Applications.Count),
                    _loc.Text(planned.Enabled ? "common.enabled" : "common.disabled"));
                if (!await ApplyGroupAsync(group)) return;
            }
            foreach (var app in plan.AcceptedApplications)
                _loc.UpsertApplication(new(app.ProcessName, app.ExecutablePath, app.DisplayName));
            RefreshApplications();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            MessageBox.Show(this, _loc.Text("common.error"), _loc.Text("detect.title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { CreatePackagesButton.Content = _loc.Text("detect.start"); CreatePackagesButton.IsEnabled = true; }
    }

    private async void EditPackage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GroupRow group) return;
        var editor = new PackageEditorWindow(this, group);
        if (editor.ShowDialog() != true) return;
        if (editor.DeleteRequested)
        {
            if (MessageBox.Show(this, string.Format(_loc.Text("packages.deleteConfirm"), group.Name),
                    _loc.Text("packages.delete"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await RunWriteAsync(new { apiVersion = 1, requestId = Guid.NewGuid().ToString("N"), command = "deleteGroup", name = group.Name });
            return;
        }
        if (editor.Result is null || !await ApplyGroupAsync(editor.Result)) return;
        foreach (var (process, path) in editor.NewPaths)
        {
            var metadata = MetadataFor(process, path);
            _loc.UpsertApplication(new(process, path, metadata.Name));
        }
        RefreshApplications();
    }

    private sealed class DetectionConsentWindow : Window
    {
        public DetectionConsentWindow(MainWindow parent)
        {
            Owner = parent; Title = parent._loc.Text("detect.title"); Width = 530;
            SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            var content = new StackPanel { Margin = new Thickness(24) };
            content.Children.Add(new TextBlock { Text = parent._loc.Text("detect.consent"), TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right,
                Margin = new Thickness(0, 20, 0, 0) };
            actions.Children.Add(new Button { Content = parent._loc.Text("common.cancel"), IsCancel = true });
            var start = new Button { Content = parent._loc.Text("detect.scan"), IsDefault = true,
                Margin = new Thickness(10, 0, 0, 0) };
            start.SetResourceReference(StyleProperty, "PrimaryButton");
            start.Click += (_, _) => DialogResult = true;
            actions.Children.Add(start); content.Children.Add(actions); Content = content;
        }
    }

    private sealed class DetectionPreviewWindow : Window
    {
        private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> SelectedProcesses => _choices.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToArray();

        public DetectionPreviewWindow(MainWindow parent, IReadOnlyList<PackageSuggestion> suggestions)
        {
            Owner = parent; Title = parent._loc.Text("detect.preview"); Width = 550; Height = 560;
            MinWidth = 440; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            var root = new DockPanel { Margin = new Thickness(18) };
            var hint = new TextBlock { Text = parent._loc.Text("detect.catalogueHint"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            DockPanel.SetDock(hint, Dock.Top);
            root.Children.Add(hint);
            var selectAll = new Button { Content = parent._loc.Text("detect.selectAll"),
                HorizontalAlignment = HAlign.Left, Margin = new Thickness(0, 0, 0, 8) };
            selectAll.Click += (_, _) =>
            {
                foreach (string process in ApplicationDetection.SelectAllEligible(suggestions))
                    if (_choices.TryGetValue(process, out var choice)) choice.IsChecked = true;
            };
            DockPanel.SetDock(selectAll, Dock.Top);
            root.Children.Add(selectAll);
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
                var members = suggestions.Where(x => x.Category == category).ToArray();
                if (members.Length == 0) continue;
                list.Children.Add(new TextBlock { Text = category == "Unclassified" ? parent._loc.Text("networkDetect.unclassified") : category,
                    FontSize = 17, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 14, 0, 8) });
                foreach (var suggestion in members)
                {
                    var row = new StackPanel { Margin = new Thickness(6, 0, 0, 10) };
                    row.Children.Add(new TextBlock { Text = $"{suggestion.Application.DisplayName}  ·  {suggestion.Application.ProcessName}",
                        FontWeight = FontWeights.SemiBold });
                    var suggested = new TextBlock { Text = category == "Unclassified"
                        ? parent._loc.Text("detect.noSuggestedGroup")
                        : string.Format(parent._loc.Text("detect.suggestedGroup"), category),
                        Margin = new Thickness(0, 2, 0, 0), FontSize = 12 };
                    suggested.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    row.Children.Add(suggested);
                    if (suggestion.CanJoinSuggestedGroup)
                    {
                        var choice = new CheckBox { Content = parent._loc.Text("detect.addToGroup"),
                            IsChecked = suggestion.MembershipSelected, Margin = new Thickness(0, 3, 0, 0) };
                        choice.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
                        row.Children.Add(choice);
                        _choices[suggestion.Application.ProcessName] = choice;
                    }
                    if (suggestion.ConflictGroup is not null || suggestion.AlreadyMember)
                    {
                        string existing = suggestion.ConflictGroup ?? category;
                        var note = new TextBlock { Text = string.Format(parent._loc.Text("detect.conflict"), existing),
                            Margin = new Thickness(0, 2, 0, 0), FontSize = 12 };
                        note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                        row.Children.Add(note);
                    }
                    list.Children.Add(row);
                }
            }
            root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
        }
    }

    private sealed class PackageEditorWindow : Window
    {
        private readonly MainWindow _parent;
        private readonly GroupRow _group;
        private readonly List<string> _members;
        private readonly Dictionary<string, string> _newPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly StackPanel _memberRows = new();
        private readonly ComboBox _choice = new();
        public GroupRow? Result { get; private set; }
        public bool DeleteRequested { get; private set; }
        public IReadOnlyDictionary<string, string> NewPaths => _newPaths;

        public PackageEditorWindow(MainWindow parent, GroupRow group)
        {
            _parent = parent; _group = group; _members = group.Applications.ToList();
            Owner = parent; Title = parent._loc.Text("packages.edit");
            Width = 570; Height = 620; MinWidth = 470; MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            var root = new DockPanel { Margin = new Thickness(18) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right,
                Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            var delete = new Button { Content = parent._loc.Text("packages.delete") };
            delete.SetResourceReference(StyleProperty, "DangerButton");
            delete.Click += (_, _) => { DeleteRequested = true; DialogResult = true; };
            var cancel = new Button { Content = parent._loc.Text("common.cancel"), IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
            var save = new Button { Content = parent._loc.Text("common.save"), IsDefault = true, Margin = new Thickness(10, 0, 0, 0) };
            save.SetResourceReference(StyleProperty, "PrimaryButton");
            save.Click += Save_Click;
            footer.Children.Add(delete); footer.Children.Add(cancel); footer.Children.Add(save); root.Children.Add(footer);

            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = parent._loc.Text("packages.name") });
            content.Children.Add(new TextBox { Text = group.Name, IsReadOnly = true, Margin = new Thickness(0, 4, 0, 12) });
            content.Children.Add(new TextBlock { Text = parent._loc.Text("apps.routingPreference") });
            _choice.ItemsSource = parent.PreferenceChoices();
            _choice.DisplayMemberPath = nameof(PreferenceChoice.Label);
            _choice.SelectedItem = ((PreferenceChoice[])_choice.ItemsSource).First(x => x.Key == parent.PackagePreferenceKey(group));
            _choice.Margin = new Thickness(0, 4, 0, 14); content.Children.Add(_choice);
            content.Children.Add(new TextBlock { Text = parent._loc.Text("groups.applications"), FontWeight = FontWeights.SemiBold });
            var sources = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 10) };
            var browse = new Button { Content = parent._loc.Text("apps.browse") };
            browse.Click += Browse_Click;
            var running = new Button { Content = parent._loc.Text("apps.running"), Margin = new Thickness(8, 0, 0, 0) };
            running.Click += Running_Click;
            sources.Children.Add(browse); sources.Children.Add(running); content.Children.Add(sources);
            content.Children.Add(_memberRows);
            root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root; RefreshMembers();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = _parent._loc.Text("apps.exeFilter"), CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(this) == true) AddMember(dialog.FileName);
        }

        private void Running_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new RunningApplicationsWindow(_parent, this);
            if (dialog.ShowDialog() == true && dialog.Selected is not null) AddMember(dialog.Selected.Path);
        }

        private void AddMember(string path)
        {
            string process = Path.GetFileName(path);
            var other = _parent._groups.FirstOrDefault(x => !x.Name.Equals(_group.Name, StringComparison.OrdinalIgnoreCase) &&
                x.Applications.Any(app => app.Equals(process, StringComparison.OrdinalIgnoreCase)));
            if (other is not null)
            {
                MessageBox.Show(this, string.Format(_parent._loc.Text("packages.inOther"), other.Name), "DualWAN",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!_members.Contains(process, StringComparer.OrdinalIgnoreCase)) _members.Add(process);
            _newPaths[process] = path;
            RefreshMembers();
        }

        private void RefreshMembers()
        {
            _memberRows.Children.Clear();
            foreach (string process in _members)
            {
                string? path = _newPaths.GetValueOrDefault(process) ?? _parent._loc.FindApplication(process)?.ExecutablePath;
                var metadata = _parent.MetadataFor(process, path);
                var card = new Border { Margin = new Thickness(0, 0, 0, 8) };
                card.SetResourceReference(StyleProperty, "Card");
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new Image { Source = metadata.Icon, Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center });
                var labels = new StackPanel(); Grid.SetColumn(labels, 1);
                labels.Children.Add(new TextBlock { Text = metadata.Name, FontWeight = FontWeights.SemiBold });
                labels.Children.Add(new TextBlock { Text = process, FontSize = 12 });
                if (_parent._rules.Any(x => x.Enabled && x.Process.Equals(process, StringComparison.OrdinalIgnoreCase)))
                {
                    var notice = new TextBlock { Text = _parent._loc.Text("packages.individualRule"), FontSize = 12 };
                    notice.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                    labels.Children.Add(notice);
                }
                row.Children.Add(labels);
                var remove = new Button { Content = _parent._loc.Text("packages.removeMember"), Tag = process,
                    VerticalAlignment = VerticalAlignment.Center };
                remove.Click += (_, _) => { _members.RemoveAll(x => x.Equals(process, StringComparison.OrdinalIgnoreCase));
                    _newPaths.Remove(process); RefreshMembers(); };
                Grid.SetColumn(remove, 2); row.Children.Add(remove);
                card.Child = row; _memberRows.Children.Add(card);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_choice.SelectedItem is not PreferenceChoice choice) return;
            RulePreference? rule = PreferenceMapping.ToRule(choice.Key);
            string wan = rule?.Wan ?? _group.Wan;
            string mode = rule?.Mode ?? _group.Mode;
            Result = _group with { Wan = wan, WanDisplay = _parent.FriendlyWan(wan), Mode = mode,
                Enabled = rule is not null, Applications = _members.ToArray(),
                ApplicationsLabel = string.Format(_parent._loc.Text("groups.appCount"), _members.Count),
                EnabledLabel = _parent._loc.Text(rule is null ? "common.disabled" : "common.enabled") };
            DialogResult = true;
        }
    }
}
