using System.Runtime.InteropServices;
using Rhino;
using Rhino.PlugIns;
using RhinoAgent.UI;

namespace RhinoAgent;

[Guid("17227B56-A4CD-4187-A826-2C166D509514")]
public sealed class RhinoAgentPlugin : PlugIn
{
    public static RhinoAgentPlugin? Instance { get; private set; }

    public RhinoAgentPlugin()
    {
        Instance = this;
    }

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        AgentMemoryPanel.Register(this);
        RhinoApp.WriteLine("RhinoAgent loaded. Type Agent to start an AI command-line session.");
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        AgentMemoryPanel.Shutdown();
        Instance = null;
        base.OnShutdown();
    }

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;
}
