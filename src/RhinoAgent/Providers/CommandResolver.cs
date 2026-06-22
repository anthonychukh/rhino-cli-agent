namespace RhinoAgent.Providers;

public sealed class CommandResolver
{
    public string? Resolve(string commandName, string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var names = OperatingSystem.IsWindows()
            ? new[] { $"{commandName}.exe", $"{commandName}.cmd", $"{commandName}.bat", commandName }
            : [commandName];

        foreach (var dir in EnumerateSearchDirectories(commandName))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate) && !IsWindowsAppsPackagePath(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchDirectories(string commandName)
    {
        var dirs = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            AddIfDirectory(dirs, Path.Combine(userProfile, ".local", "bin"));

            if (commandName.Equals("codex", StringComparison.OrdinalIgnoreCase))
            {
                var codexBin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
                AddIfDirectory(dirs, codexBin);

                if (Directory.Exists(codexBin))
                {
                    foreach (var subdir in Directory.GetDirectories(codexBin)
                                 .Select(path => new DirectoryInfo(path))
                                 .OrderByDescending(info => info.LastWriteTimeUtc))
                    {
                        AddIfDirectory(dirs, subdir.FullName);
                    }
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Rhino for Mac is usually launched as a GUI app, so it may not
            // inherit the user's shell PATH. Check common CLI install folders.
            AddIfDirectory(dirs, Path.Combine(home, ".local", "bin"));
            AddIfDirectory(dirs, Path.Combine(home, ".npm-global", "bin"));
            AddIfDirectory(dirs, "/opt/homebrew/bin");
            AddIfDirectory(dirs, "/usr/local/bin");
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            AddIfDirectory(dirs, dir.Trim());

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddIfDirectory(List<string> dirs, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            dirs.Add(path);
    }

    private static bool IsWindowsAppsPackagePath(string candidate)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var fullPath = Path.GetFullPath(candidate);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var packageRoot = Path.Combine(programFiles, "WindowsApps");
        return fullPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase);
    }
}
