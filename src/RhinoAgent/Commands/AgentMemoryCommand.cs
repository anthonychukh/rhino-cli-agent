using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using RhinoAgent.Memory;
using RhinoAgent.UI;

namespace RhinoAgent.Commands;

[Guid("42C7E7EE-73A9-4E66-A813-26639145C989")]
public sealed class AgentMemoryCommand : Command
{
    public override string EnglishName => "AgentMemory";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        AgentMemoryStore.EnsureCreated(doc);
        if (!AgentMemoryPanel.OpenPanel())
            return Result.Failure;

        RhinoApp.WriteLine("RhinoAgent memory panel opened.");
        return Result.Success;
    }
}
