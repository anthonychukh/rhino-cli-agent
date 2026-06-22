using Rhino;
using RhinoAgent.Providers;
using RhinoAgent.Tools;

namespace RhinoAgent.Runtime;

public sealed class AgentServices
{
    private AgentServices(ProviderFactory providerFactory, RhinoToolHost toolHost, ApprovalService approvals)
    {
        ProviderFactory = providerFactory;
        ToolHost = toolHost;
        Approvals = approvals;
    }

    public ProviderFactory ProviderFactory { get; }
    public RhinoToolHost ToolHost { get; }
    public ApprovalService Approvals { get; }

    public static AgentServices Create(AgentConfig config, RhinoDoc doc)
    {
        var resolver = new CommandResolver();
        var auth = new AuthService(resolver);
        var providerFactory = new ProviderFactory(config, resolver, auth, doc);
        return new AgentServices(providerFactory, new RhinoToolHost(doc, config), new ApprovalService(config));
    }
}
