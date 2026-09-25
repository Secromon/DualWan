using System.IO;
using System.Text.Json;

namespace DualWAN.Dashboard;

public sealed record LanguagePack(string Code, string DisplayName, IReadOnlyDictionary<string, string> Strings);
public sealed record ApplicationCatalogueEntry(string ProcessName, string? ExecutablePath, string DisplayName);

public sealed class LocalizationService
{
    private readonly Dictionary<string, LanguagePack> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ApplicationCatalogueEntry> _applications = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualWAN", "dashboard.json");
    public IReadOnlyCollection<LanguagePack> Packs => _packs.Values.OrderBy(x => x.DisplayName).ToArray();
    public IReadOnlyCollection<ApplicationCatalogueEntry> Applications => _applications.Values.ToArray();
    public string CurrentCode { get; private set; } = "en-US";
    public string CurrentTheme { get; private set; } = "System";
    public bool PackageDefaultsInitialized { get; private set; }

    public LocalizationService()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Languages");
        if (Directory.Exists(folder)) foreach (string file in Directory.EnumerateFiles(folder, "*.json")) Load(file);
        if (!_packs.ContainsKey("en-US")) throw new InvalidDataException("Canonical en-US language pack is missing.");
        try
        {
            if (File.Exists(_settingsPath))
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                if (settings.RootElement.TryGetProperty("language", out var language)) CurrentCode = language.GetString() ?? "en-US";
                if (settings.RootElement.TryGetProperty("theme", out var theme)) CurrentTheme = theme.GetString() ?? "System";
                if (settings.RootElement.TryGetProperty("packageDefaultsInitialized", out var initialized) && initialized.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    PackageDefaultsInitialized = initialized.GetBoolean();
                if (settings.RootElement.TryGetProperty("applications", out var applications) && applications.ValueKind == JsonValueKind.Array)
                    foreach (var item in applications.EnumerateArray())
                    {
                        string? process = item.TryGetProperty("processName", out var name) ? name.GetString() : null;
                        if (string.IsNullOrWhiteSpace(process) || !process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(process) != process) continue;
                        string? path = item.TryGetProperty("executablePath", out var savedPath) && savedPath.ValueKind == JsonValueKind.String
                            ? savedPath.GetString() : null;
                        string display = item.TryGetProperty("displayName", out var savedName) && savedName.ValueKind == JsonValueKind.String
                            ? savedName.GetString() ?? process : process;
                        _applications[process] = new(process, path, string.IsNullOrWhiteSpace(display) ? process : display);
                    }
            }
        }
        catch { CurrentCode = "en-US"; CurrentTheme = "System"; }
        if (!_packs.ContainsKey(CurrentCode)) CurrentCode = "en-US";
        if (CurrentTheme is not ("Light" or "Dark" or "System")) CurrentTheme = "System";
    }

    private void Load(string file)
    {
        try
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(file));var root=doc.RootElement;
            var meta=root.GetProperty("_meta");string code=meta.GetProperty("languageCode").GetString()!;string name=meta.GetProperty("displayName").GetString()!;
            if(string.IsNullOrWhiteSpace(code)||string.IsNullOrWhiteSpace(name))throw new InvalidDataException();
            var strings=new Dictionary<string,string>();foreach(var p in root.EnumerateObject())if(p.Name!="_meta"&&p.Value.ValueKind==JsonValueKind.String)strings[p.Name]=p.Value.GetString()!;
            _packs[code]=new(code,name,strings);
        }
        catch(Exception ex){System.Diagnostics.Debug.WriteLine($"Invalid language pack {file}: {ex.Message}");}
    }
    public string Text(string key){if(_packs.TryGetValue(CurrentCode,out var selected)&&selected.Strings.TryGetValue(key,out var value))return value;return _packs["en-US"].Strings.TryGetValue(key,out value)?value:key;}
    public void Select(string code){if(!_packs.ContainsKey(code))return;CurrentCode=code;Save();}
    public void SelectTheme(string theme){if(theme is not ("Light" or "Dark" or "System"))return;CurrentTheme=theme;Save();}
    public ApplicationCatalogueEntry? FindApplication(string process) => _applications.GetValueOrDefault(process);
    public void UpsertApplication(ApplicationCatalogueEntry application){_applications[application.ProcessName]=application;Save();}
    public void RemoveApplication(string process){if(_applications.Remove(process))Save();}
    public void MarkPackageDefaultsInitialized(){PackageDefaultsInitialized=true;Save();}
    public void AddMissingApplications(IEnumerable<string> processes)
    {
        bool changed=false;
        foreach(string process in processes)
            if(!_applications.ContainsKey(process)){_applications[process]=new(process,null,process);changed=true;}
        if(changed)Save();
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath,JsonSerializer.Serialize(new
        {
            language=CurrentCode,theme=CurrentTheme,packageDefaultsInitialized=PackageDefaultsInitialized,
            applications=_applications.Values.Select(x=>new{processName=x.ProcessName,executablePath=x.ExecutablePath,displayName=x.DisplayName}).ToArray()
        },new JsonSerializerOptions{WriteIndented=true}));
    }
}
