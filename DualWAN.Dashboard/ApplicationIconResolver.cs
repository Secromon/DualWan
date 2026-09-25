using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DualWAN.Dashboard;

internal sealed record ResolvedApplicationVisual(string? Path, string? Name, ImageSource? Icon);

internal static class ApplicationIconResolver
{
    private static readonly ConcurrentDictionary<string, ResolvedApplicationVisual> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static ResolvedApplicationVisual Resolve(string processName, string? cataloguePath)
    {
        string? path = ValidPath(cataloguePath) ?? RunningPath(processName) ?? AppPath(processName);
        if (path is null) return new(null, null, null);
        return Cache.GetOrAdd(path, static candidate =>
        {
            string? name = null;
            ImageSource? icon = null;
            try
            {
                var version = FileVersionInfo.GetVersionInfo(candidate);
                name = !string.IsNullOrWhiteSpace(version.FileDescription) ? version.FileDescription : version.ProductName;
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            try
            {
                using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(candidate);
                if (extracted is not null)
                {
                    var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        extracted.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    image.Freeze();
                    icon = image;
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            return new(candidate, name, icon);
        });
    }

    private static string? ValidPath(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full = System.IO.Path.GetFullPath(path);
            return System.IO.Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? RunningPath(string processName)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(processName)))
            {
                using (process)
                {
                    try
                    {
                        string? path = ValidPath(process.MainModule?.FileName);
                        if (path is not null) return path;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or
                        UnauthorizedAccessException or IOException) { Debug.WriteLine(ex); }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Debug.WriteLine(ex); }
        return null;
    }

    private static string? AppPath(string processName)
    {
        try
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + processName);
                string? path = ValidPath(key?.GetValue(null) as string);
                if (path is not null) return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine(ex);
        }
        return null;
    }
}
