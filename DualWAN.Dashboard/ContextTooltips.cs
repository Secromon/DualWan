using System.Windows;
using System.Windows.Controls;

namespace DualWAN.Dashboard;

public partial class MainWindow
{
    private static void SetContextTooltip(FrameworkElement control, string text)
    {
        control.ToolTip = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        ToolTipService.SetInitialShowDelay(control, 300);
    }

    private void ApplyContextTooltips()
    {
        SetContextTooltip(AddApplicationButton, _loc.Text("context.addApplication"));
        SetContextTooltip(CreatePackagesButton, _loc.Text("context.detectApplications"));
        SetContextTooltip(NetworkActivityButton, _loc.Text("context.networkActivity"));
        SetContextTooltip(AddGroupButton, _loc.Text("context.addGroup"));
        foreach (System.Windows.Controls.Button button in new[] { Preset1Apply, Preset2Apply, Preset3Apply, Preset4Apply })
            SetContextTooltip(button, _loc.Text("context.applyPreset"));
        SetContextTooltip(ThemeSelect, _loc.Text("context.theme"));
        SetContextTooltip(LanguageSelect, _loc.Text("context.language"));
        SetContextTooltip(SaveSettingsButton, _loc.Text("context.saveSettings"));
    }
}
