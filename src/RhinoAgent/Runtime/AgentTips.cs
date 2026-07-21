namespace RhinoAgent.Runtime;

public static class AgentTips
{
    public static IReadOnlyList<string> All { get; } =
    [
        "Ask in plain language and include dimensions, units, materials, and constraints when they matter.",
        "Paste or drag files into the Agent prompt. Multiple files get placeholders such as [.stp 1] and [.stp 2].",
        "Paste a screenshot or image when visual context matters; RhinoAgent sends supported images through the provider's native image input.",
        "Keep modeling while Agent is open by typing native commands such as _Line, ! _Circle, or one of your Rhino aliases.",
        "Use /ask <prompt> when a chat request starts with a word Rhino might interpret as a command.",
        "Use /mode ask|auto|full|plan to choose how much freedom Agent has to run tools.",
        "Use /status to check the active provider, login state, model, process mode, and permissions.",
        "Use /model and /effort to tune the active provider. Restart Agent after changing settings that affect its provider session.",
        "Use /continue or /resume to reconnect to saved provider conversations for the current working directory.",
        "Use /clear to start a fresh conversation and remove the saved provider resume pointer for the current working directory.",
        "Use /skill list to see reusable workflows, or /skill use <name> <request> to invoke one explicitly.",
        "Use /memory status to inspect document memory, and /memory index to flush queued durable notes into the active .3dm.",
        "Use /debug off for a quieter command line and /usage off to hide provider token and cost messages.",
        "Use /timeout <seconds> to cap a provider turn, or /timeout off to let long jobs keep running.",
        "You can orbit, pan, zoom, and inspect panels while Agent is thinking; Rhino keeps pumping its UI during provider work.",
        "Ask Agent to capture the viewport when appearance matters, but use exact model inspection for dimensions, layers, IDs, and topology."
    ];

    public static string GetRandom() => All[Random.Shared.Next(All.Count)];

    public static string FormatAll() => string.Join(Environment.NewLine,
        new[] { "RhinoAgent tips:" }.Concat(
            All.Select((tip, index) => $"  {index + 1}. {tip}")));
}
