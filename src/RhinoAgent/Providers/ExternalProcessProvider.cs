using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RhinoAgent.Providers;

public abstract class ExternalProcessProvider : IAgentProvider
{
    private readonly string _executablePath;
    private readonly string _workingDirectory;

    protected ExternalProcessProvider(string executablePath, string model, AgentPermissionMode permissionMode, string workingDirectory)
    {
        _executablePath = executablePath;
        Model = model;
        PermissionMode = permissionMode;
        _workingDirectory = workingDirectory;
    }

    public abstract AgentProviderKind Kind { get; }
    public abstract string DisplayName { get; }
    public AgentProviderProcessMode ProcessMode => AgentProviderProcessMode.Stateless;
    protected string Model { get; }
    protected AgentPermissionMode PermissionMode { get; }

    public async Task<AgentProviderResult> RunPromptAsync(
        string prompt,
        Action<AgentProgress> progress,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            return await RunPromptThroughCmdAsync(prompt, progress, cancellationToken).ConfigureAwait(false);

        using var process = new Process();
        process.StartInfo.FileName = _executablePath;
        foreach (var arg in BuildArguments(prompt))
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.WorkingDirectory = Directory.Exists(_workingDirectory)
            ? _workingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.CreateNoWindow = true;

        var stderr = new StringBuilder();
        var collector = CreateCollector(progress);

        progress(new AgentProgress($"Starting {DisplayName}: {_executablePath}"));
        process.Start();
        progress(new AgentProgress($"{DisplayName} process started: pid {process.Id}, exited {process.HasExited}."));
        await using var _ = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
                // Best-effort cancellation only.
            }
        });

        var stdoutTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;

                if (line is not null)
                    collector.AcceptLine(line);
            }
        }, cancellationToken);

        var stderrTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;

                if (line is not null)
                    stderr.AppendLine(line);
            }
        }, cancellationToken);

        await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
        progress(new AgentProgress($"{DisplayName} prompt written to stdin."));

        while (true)
        {
            process.Refresh();
            if (process.HasExited || !IsProcessAlive(process.Id))
                break;

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            progress(new AgentProgress($"{DisplayName} output drain timed out; using captured output so far."));
        }

        var exitCode = process.HasExited ? process.ExitCode : 0;
        return collector.ToResult(exitCode, stderr.ToString());
    }

    private async Task<AgentProviderResult> RunPromptThroughCmdAsync(
        string prompt,
        Action<AgentProgress> progress,
        CancellationToken cancellationToken)
    {
        // Rhino-hosted redirected stdio is unreliable for some Windows CLI tools.
        // A temp batch file with stdin/stdout files behaves closer to a user terminal.
        var tempDir = Path.Combine(Path.GetTempPath(), "RhinoAgent", "providers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var promptPath = Path.Combine(tempDir, "prompt.txt");
        var stdoutPath = Path.Combine(tempDir, "stdout.jsonl");
        var stderrPath = Path.Combine(tempDir, "stderr.txt");
        var batchPath = Path.Combine(tempDir, "run-provider.cmd");
        await File.WriteAllTextAsync(promptPath, prompt, cancellationToken).ConfigureAwait(false);

        var collector = CreateCollector(progress);
        var args = BuildArguments(prompt).Select(QuoteForCmd);
        var command = $"{QuoteForCmd(_executablePath)} {string.Join(" ", args)} < {QuoteForCmd(promptPath)} > {QuoteForCmd(stdoutPath)} 2> {QuoteForCmd(stderrPath)}";
        await File.WriteAllTextAsync(Path.Combine(tempDir, "command.txt"), command, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            batchPath,
            $"@echo off\r\ncd /d {QuoteForCmd(GetProcessWorkingDirectory())}\r\n{command}\r\nexit /b %ERRORLEVEL%\r\n",
            cancellationToken).ConfigureAwait(false);

        using var process = new Process();
        process.StartInfo.FileName = "cmd.exe";
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add(batchPath);
        process.StartInfo.WorkingDirectory = tempDir;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        progress(new AgentProgress($"Starting {DisplayName}: {_executablePath}"));
        process.Start();
        progress(new AgentProgress($"{DisplayName} cmd wrapper started: pid {process.Id}."));

        await using var _ = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
                // Best-effort cancellation only.
            }
        });

        while (true)
        {
            process.Refresh();
            if (process.HasExited || !IsProcessAlive(process.Id))
                break;

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(stdoutPath))
        {
            foreach (var line in File.ReadLines(stdoutPath))
                collector.AcceptLine(line);
        }

        var stderr = File.Exists(stderrPath) ? await File.ReadAllTextAsync(stderrPath, cancellationToken).ConfigureAwait(false) : "";
        var exitCode = process.HasExited ? process.ExitCode : 0;
        progress(new AgentProgress($"{DisplayName} cmd wrapper exited: {exitCode}; temp {tempDir}"));

        // Keep failed provider runs on disk; command.txt/stdout.jsonl/stderr.txt
        // are the fastest way to compare Rhino-hosted execution with a terminal.
        if (exitCode == 0)
            TryDeleteDirectory(tempDir);
        return collector.ToResult(exitCode, stderr);
    }

    protected abstract IReadOnlyList<string> BuildArguments(string prompt);
    protected abstract IProviderOutputCollector CreateCollector(Action<AgentProgress> progress);

    public void Reset()
    {
    }

    public void Dispose()
    {
    }

    protected static long? ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var longValue)
            ? longValue
            : null;
    }

    protected static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var decimalValue)
            ? decimalValue
            : null;
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private string GetProcessWorkingDirectory() => Directory.Exists(_workingDirectory)
        ? _workingDirectory
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string QuoteForCmd(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temp provider artifacts are best-effort cleanup.
        }
    }
}

public interface IProviderOutputCollector
{
    void AcceptLine(string line);
    AgentProviderResult ToResult(int exitCode, string stderr);
}
