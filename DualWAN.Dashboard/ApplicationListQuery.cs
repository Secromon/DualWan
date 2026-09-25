using System.Collections;
using System.IO;

namespace DualWAN.Dashboard;

internal static class ApplicationListQuery
{
    internal static bool Matches(string? search, string? displayName, string? processName,
        string? executablePath, string? groupName)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        string term = search.Trim();
        return new[] { displayName, processName, Path.GetFileName(executablePath), groupName }
            .Any(value => value?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true);
    }

    internal static int Compare(string? firstName, string? firstProcess, string? secondName, string? secondProcess)
    {
        int result = StringComparer.CurrentCultureIgnoreCase.Compare(
            string.IsNullOrWhiteSpace(firstName) ? firstProcess : firstName,
            string.IsNullOrWhiteSpace(secondName) ? secondProcess : secondName);
        return result != 0 ? result : StringComparer.CurrentCultureIgnoreCase.Compare(firstProcess, secondProcess);
    }
}
