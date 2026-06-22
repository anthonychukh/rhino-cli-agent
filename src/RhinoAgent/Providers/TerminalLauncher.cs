using System.Diagnostics;

namespace RhinoAgent.Providers;

public sealed record TerminalCommand(string Executable, IReadOnlyList<string> Arguments, string DisplayCommand);

public static class TerminalLauncher
{
    public static bool Launch(TerminalCommand command)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = true,
                    Arguments = "/k " + Quote(command.Executable) + " " + string.Join(" ", command.Arguments.Select(Quote))
                };
                Process.Start(psi);
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                var script = Quote(command.Executable) + " " + string.Join(" ", command.Arguments.Select(Quote));
                Process.Start(new ProcessStartInfo("osascript")
                {
                    UseShellExecute = false,
                    ArgumentList =
                    {
                        "-e",
                        $"tell application \"Terminal\" to do script \"{script.Replace("\"", "\\\"")}\""
                    }
                });
                return true;
            }

            Process.Start(new ProcessStartInfo(command.Executable)
            {
                UseShellExecute = true,
                Arguments = string.Join(" ", command.Arguments.Select(Quote))
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
            return "\"\"";
        return value.Contains(' ') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
    }
}
