using Microsoft.Win32;

namespace DualWAN.Dashboard;

internal static class DashboardStartup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "DualWAN";
    internal static bool IsEnabled
    {
        get => Registry.CurrentUser.OpenSubKey(Key)?.GetValue(Name) is string value &&
               value.Equals($"\"{Environment.ProcessPath}\"", StringComparison.OrdinalIgnoreCase);
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(Name, false);
        }
    }
}
