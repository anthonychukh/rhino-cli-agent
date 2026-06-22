using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using RhinoAgent.Config;
using RhinoAgent.Runtime;

namespace RhinoAgent.Commands;

[Guid("4DECF3DC-4DC9-40E3-836E-B6F54313F48A")]
public sealed class AgentLoginCommand : Command
{
    public override string EnglishName => "AgentLogin";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var config = AgentConfigStore.Load();
        var services = AgentServices.Create(config, doc);
        LoginFlow.Run(config, services);
        return Result.Success;
    }
}
