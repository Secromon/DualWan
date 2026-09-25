using System.Collections.ObjectModel;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Image = System.Windows.Controls.Image;
using SelectionMode = System.Windows.Controls.SelectionMode;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;
using HAlign = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace DualWAN.Dashboard;

public partial class MainWindow
{
    private readonly ObservableCollection<ApplicationRow> _applications = [];
    private readonly Dictionary<string, ResolvedApplicationVisual> _applicationVisualCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _applicationVisualLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _applicationIconLimit = new(4);
    private ListCollectionView? _applicationView;

    private sealed record ApplicationMetadata(string Name, ImageSource? Icon);
    private sealed record RunningApplicationChoice(string Name, string Process, string Path, ImageSource? Icon);
    private sealed record PreferenceChoice(string Key, string Label, string Tooltip);
    private sealed record RulePreference(string Wan, string Mode);
    private sealed record ApplicationRow(string Name, string Process, string Preference, string Group, string? GroupName,
        string Missing, ImageSource? Icon, string? Path, RuleRow? Rule, EffectivePolicySource Source, string Origin)
    {
        public bool CanOpenLocation => ApplicationFileLocation.TryCreate(Path, out _);
    }

    private sealed class ApplicationRowComparer : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var first = (ApplicationRow)x!;
            var second = (ApplicationRow)y!;
            return ApplicationListQuery.Compare(first.Name, first.Process, second.Name, second.Process);
        }
    }

    private void InitializeApplicationsView()
    {
        _applicationView = (ListCollectionView)CollectionViewSource.GetDefaultView(_applications);
        _applicationView.CustomSort = new ApplicationRowComparer();
        _applicationView.Filter = item => item is ApplicationRow row && ApplicationListQuery.Matches(
            ApplicationsSearch.Text, row.Name, row.Process, row.Path, row.GroupName);
    }

    private void ApplicationsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applicationView is null) return;
        ApplicationsSearchHint.Visibility = string.IsNullOrEmpty(ApplicationsSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        _applicationView.Refresh();
        UpdateApplicationsEmpty();
    }

    private void ClearApplicationsSearch_Click(object sender, RoutedEventArgs e) => ApplicationsSearch.Clear();

    private void OpenApplicationLocation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ApplicationRow row) return;
        if (!ApplicationFileLocation.TryCreate(row.Path, out ProcessStartInfo? startInfo))
        {
            MessageBox.Show(this, _loc.Text("apps.locationUnavailable"), _loc.Text("apps.openFileLocation"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(startInfo!); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or
            UnauthorizedAccessException or IOException)
        {
            Debug.WriteLine(ex);
            MessageBox.Show(this, _loc.Text("apps.locationUnavailable"), _loc.Text("apps.openFileLocation"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void UpdateApplicationsEmpty()
    {
        ApplicationsEmpty.Visibility = _applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplicationsSearchEmpty.Visibility = _applications.Count > 0 && _applicationView?.IsEmpty == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static class PreferenceMapping
    {
        public static string FromRule(RuleRow rule) => !rule.Enabled ? "none" : FromPolicy(rule.Wan, rule.Mode);

        public static string FromPolicy(string wan, string mode) => (wan.ToUpperInvariant(), mode.ToUpperInvariant()) switch
        {
            ("WAN1", "FAILOVER") => "prefer1",
            ("WAN2", "FAILOVER") => "prefer2",
            ("WAN1", "STRICT") => "only1",
            ("WAN2", "STRICT") => "only2",
            _ => "none"
        };

        public static RulePreference? ToRule(string key) => key switch
        {
            "prefer1" => new("WAN1", "FAILOVER"),
            "prefer2" => new("WAN2", "FAILOVER"),
            "only1" => new("WAN1", "STRICT"),
            "only2" => new("WAN2", "STRICT"),
            _ => null
        };
    }

    private string FriendlyWan(string id)
    {
        string? display = _wans.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Display;
        int separator = display?.IndexOf(" — ", StringComparison.Ordinal) ?? -1;
        return separator >= 0 ? display![(separator + 3)..] : display ?? id;
    }

    private PreferenceChoice[] PreferenceChoices(string? process = null)
    {
        string first = FriendlyWan("WAN1"), second = FriendlyWan("WAN2");
        bool hasGroup = process is not null && GroupFor(process) is not null;
        return
        [
            new("none", _loc.Text(ApplicationPolicyView.NoRuleLabelKey(hasGroup)),
                _loc.Text(hasGroup ? "context.groupDecides" : "context.noRule")),
            new("prefer1", string.Format(_loc.Text("apps.prefer"), first),
                string.Format(_loc.Text("context.prefer1"), first, second)),
            new("prefer2", string.Format(_loc.Text("apps.prefer"), second),
                string.Format(_loc.Text("context.prefer2"), first, second)),
            new("only1", string.Format(_loc.Text("apps.only"), first),
                string.Format(_loc.Text("context.only1"), first)),
            new("only2", string.Format(_loc.Text("apps.only"), second),
                string.Format(_loc.Text("context.only2"), first, second))
        ];
    }

    private GroupRow? MembershipGroupFor(string process) => _groups.FirstOrDefault(group =>
        group.Applications.Any(app => app.Equals(process, StringComparison.OrdinalIgnoreCase)));

    private GroupRow? ActiveGroupFor(string process)
    {
        GroupRow? group = MembershipGroupFor(process);
        return group is not null && ApplicationPolicyView.HasActiveGroupPolicy(group.Enabled, group.Wan, group.Mode)
            ? group : null;
    }

    private string? GroupFor(string process) => ActiveGroupFor(process)?.Name;

    private ApplicationMetadata MetadataFor(string process, string? path)
    {
        ResolvedApplicationVisual resolved = ApplicationIconResolver.Resolve(process, path);
        return new(string.IsNullOrWhiteSpace(resolved.Name) ? process : resolved.Name, resolved.Icon);
    }

    private static string VisualKey(string process, string? path) => process + "|" + path;

    private async void LoadApplicationVisual(string process, string? cataloguePath, string key)
    {
        try
        {
            await _applicationIconLimit.WaitAsync();
            ResolvedApplicationVisual resolved;
            try { resolved = await Task.Run(() => ApplicationIconResolver.Resolve(process, cataloguePath)); }
            finally { _applicationIconLimit.Release(); }
            if (Dispatcher.HasShutdownStarted) return;
            _applicationVisualCache[key] = resolved;
            for (int index = 0; index < _applications.Count; index++)
            {
                ApplicationRow row = _applications[index];
                if (!row.Process.Equals(process, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_loc.FindApplication(process)?.ExecutablePath, cataloguePath, StringComparison.OrdinalIgnoreCase)) continue;
                string name = row.Name.Equals(process, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(resolved.Name)
                    ? resolved.Name : row.Name;
                string? resolvedPath = resolved.Path ?? row.Path;
                string statusKey = ApplicationFileLocation.StatusKey(resolvedPath);
                _applications[index] = row with { Name = name, Icon = resolved.Icon, Path = resolvedPath,
                    Missing = statusKey.Length == 0 ? "" : _loc.Text(statusKey) };
            }
            UpdateApplicationsEmpty();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { _applicationVisualLoads.Remove(key); }
    }

    private RunningApplicationChoice[] RunningApplications()
    {
        var found = new Dictionary<string, RunningApplicationChoice>(StringComparer.OrdinalIgnoreCase);
        var systemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "svchost", "csrss", "services", "wininit", "winlogon", "System", "Idle" };
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero ||
                        systemNames.Contains(process.ProcessName) || found.ContainsKey(process.ProcessName)) continue;
                    string? path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                        path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                    string executable = System.IO.Path.GetFileName(path);
                    var metadata = MetadataFor(executable, path);
                    found[process.ProcessName] = new(metadata.Name, executable, path, metadata.Icon);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
                {
                    Debug.WriteLine(ex);
                }
            }
        }
        return found.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private void RefreshApplications()
    {
        if (ApplicationsList is null) return;
        _loc.AddMissingApplications(_rules.Select(x => x.Process));
        _applications.Clear();
        foreach (ApplicationCatalogueEntry entry in _loc.Applications)
        {
            RuleRow? rule = _rules.FirstOrDefault(x => x.Process.Equals(entry.ProcessName, StringComparison.OrdinalIgnoreCase));
            string? path = entry.ExecutablePath;
            string visualKey = VisualKey(entry.ProcessName, path);
            _applicationVisualCache.TryGetValue(visualKey, out var visual);
            string name = entry.DisplayName.Equals(entry.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(visual?.Name) ? visual.Name : entry.DisplayName;
            GroupRow? memberGroup = MembershipGroupFor(entry.ProcessName);
            EffectivePolicy effective = ApplicationPolicyView.Resolve(
                rule is null ? null : new PolicyInput(rule.Wan, rule.Mode, rule.Enabled),
                memberGroup is null ? null : new PolicyInput(memberGroup.Wan, memberGroup.Mode,
                    memberGroup.Enabled, memberGroup.Name));
            string key = effective.Source == EffectivePolicySource.Windows ? "none" :
                PreferenceMapping.FromPolicy(effective.Wan!, effective.Mode!);
            int choice = key switch { "prefer1" => 1, "prefer2" => 2, "only1" => 3, "only2" => 4, _ => 0 };
            string preference = effective.Source == EffectivePolicySource.Windows
                ? _loc.Text("apps.noRule") : PreferenceChoices(entry.ProcessName)[choice].Label;
            string origin = effective.Source switch
            {
                EffectivePolicySource.Individual => _loc.Text("apps.originIndividual"),
                EffectivePolicySource.Group => string.Format(_loc.Text("apps.originGroup"), effective.GroupName),
                _ => _loc.Text("apps.originWindows")
            };
            string membership = memberGroup is not null && effective.Source != EffectivePolicySource.Group
                ? string.Format(_loc.Text("apps.memberOfGroup"), memberGroup.Name) : "";
            string? displayPath = visual?.Path ?? path;
            string statusKey = ApplicationFileLocation.StatusKey(displayPath);
            _applications.Add(new(name, entry.ProcessName, preference, membership,
                memberGroup?.Name,
                statusKey.Length == 0 ? "" : _loc.Text(statusKey),
                visual?.Icon, displayPath, rule, effective.Source, origin));
            if (visual is null && _applicationVisualLoads.Add(visualKey))
                LoadApplicationVisual(entry.ProcessName, path, visualKey);
        }
        UpdateApplicationsEmpty();
        AddApplicationButton.IsEnabled = _rulesEverLoaded && RulesGrid.IsEnabled;
        RefreshPackageCards();
    }

    private async void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        var editor = new ApplicationEditorWindow(this, null);
        if (editor.ShowDialog() != true) return;
        if (editor.DuplicateProcess is not null)
        {
            ApplicationRow? existing = _applications.FirstOrDefault(x => x.Process.Equals(editor.DuplicateProcess, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) EditApplication(existing, editor.Path);
            return;
        }
        await SaveApplicationAsync(editor.Process!, editor.Path, editor.PreferenceKey!);
    }

    private void EditApplication_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ApplicationRow row) EditApplication(row);
    }

    private async void EditApplication(ApplicationRow row, string? selectedPath = null)
    {
        var editor = new ApplicationEditorWindow(this, row, selectedPath);
        if (editor.ShowDialog() == true)
            await SaveApplicationAsync(row.Process, editor.Path, editor.PreferenceKey!);
    }

    private async void RemoveApplication_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ApplicationRow row) return;
        if (MessageBox.Show(this, string.Format(_loc.Text("apps.removeConfirm"), row.Name),
                _loc.Text("apps.remove"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RemoveApplicationAsync(row);
    }

    private async Task RemoveApplicationAsync(ApplicationRow row)
    {
        if (row.Rule is not null && !await DeleteApplicationRuleAsync(row.Process)) return;
        _loc.RemoveApplication(row.Process);
        RefreshApplications();
        ShowGroupNotice(row.Process);
    }

    private async Task SaveApplicationAsync(string process, string? path, string choice)
    {
        ApplicationCatalogueEntry? previous = _loc.FindApplication(process);
        path ??= previous?.ExecutablePath;
        ApplicationMetadata metadata = MetadataFor(process, path);
        string displayName = metadata.Name.Equals(process, StringComparison.OrdinalIgnoreCase)
            ? previous?.DisplayName ?? process : metadata.Name;
        RulePreference? preference = PreferenceMapping.ToRule(choice);
        if (preference is null)
        {
            if (_rules.Any(x => x.Process.Equals(process, StringComparison.OrdinalIgnoreCase)))
                if (!await DeleteApplicationRuleAsync(process)) return;
            _loc.UpsertApplication(new(process, path, displayName));
            RefreshApplications();
            ShowGroupNotice(process);
            return;
        }
        if (!await ApplyRuleAsync(new RuleRow(process, preference.Wan, FriendlyWan(preference.Wan),
            preference.Mode, true, _loc.Text("common.enabled")))) return;
        _loc.UpsertApplication(new(process, path, displayName));
        RefreshApplications();
        ApplicationsNotice.Text = "";
    }

    private async Task<bool> DeleteApplicationRuleAsync(string process)
    {
        return await RunWriteAsync(new { apiVersion = 1, requestId = Guid.NewGuid().ToString("N"), command = "deleteRule", process });
    }

    private void ShowGroupNotice(string process)
    {
        string? group = GroupFor(process);
        ApplicationsNotice.Text = group is null ? "" : string.Format(_loc.Text("apps.managedByGroup"), group);
    }

    private sealed class ApplicationEditorWindow : Window
    {
        private readonly MainWindow _parent;
        private readonly ApplicationRow? _existing;
        private readonly TextBlock _selected = new();
        private readonly TextBlock _group = new();
        private readonly TextBlock _windowsDescription = new();
        private readonly ComboBox _choice = new();
        private string? _path;
        public string? Process { get; private set; }
        public string? Path => _path;
        public string? PreferenceKey => (_choice.SelectedItem as PreferenceChoice)?.Key;
        public string? DuplicateProcess { get; private set; }

        public ApplicationEditorWindow(MainWindow parent, ApplicationRow? existing, string? selectedPath = null)
        {
            _parent = parent;
            _existing = existing;
            Owner = parent;
            Title = parent._loc.Text(existing is null ? "apps.add" : "apps.edit");
            Width = 500;
            MinHeight = 300;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");

            var form = new StackPanel { Margin = new Thickness(24) };
            var browse = new Button { Content = parent._loc.Text("apps.browse"), HorizontalAlignment = HAlign.Left };
            browse.Click += Browse_Click;
            if (existing is null)
            {
                var sources = new StackPanel { Orientation = Orientation.Horizontal };
                sources.Children.Add(browse);
                var running = new Button { Content = parent._loc.Text("apps.running"), Margin = new Thickness(10, 0, 0, 0) };
                running.Click += Running_Click;
                sources.Children.Add(running);
                form.Children.Add(sources);
            }
            else { Process = existing.Process; _path = selectedPath ?? existing.Path; }
            string? selectedName = existing is not null && selectedPath is not null ? parent.MetadataFor(existing.Process, selectedPath).Name : existing?.Name;
            _selected.Text = selectedName is not null ? $"{selectedName}  ·  {existing!.Process}" : parent._loc.Text("apps.selectExecutable");
            _selected.TextWrapping = TextWrapping.Wrap;
            _selected.Margin = new Thickness(0, 12, 0, 4);
            form.Children.Add(_selected);
            _group.Text = existing?.Group ?? "";
            _group.TextWrapping = TextWrapping.Wrap;
            _group.Margin = new Thickness(0, 4, 0, 12);
            form.Children.Add(_group);
            form.Children.Add(new TextBlock { Text = parent._loc.Text("apps.routingPreference"), Margin = new Thickness(0, 6, 0, 6) });
            _choice.ItemsSource = parent.PreferenceChoices(Process);
            _choice.DisplayMemberPath = nameof(PreferenceChoice.Label);
            var itemStyle = new Style(typeof(ComboBoxItem),
                (Style)System.Windows.Application.Current.Resources["DualWanComboBoxItemStyle"]);
            itemStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
                new System.Windows.Data.Binding(nameof(PreferenceChoice.Tooltip))));
            itemStyle.Setters.Add(new Setter(ToolTipService.InitialShowDelayProperty, 300));
            _choice.ItemContainerStyle = itemStyle;
            _choice.SelectedItem = ((PreferenceChoice[])_choice.ItemsSource).First(x => x.Key == (existing?.Rule is null ? "none" : PreferenceMapping.FromRule(existing.Rule)));
            SetContextTooltip(_choice, ((PreferenceChoice)_choice.SelectedItem).Tooltip);
            form.Children.Add(_choice);
            _windowsDescription.Text = parent._loc.Text(ApplicationPolicyView.NoRuleDescriptionKey(Process is not null && parent.GroupFor(Process) is not null));
            _windowsDescription.TextWrapping = TextWrapping.Wrap;
            _windowsDescription.Margin = new Thickness(0, 8, 0, 0);
            _windowsDescription.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            _choice.SelectionChanged += (_, _) =>
            {
                _windowsDescription.Visibility = PreferenceKey == "none" ? Visibility.Visible : Visibility.Collapsed;
                if (_choice.SelectedItem is PreferenceChoice selected) SetContextTooltip(_choice, selected.Tooltip);
            };
            form.Children.Add(_windowsDescription);
            _windowsDescription.Visibility = PreferenceKey == "none" ? Visibility.Visible : Visibility.Collapsed;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right, Margin = new Thickness(0, 24, 0, 0) };
            var cancel = new Button { Content = parent._loc.Text("common.cancel"), IsCancel = true };
            var save = new Button { Content = parent._loc.Text("common.save"), IsDefault = true, Margin = new Thickness(10, 0, 0, 0) };
            save.SetResourceReference(StyleProperty, "PrimaryButton");
            save.Click += Save_Click;
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            form.Children.Add(buttons);
            Content = form;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var picker = new OpenFileDialog { Filter = _parent._loc.Text("apps.exeFilter"), CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(this) != true) return;
            ChooseExecutable(picker.FileName);
        }

        private void Running_Click(object sender, RoutedEventArgs e)
        {
            var picker = new RunningApplicationsWindow(_parent, this);
            if (picker.ShowDialog() == true && picker.Selected is not null) ChooseExecutable(picker.Selected.Path);
        }

        private void ChooseExecutable(string path)
        {
            _path = path;
            Process = System.IO.Path.GetFileName(_path);
            ApplicationRow? duplicate = _parent._applications.FirstOrDefault(x => x.Process.Equals(Process, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
            {
                if (MessageBox.Show(this, _parent._loc.Text("apps.alreadyConfigured"),
                        "DualWAN", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    DuplicateProcess = Process;
                    DialogResult = true;
                }
                else { _path = null; Process = null; }
                return;
            }
            ApplicationMetadata metadata = _parent.MetadataFor(Process, _path);
            _selected.Text = $"{metadata.Name}  ·  {Process}";
            string? group = _parent.GroupFor(Process);
            _group.Text = group is null ? "" : string.Format(_parent._loc.Text("apps.managedByGroup"), group);
            string? selectedKey = PreferenceKey;
            _choice.ItemsSource = _parent.PreferenceChoices(Process);
            _choice.SelectedItem = ((PreferenceChoice[])_choice.ItemsSource).First(x => x.Key == selectedKey);
            SetContextTooltip(_choice, ((PreferenceChoice)_choice.SelectedItem).Tooltip);
            _windowsDescription.Text = _parent._loc.Text(ApplicationPolicyView.NoRuleDescriptionKey(group is not null));
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (Process is null || (_existing is null && _path is null))
            {
                MessageBox.Show(this, _parent._loc.Text("apps.selectExecutable"), "DualWAN", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        }
    }

    private sealed class RunningApplicationsWindow : Window
    {
        private readonly ListBox _list = new();
        public RunningApplicationChoice? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as RunningApplicationChoice;

        public RunningApplicationsWindow(MainWindow parent, Window owner)
        {
            Owner = owner;
            Title = parent._loc.Text("apps.selectApplication");
            Width = 520; Height = 530; MinWidth = 420; MinHeight = 350;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");

            var root = new DockPanel { Margin = new Thickness(18) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right,
                Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var cancel = new Button { Content = parent._loc.Text("common.cancel"), IsCancel = true };
            var select = new Button { Content = parent._loc.Text("apps.select"), IsDefault = true,
                Margin = new Thickness(10, 0, 0, 0), IsEnabled = false };
            select.SetResourceReference(StyleProperty, "PrimaryButton");
            select.Click += (_, _) => { if (Selected is not null) DialogResult = true; };
            buttons.Children.Add(cancel); buttons.Children.Add(select); root.Children.Add(buttons);

            var choices = parent.RunningApplications();
            if (choices.Length == 0)
            {
                var empty = new TextBlock { Text = parent._loc.Text("apps.noRunning"), VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HAlign.Center };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                root.Children.Add(empty);
            }
            else
            {
                _list.SetResourceReference(ListBox.BackgroundProperty, "SurfaceBrush");
                _list.SetResourceReference(ListBox.ForegroundProperty, "TextPrimaryBrush");
                _list.SetResourceReference(ListBox.BorderBrushProperty, "BorderBrush");
                _list.SelectionMode = SelectionMode.Single;
                _list.SelectionChanged += (_, _) => select.IsEnabled = Selected is not null;
                _list.MouseDoubleClick += (_, _) => { if (Selected is not null) DialogResult = true; };
                foreach (var app in choices)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(new Image { Source = app.Icon, Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0) });
                    var labels = new StackPanel();
                    labels.Children.Add(new TextBlock { Text = app.Name, FontWeight = FontWeights.SemiBold });
                    var process = new TextBlock { Text = app.Process, FontSize = 12 };
                    process.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    labels.Children.Add(process); row.Children.Add(labels);
                    var item = new ListBoxItem { Content = row, Tag = app };
                    item.SetResourceReference(StyleProperty, "RunningAppListItem");
                    _list.Items.Add(item);
                }
                root.Children.Add(_list);
            }
            Content = root;
        }
    }
}

internal static class ApplicationPolicyView
{
    internal static EffectivePolicy Resolve(PolicyInput? individual, PolicyInput? group)
    {
        if (individual?.Enabled == true)
            return new(EffectivePolicySource.Individual, individual.Wan, individual.Mode, null);
        if (group is not null && HasActiveGroupPolicy(group.Enabled, group.Wan, group.Mode))
            return new(EffectivePolicySource.Group, group.Wan, group.Mode, group.Name);
        return new(EffectivePolicySource.Windows, null, null, null);
    }

    internal static bool HasActiveGroupPolicy(bool enabled, string wan, string mode) =>
        enabled && (wan.Equals("WAN1", StringComparison.OrdinalIgnoreCase) ||
                    wan.Equals("WAN2", StringComparison.OrdinalIgnoreCase)) &&
                   (mode.Equals("STRICT", StringComparison.OrdinalIgnoreCase) ||
                    mode.Equals("FAILOVER", StringComparison.OrdinalIgnoreCase));

    internal static string NoRuleLabelKey(bool activeGroup) =>
        activeGroup ? "apps.groupDecides" : "apps.noRule";

    internal static string NoRuleDescriptionKey(bool activeGroup) =>
        activeGroup ? "apps.groupDecidesDescription" : "apps.noRuleDescription";
}

internal enum EffectivePolicySource { Individual, Group, Windows }
internal sealed record PolicyInput(string Wan, string Mode, bool Enabled, string? Name = null);
internal sealed record EffectivePolicy(EffectivePolicySource Source, string? Wan, string? Mode, string? GroupName);

internal sealed record GroupMemberInput(string Process, string? DisplayName, PolicyInput? Individual);
internal sealed record GroupMemberState(string Process, string DisplayName, EffectivePolicy Effective);

internal static class GroupMemberPresentation
{
    internal static IReadOnlyList<GroupMemberState> Build(IEnumerable<GroupMemberInput> members, PolicyInput group) =>
        members.Select(member => new GroupMemberState(member.Process,
                string.IsNullOrWhiteSpace(member.DisplayName) ? member.Process : member.DisplayName,
                ApplicationPolicyView.Resolve(member.Individual, group)))
            .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.Process, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
