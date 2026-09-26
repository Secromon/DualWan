using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Image = System.Windows.Controls.Image;

namespace DualWAN.Dashboard;

public partial class MainWindow
{
    private sealed record GroupModeChoice(string Id, string Label);
    private GroupRow? _groupOriginal;
    private readonly HashSet<string> _groupDraftMembers = new(StringComparer.OrdinalIgnoreCase);
    private ICollectionView? _groupView;
    private bool _groupEditorLoading;
    private bool _groupCreating;
    private bool _groupSelectionRollback;
    private bool _groupSaving;
    private readonly Dictionary<string, ImageSource?> _groupIcons = new(StringComparer.OrdinalIgnoreCase);

    private ImageSource? CatalogueIcon(ApplicationCatalogueEntry? entry)
    {
        if (string.IsNullOrWhiteSpace(entry?.ExecutablePath)) return null;
        string path = entry.ExecutablePath;
        if (_groupIcons.TryGetValue(path, out ImageSource? cached)) return cached;
        ImageSource? icon = null;
        try
        {
            if (File.Exists(path))
            {
                using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (extracted is not null)
                {
                    var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        extracted.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    image.Freeze(); icon = image;
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        _groupIcons[path] = icon;
        return icon;
    }

    private void InitializeGroupsView()
    {
        GroupsGrid.ItemsSource = _groups;
        _groupView = CollectionViewSource.GetDefaultView(_groups);
        _groupView.Filter = item => item is GroupRow row &&
            row.Name.Contains(GroupsSearch.Text ?? "", StringComparison.OrdinalIgnoreCase);
        ClearGroupEditor();
    }

    private void GroupsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _groupEditorLoading = true;
        _groupView?.Refresh();
        if (_groupOriginal is not null && _groupView?.Contains(_groupOriginal) == true)
            GroupsGrid.SelectedItem = _groupOriginal;
        _groupEditorLoading = false;
        if (_groupOriginal is not null && _groupView?.Contains(_groupOriginal) == false && !GroupIsDirty())
            ClearGroupEditor();
        UpdateGroupEmpty();
    }

    private void RestoreGroupSelection(string? name)
    {
        _groupEditorLoading = true;
        _groupView?.Refresh();
        GroupWanEdit.ItemsSource = _wans.ToArray();
        GroupModeEdit.ItemsSource = new[]
        {
            new GroupModeChoice("FAILOVER", _loc.Text("groups.preferMode")),
            new GroupModeChoice("STRICT", _loc.Text("groups.onlyMode"))
        };
        GroupRow? match = _groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match is not null && (_groupView?.Contains(match) ?? true)) GroupsGrid.SelectedItem = match;
        else match = null;
        _groupEditorLoading = false;
        LoadGroupEditor(match);
        UpdateGroupEmpty();
    }

    private void GroupsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_groupEditorLoading || _groupSelectionRollback) return;
        GroupRow? next = GroupsGrid.SelectedItem as GroupRow;
        if (ReferenceEquals(next, _groupOriginal) && !_groupCreating) return;
        if (!ConfirmDiscardGroup())
        {
            _groupSelectionRollback = true;
            GroupsGrid.SelectedItem = _groupOriginal;
            _groupSelectionRollback = false;
            return;
        }
        LoadGroupEditor(next);
    }

    private void BeginNewGroup()
    {
        if (!ConfirmDiscardGroup()) return;
        _groupEditorLoading = true;
        GroupsGrid.SelectedItem = null;
        _groupEditorLoading = false;
        _groupCreating = true;
        _groupOriginal = null;
        _groupDraftMembers.Clear();
        GroupNameEdit.Text = "";
        GroupWanEdit.SelectedValue = _wans.FirstOrDefault()?.Id;
        GroupModeEdit.SelectedValue = "FAILOVER";
        GroupEnabledEdit.IsChecked = true;
        GroupEditor.Visibility = Visibility.Visible;
        GroupsEmpty.Visibility = Visibility.Collapsed;
        GroupEditorTitle.Text = _loc.Text("groups.new");
        GroupDeleteButton.Visibility = Visibility.Collapsed;
        RenderGroupMembers();
        UpdateGroupSave();
        GroupNameEdit.Focus();
    }

    private void LoadGroupEditor(GroupRow? group)
    {
        _groupEditorLoading = true;
        _groupOriginal = group;
        _groupCreating = false;
        _groupDraftMembers.Clear();
        if (group is not null)
        {
            foreach (string process in group.Applications) _groupDraftMembers.Add(process);
            GroupNameEdit.Text = group.Name;
            GroupWanEdit.SelectedValue = group.Wan;
            GroupModeEdit.SelectedValue = group.Mode;
            GroupEnabledEdit.IsChecked = group.Enabled;
            GroupEditorTitle.Text = group.Name;
            GroupEditor.Visibility = Visibility.Visible;
            GroupsEmpty.Visibility = Visibility.Collapsed;
            GroupDeleteButton.Visibility = Visibility.Visible;
        }
        else
        {
            GroupNameEdit.Text = "";
            GroupEditor.Visibility = Visibility.Collapsed;
            GroupsEmpty.Visibility = Visibility.Visible;
        }
        _groupEditorLoading = false;
        RenderGroupMembers();
        UpdateGroupSave();
        UpdateGroupEmpty();
    }

    private void ClearGroupEditor() => LoadGroupEditor(null);

    private void UpdateGroupEmpty()
    {
        if (GroupEditor.Visibility == Visibility.Visible) return;
        GroupsEmpty.Text = _groups.Count == 0 ? _loc.Text("groups.empty") :
            _groupView?.IsEmpty == true ? _loc.Text("groups.searchEmpty") : _loc.Text("groups.select");
    }

    private bool ConfirmDiscardGroup() => !GroupIsDirty() ||
        MessageBox.Show(this, _loc.Text("groups.discard"), "DualWAN",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private bool GroupIsDirty()
    {
        if (!_groupCreating && _groupOriginal is null) return false;
        if (_groupCreating) return GroupNameEdit.Text.Length > 0 || _groupDraftMembers.Count > 0 ||
            GroupEnabledEdit.IsChecked != true || (GroupWanEdit.SelectedValue as string) != _wans.FirstOrDefault()?.Id ||
            (GroupModeEdit.SelectedValue as string) != "FAILOVER";
        GroupRow group = _groupOriginal!;
        return !group.Name.Equals(GroupNameEdit.Text.Trim(), StringComparison.Ordinal) ||
            !group.Wan.Equals(GroupWanEdit.SelectedValue as string, StringComparison.OrdinalIgnoreCase) ||
            !group.Mode.Equals(GroupModeEdit.SelectedValue as string, StringComparison.OrdinalIgnoreCase) ||
            group.Enabled != (GroupEnabledEdit.IsChecked == true) ||
            !_groupDraftMembers.SetEquals(group.Applications);
    }

    private void GroupEditor_Changed(object sender, RoutedEventArgs e)
    {
        if (_groupEditorLoading || GroupSaveButton is null) return;
        UpdateGroupSave();
    }

    private void UpdateGroupSave() => GroupSaveButton.IsEnabled = !_groupSaving && GroupsGrid.IsEnabled &&
        (_groupCreating || _groupOriginal is not null) && GroupIsDirty();

    private void RenderGroupMembers()
    {
        if (GroupMembersPanel is null) return;
        GroupMembersPanel.Children.Clear();
        foreach (string process in _groupDraftMembers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            ApplicationCatalogueEntry? entry = _loc.FindApplication(process);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            var remove = new Button { Content = _loc.Text("groups.removeMember"), Tag = process, ToolTip = _loc.Text("groups.removeMember") };
            remove.SetResourceReference(FrameworkElement.StyleProperty, "RowActionButton");
            remove.Click += (_, _) => { _groupDraftMembers.Remove(process); RenderGroupMembers(); UpdateGroupSave(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            ImageSource? iconSource = CatalogueIcon(entry);
            if (iconSource is not null)
            {
                var icon = new Image { Source = iconSource, Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0) };
                DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
            }
            var details = new StackPanel();
            details.Children.Add(new TextBlock { Text = entry?.DisplayName ?? process,
                FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var secondary = new TextBlock { Text = process, TextTrimming = TextTrimming.CharacterEllipsis };
            secondary.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            details.Children.Add(secondary);
            if (!string.IsNullOrWhiteSpace(entry?.ExecutablePath))
            {
                var path = new TextBlock { Text = entry.ExecutablePath, TextTrimming = TextTrimming.CharacterEllipsis };
                path.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                details.Children.Add(path);
            }
            if (_rules.Any(rule => rule.Enabled && rule.Process.Equals(process, StringComparison.OrdinalIgnoreCase)))
            {
                var state = new TextBlock { Text = _loc.Text("groups.individualOverride") };
                state.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                details.Children.Add(state);
            }
            row.Children.Add(details);
            GroupMembersPanel.Children.Add(row);
        }
        GroupAppsLabel.Text = string.Format(_loc.Text("groups.appCount"), _groupDraftMembers.Count);
    }

    private void GroupAddApps_Click(object sender, RoutedEventArgs e)
    {
        var picker = new GroupApplicationPicker(this, _groupDraftMembers);
        if (picker.ShowDialog() != true) return;
        _groupDraftMembers.Clear();
        foreach (string process in picker.SelectedProcesses) _groupDraftMembers.Add(process);
        RenderGroupMembers();
        UpdateGroupSave();
    }

    private async void GroupSave_Click(object sender, RoutedEventArgs e)
    {
        string name = GroupNameEdit.Text.Trim();
        if (name.Length is < 1 or > 64 || name.IndexOfAny(['\\', '/', '\0']) >= 0)
        { ShowGroupError(_loc.Text("groups.invalidName")); return; }
        if (GroupWanEdit.SelectedItem is not WanChoice wan)
        { ShowGroupError(_loc.Text("groups.error.invalidWan")); return; }
        if (GroupModeEdit.SelectedValue is not string mode)
        { ShowGroupError(_loc.Text("groups.error.invalidMode")); return; }
        if (_groups.Any(g => !g.Name.Equals(_groupOriginal?.Name, StringComparison.OrdinalIgnoreCase) &&
            g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { ShowGroupError(_loc.Text("groups.error.nameExists")); return; }
        var group = new GroupRow(name, wan.Id, wan.Display, mode, GroupEnabledEdit.IsChecked == true,
            _groupDraftMembers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            string.Format(_loc.Text("groups.appCount"), _groupDraftMembers.Count),
            _loc.Text(GroupEnabledEdit.IsChecked == true ? "common.enabled" : "groups.disabled"))
            { PolicyDisplay = GroupPolicyLabel(wan.Id, mode, GroupEnabledEdit.IsChecked == true) };
        string? original = _groupOriginal?.Name;
        _groupSaving = true; UpdateGroupSave();
        try
        {
            if (!await ApplyEditedGroupAsync(group, original)) return;
            _groupCreating = false;
            _groupOriginal = group;
            await RefreshGroupsAsync();
            RestoreGroupSelection(name);
        }
        finally { _groupSaving = false; UpdateGroupSave(); }
    }

    private void ShowGroupError(string message) =>
        MessageBox.Show(this, message, "DualWAN", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static string GroupErrorKey(int code) => code switch
    {
        20 => "groups.error.nameExists", 21 => "groups.error.notFound",
        22 => "groups.error.membership", 23 => "groups.error.duplicate",
        24 => "groups.error.invalidWan", 25 => "groups.error.invalidMode",
        _ => "groups.error.invalid"
    };

    private void ApplyGroupsLanguage()
    {
        GroupsTitle.Text = _loc.Text("groups.title");
        GroupsSubtitle.Text = _loc.Text("groups.subtitle");
        RefreshGroupsButton.Content = _loc.Text("common.refresh");
        AddGroupButton.Content = _loc.Text("groups.new");
        GroupsHelp.Text = _loc.Text("groups.help");
        GroupNameLabel.Text = _loc.Text("groups.name");
        GroupWanLabel.Text = _loc.Text("history.wan");
        GroupModeLabel.Text = _loc.Text("groups.policy");
        GroupAppsLabel.Text = string.Format(_loc.Text("groups.appCount"), _groupDraftMembers.Count);
        GroupEnabledEdit.Content = _loc.Text("common.enabled");
        GroupAddAppsButton.Content = _loc.Text("groups.addApps");
        GroupSaveButton.Content = _loc.Text("common.save");
        GroupDeleteButton.Content = _loc.Text("groups.delete");
        GroupsSearchLabel.Text = _loc.Text("groups.search");
        GroupsSearch.ToolTip = _loc.Text("groups.search");
        if (_groupCreating) GroupEditorTitle.Text = _loc.Text("groups.new");
        else if (_groupOriginal is not null) GroupEditorTitle.Text = _groupOriginal.Name;
        string? mode = GroupModeEdit.SelectedValue as string;
        _groupEditorLoading = true;
        GroupModeEdit.ItemsSource = new[] {
            new GroupModeChoice("FAILOVER", _loc.Text("groups.preferMode")),
            new GroupModeChoice("STRICT", _loc.Text("groups.onlyMode")) };
        GroupModeEdit.SelectedValue = mode;
        _groupEditorLoading = false;
        UpdateGroupEmpty();
        RenderGroupMembers();
    }

    private sealed class GroupApplicationPicker : Window
    {
        private readonly MainWindow _owner;
        private readonly HashSet<string> _selected;
        private readonly StackPanel _rows = new();
        private readonly TextBox _search = new();
        public IReadOnlyCollection<string> SelectedProcesses => _selected;

        public GroupApplicationPicker(MainWindow owner, IEnumerable<string> current)
        {
            _owner = owner;
            _selected = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
            Owner = owner; Title = owner._loc.Text("groups.addApps");
            Width = 620; Height = 640; MinWidth = 420; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            var layout = new DockPanel { Margin = new Thickness(18) };
            _search.ToolTip = owner._loc.Text("groups.searchApps");
            _search.Margin = new Thickness(0, 0, 0, 10);
            _search.TextChanged += (_, _) => Render();
            DockPanel.SetDock(_search, Dock.Top); layout.Children.Add(_search);
            var apply = new Button { Content = owner._loc.Text("common.apply"), IsDefault = true,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            apply.SetResourceReference(StyleProperty, "PrimaryButton");
            apply.Click += (_, _) => DialogResult = true;
            DockPanel.SetDock(apply, Dock.Bottom); layout.Children.Add(apply);
            layout.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = layout;
            Render();
        }

        private void Render()
        {
            _rows.Children.Clear();
            string term = _search.Text.Trim();
            foreach (ApplicationCatalogueEntry entry in _owner._loc.Applications
                .Where(x => x.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    x.ProcessName.Contains(term, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                string? otherGroup = _owner._groups.FirstOrDefault(g =>
                    !g.Name.Equals(_owner._groupOriginal?.Name, StringComparison.OrdinalIgnoreCase) &&
                    g.Applications.Contains(entry.ProcessName, StringComparer.OrdinalIgnoreCase))?.Name;
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                var check = new CheckBox { IsChecked = _selected.Contains(entry.ProcessName),
                    IsEnabled = otherGroup is null, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0) };
                check.Checked += (_, _) => _selected.Add(entry.ProcessName);
                check.Unchecked += (_, _) => _selected.Remove(entry.ProcessName);
                DockPanel.SetDock(check, Dock.Left); line.Children.Add(check);
                ImageSource? iconSource = _owner.CatalogueIcon(entry);
                if (iconSource is not null)
                {
                    var icon = new Image { Source = iconSource, Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0) };
                    DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
                }
                var details = new StackPanel();
                details.Children.Add(new TextBlock { Text = entry.DisplayName, FontWeight = FontWeights.SemiBold });
                details.Children.Add(Secondary(entry.ProcessName));
                if (!string.IsNullOrWhiteSpace(entry.ExecutablePath)) details.Children.Add(Secondary(entry.ExecutablePath));
                if (otherGroup is not null) details.Children.Add(Secondary(
                    string.Format(_owner._loc.Text("groups.inOther"), otherGroup)));
                line.Children.Add(details); _rows.Children.Add(line);
            }
        }

        private static TextBlock Secondary(string value)
        {
            var text = new TextBlock { Text = value, TextTrimming = TextTrimming.CharacterEllipsis };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return text;
        }
    }
}
