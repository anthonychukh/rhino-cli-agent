using Rhino;
using Rhino.Input.Custom;
using RhinoAgent.Tools;

namespace RhinoAgent.Runtime;

public sealed class ApprovalService
{
    private readonly AgentConfig _config;

    public ApprovalService(AgentConfig config)
    {
        _config = config;
    }

    public bool ShouldExecute(ToolCallRequest call, RhinoToolHost host, out string reason)
    {
        if (_config.PermissionMode == AgentPermissionMode.Plan)
        {
            reason = "Plan mode: tool call was reported but not executed.";
            return false;
        }

        reason = "";
        return host.HasTool(call.Tool);
    }

    public bool RequiresPrompt(ToolCallRequest call, RhinoToolHost host)
    {
        if (_config.PermissionMode == AgentPermissionMode.FullAccess)
            return false;
        if (_config.PermissionMode == AgentPermissionMode.Ask)
            return true;

        // Auto mode is intentionally generous for in-document modeling, but
        // still asks before arbitrary command/script/file writes.
        return host.IsHighImpact(call.Tool);
    }

    public static bool PromptForApproval(ToolCallRequest call)
    {
        var getter = new GetOption();
        getter.SetCommandPrompt($"Approve Agent tool '{call.Tool}'?");
        getter.AddOption("Yes");
        getter.AddOption("No");
        getter.AcceptNothing(true);

        var result = getter.Get();
        if (result == Rhino.Input.GetResult.Nothing)
            return false;
        if (result != Rhino.Input.GetResult.Option)
            return false;

        var option = getter.Option();
        var approved = option?.EnglishName == "Yes";
        RhinoApp.WriteLine(approved ? "Approved." : "Denied.");
        return approved;
    }
}
