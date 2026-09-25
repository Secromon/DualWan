namespace DualWAN.Dashboard;

public partial class MainWindow
{
    private sealed record HelpSection(string Title, string Body);

    private static readonly string[] HelpTopics =
    [
        "what", "wans", "applications", "individual", "groups", "preferOnly", "failover",
        "wanDown", "lan", "detection", "networkDetection", "history", "presets",
        "limitations", "privacy"
    ];

    private void RefreshHelp()
    {
        if (HelpSections is null) return;
        HelpTitle.Text = _loc.Text("tab.help");
        string first = FriendlyWan("WAN1"), second = FriendlyWan("WAN2");
        HelpSections.ItemsSource = HelpTopics.Select(topic =>
        {
            string body = _loc.Text($"help.{topic}.body");
            body = topic switch
            {
                "wans" or "preferOnly" => string.Format(body, first, second),
                _ => body
            };
            return new HelpSection(_loc.Text($"help.{topic}.title"), body);
        }).ToArray();
    }
}
