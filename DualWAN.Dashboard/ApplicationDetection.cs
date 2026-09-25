using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DualWAN.Dashboard;

public sealed record DetectedApplication(string ProcessName, string DisplayName, string ExecutablePath, string Source);
public sealed record PackageSnapshot(string Name, string Wan, string Mode, bool Enabled, IReadOnlyList<string> Applications);
public sealed record PackageSuggestion(DetectedApplication Application, string Category, string? ConflictGroup, bool AlreadyMember)
{
    public bool MembershipSelected { get; init; } // Suggestions never imply consent.
    public bool CanJoinSuggestedGroup => Category != "Unclassified" && ConflictGroup is null && !AlreadyMember;
}
public sealed record PackagePlan(IReadOnlyList<PackageSnapshot> GroupsToUpsert, IReadOnlyList<DetectedApplication> AcceptedApplications);
public sealed record NetworkActivityProposal(DetectedNetworkApplication Application, string Category,
    string? ConflictGroup, bool AlreadyMember);

public static class ApplicationDetection
{
    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome.exe"] = "Browsers", ["msedge.exe"] = "Browsers", ["firefox.exe"] = "Browsers",
        ["brave.exe"] = "Browsers", ["opera.exe"] = "Browsers",
        ["steam.exe"] = "Gaming", ["EpicGamesLauncher.exe"] = "Gaming", ["Battle.net.exe"] = "Gaming",
        ["GalaxyClient.exe"] = "Gaming", ["GOGGalaxy.exe"] = "Gaming",
        ["OneDrive.exe"] = "Cloud & Sync", ["Dropbox.exe"] = "Cloud & Sync",
        ["GoogleDriveFS.exe"] = "Cloud & Sync", ["iCloudDrive.exe"] = "Cloud & Sync"
    };

    public static string? Classify(string processName) => Categories.GetValueOrDefault(processName);

    public static IReadOnlyList<NetworkActivityProposal> ProposeNetwork(IEnumerable<DetectedNetworkApplication> detected,
        IEnumerable<PackageSnapshot> groups)
    {
        var memberships = groups.SelectMany(g => g.Applications.Select(p => (Process: p, Group: g.Name)))
            .ToDictionary(x => x.Process, x => x.Group, StringComparer.OrdinalIgnoreCase);
        return detected.Select(app =>
        {
            string category = Classify(app.ProcessName) ?? "Unclassified";
            memberships.TryGetValue(app.ProcessName, out string? existing);
            return new NetworkActivityProposal(app, category,
                existing is not null && !existing.Equals(category, StringComparison.OrdinalIgnoreCase) ? existing : null,
                existing is not null && existing.Equals(category, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
    }

    public static IReadOnlyList<DetectedApplication> FromCatalogue(IEnumerable<ApplicationCatalogueEntry> entries)
    {
        return entries.Where(x => !string.IsNullOrWhiteSpace(x.ExecutablePath) && File.Exists(x.ExecutablePath))
            .Select(x => new DetectedApplication(x.ProcessName, x.DisplayName, x.ExecutablePath!, "catalogue")).ToArray();
    }

    public static IReadOnlyList<DetectedApplication> FromInstalledSoftware()
    {
        var found = new List<DetectedApplication>();
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) continue;
                foreach (string keyName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var entry = root.OpenSubKey(keyName);
                        string? path = ExecutableFromDisplayIcon(entry?.GetValue("DisplayIcon") as string);
                        if (path is null) continue;
                        string process = Path.GetFileName(path);
                        string display = entry?.GetValue("DisplayName") as string ?? process;
                        found.Add(new(process, string.IsNullOrWhiteSpace(display) ? process : display, path, "registry"));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or ArgumentException)
                    { Debug.WriteLine(ex); }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or ArgumentException)
            { Debug.WriteLine(ex); }
        }
        return found;
    }

    private static string? ExecutableFromDisplayIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        icon = icon.Trim();
        string candidate;
        if (icon.StartsWith('"'))
        {
            int end = icon.IndexOf('"', 1);
            if (end <= 1) return null;
            candidate = icon[1..end];
        }
        else
        {
            int comma = icon.LastIndexOf(',');
            candidate = comma >= 0 && int.TryParse(icon[(comma + 1)..].Trim(), out _) ? icon[..comma] : icon;
        }
        candidate = Environment.ExpandEnvironmentVariables(candidate.Trim());
        return candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate) ? candidate : null;
    }

    public static IReadOnlyList<DetectedApplication> Merge(params IEnumerable<DetectedApplication>[] sources)
    {
        var result = new Dictionary<string, DetectedApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        foreach (var app in source)
        {
            if (!app.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(app.ExecutablePath) || !File.Exists(app.ExecutablePath)) continue;
            if (!result.TryGetValue(app.ProcessName, out var previous)) result[app.ProcessName] = app;
            else if (previous.DisplayName.Equals(previous.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                     !app.DisplayName.Equals(app.ProcessName, StringComparison.OrdinalIgnoreCase))
                result[app.ProcessName] = app;
        }
        return result.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static IReadOnlyList<PackageSuggestion> Propose(IEnumerable<DetectedApplication> detected, IEnumerable<PackageSnapshot> groups)
    {
        var memberships = groups.SelectMany(g => g.Applications.Select(p => (Process: p, Group: g.Name)))
            .ToDictionary(x => x.Process, x => x.Group, StringComparer.OrdinalIgnoreCase);
        return detected.Select(a =>
            {
                string category = Classify(a.ProcessName) ?? "Unclassified";
                memberships.TryGetValue(a.ProcessName, out string? existing);
                return new PackageSuggestion(a, category,
                    existing is not null && !existing.Equals(category, StringComparison.OrdinalIgnoreCase) ? existing : null,
                    existing is not null && existing.Equals(category, StringComparison.OrdinalIgnoreCase));
            }).ToArray();
    }

    public static IReadOnlyList<string> SelectAllEligible(IEnumerable<PackageSuggestion> suggestions) =>
        suggestions.Where(x => x.CanJoinSuggestedGroup).Select(x => x.Application.ProcessName).ToArray();

    public static PackagePlan PlanOptIn(IEnumerable<PackageSuggestion> suggestions,
        IEnumerable<PackageSnapshot> existingGroups, IEnumerable<string> selectedMemberships)
    {
        var detected = suggestions.ToArray();
        var eligible = detected.Where(x => x.CanJoinSuggestedGroup).ToArray();
        var membershipPlan = Plan(eligible, existingGroups, selectedMemberships);
        var accepted = detected.Select(x => x.Application)
            .DistinctBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase).ToArray();
        return new PackagePlan(membershipPlan.GroupsToUpsert, accepted);
    }

    public static PackagePlan Plan(IEnumerable<PackageSuggestion> suggestions, IEnumerable<PackageSnapshot> existingGroups,
        IEnumerable<string> approvedProcesses)
    {
        var groups = existingGroups.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var approved = approvedProcesses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<DetectedApplication>();
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var suggestion in suggestions)
        {
            if (!approved.Contains(suggestion.Application.ProcessName) || suggestion.ConflictGroup is not null || suggestion.AlreadyMember)
                continue;
            var occupied = groups.Values.FirstOrDefault(g => g.Applications.Contains(suggestion.Application.ProcessName, StringComparer.OrdinalIgnoreCase));
            if (occupied is not null) continue;
            if (!groups.TryGetValue(suggestion.Category, out var group))
                group = new PackageSnapshot(suggestion.Category, "WAN1", "FAILOVER", false, []);
            group = group with { Applications = [.. group.Applications, suggestion.Application.ProcessName] };
            groups[group.Name] = group;
            changed.Add(group.Name);
            accepted.Add(suggestion.Application);
        }
        return new PackagePlan(changed.Select(name => groups[name]).ToArray(), accepted);
    }
}
