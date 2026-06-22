using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using RhinoAgent.Config;
using RhinoAgent.Runtime;

namespace RhinoAgent.Commands;

[Guid("2876BD8E-771A-4BE9-A0AC-3771D91FFAE5")]
public sealed class AgentStatusCommand : Command
{
    public override string EnglishName => "AgentStatus";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var config = AgentConfigStore.Load();
        var services = AgentServices.Create(config, doc);
        StatusPrinter.Print(config, services);
        return Result.Success;
    }
}
