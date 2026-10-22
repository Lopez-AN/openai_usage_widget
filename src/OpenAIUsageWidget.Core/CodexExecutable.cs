namespace OpenAIUsageWidget.Core;

public static class CodexExecutable
{
    public static string? Find(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            return IsExecutable(path) ? Path.GetFullPath(path) : null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var paths = new List<string>
        {
            Path.Combine(home, ".codex", "packages", "standalone", "current", "bin", "codex.exe"),
            Path.Combine(roaming, "npm", "codex.cmd")
        };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            paths.Add(Path.Combine(directory.Trim('"'), "codex.exe"));
            paths.Add(Path.Combine(directory.Trim('"'), "codex.cmd"));
        }
        return paths.FirstOrDefault(IsExecutable);
    }

    private static bool IsExecutable(string path) => File.Exists(path) &&
        (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(path).Equals(".cmd", StringComparison.OrdinalIgnoreCase)) &&
        !path.Contains('"') && !path.Contains('\r') && !path.Contains('\n');
}
