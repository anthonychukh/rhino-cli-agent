using Rhino;
using RhinoAgent.Providers;
using RhinoAgent.Skills;
using RhinoAgent.Tools;

namespace RhinoAgent.Runtime;

public sealed class AgentServices
{
    private AgentServices(ProviderFactory providerFactory, RhinoToolHost toolHost, ApprovalService approvals, SkillStore skillStore)
    {
        ProviderFactory = providerFactory;
        ToolHost = toolHost;
        Approvals = approvals;
        SkillStore = skillStore;
    }

    public ProviderFactory ProviderFactory { get; }
    public RhinoToolHost ToolHost { get; }
    public ApprovalService Approvals { get; }
    public SkillStore SkillStore { get; }

    public static AgentServices Create(AgentConfig config, RhinoDoc doc)
    {
        var resolver = new CommandResolver();
        var auth = new AuthService(resolver);
        var providerFactory = new ProviderFactory(config, resolver, auth, doc);
        var skillStore = new SkillStore();
        return new AgentServices(providerFactory, new RhinoToolHost(doc, config, skillStore), new ApprovalService(config), skillStore);
    }
}
