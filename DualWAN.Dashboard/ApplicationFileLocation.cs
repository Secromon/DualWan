using System.Diagnostics;
using System.IO;

namespace DualWAN.Dashboard;

internal static class ApplicationFileLocation
{
    internal static string StatusKey(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return "apps.pathUnavailable";
        try
        {
            if (!Path.IsPathFullyQualified(executablePath) ||
                !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return "apps.pathUnavailable";
            return File.Exists(executablePath) ? "" : "apps.executableNotFound";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "apps.pathUnavailable";
        }
    }

    internal static bool TryCreate(string? executablePath, out ProcessStartInfo? startInfo)
    {
        startInfo = null;
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath)) return false;
            if (!Path.IsPathFullyQualified(executablePath)) return false;
            string fullPath = Path.GetFullPath(executablePath);
            if (!Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath)) return false;

            startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                Arguments = $"/select,\"{fullPath}\""
            };
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
