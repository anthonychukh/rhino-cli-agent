using System.Diagnostics;
using System.Text.Json;

namespace RhinoAgent.Providers;

public sealed class AuthService
{
    private readonly CommandResolver _resolver;

    public AuthService(CommandResolver resolver)
    {
        _resolver = resolver;
    }

    public ProviderStatus GetStatus(AgentProviderKind provider, string? configuredPath)
    {
        var exe = _resolver.Resolve(provider == AgentProviderKind.Claude ? "claude" : "codex", configuredPath);
        if (exe is null)
            return new ProviderStatus(provider, false, false, null, null, "Executable was not found on PATH.");

        try
        {
            return provider == AgentProviderKind.Claude
                ? ClaudeStatus(exe)
                : CodexStatus(exe);
        }
        catch (Exception ex)
        {
            return new ProviderStatus(provider, true, false, exe, null, ex.Message);
        }
    }

    private static ProviderStatus ClaudeStatus(string exe)
    {
        var result = Run(exe, ["auth", "status"]);
        if (result.ExitCode != 0)
            return new ProviderStatus(AgentProviderKind.Claude, true, false, exe, null, FirstNonEmpty(result.StdErr, result.StdOut));

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            var loggedIn = root.TryGetProperty("loggedIn", out var loggedInEl) && loggedInEl.GetBoolean();
            var email = root.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
            var method = root.TryGetProperty("authMethod", out var methodEl) ? methodEl.GetString() : null;
            return new ProviderStatus(AgentProviderKind.Claude, true, loggedIn, exe, email, method);
        }
        catch
        {
            return new ProviderStatus(AgentProviderKind.Claude, true, true, exe, null, result.StdOut.Trim());
        }
    }

    private static ProviderStatus CodexStatus(string exe)
    {
        var result = Run(exe, ["login", "status"]);
        var loggedIn = result.ExitCode == 0;
        var detail = FirstNonEmpty(result.StdOut, result.StdErr);
        return new ProviderStatus(AgentProviderKind.Codex, true, loggedIn, exe, null, detail);
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(string exe, IReadOnlyList<string> args)
    {
        using var process = new Process();
        process.StartInfo.FileName = exe;
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(10_000))
        {
            TryKill(process);
            return (-1, "", "Timed out while checking CLI authentication status.");
        }

        Task.WaitAll(stdoutTask, stderrTask);
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Status checks are best-effort; the caller reports the timeout.
        }
    }
}
