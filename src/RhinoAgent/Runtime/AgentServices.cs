using Rhino;
using RhinoAgent.Memory;
using RhinoAgent.Providers;
using RhinoAgent.Skills;
using RhinoAgent.Tools;

namespace RhinoAgent.Runtime;

public sealed class AgentServices
{
    private AgentServices(
        RhinoDoc document,
        ProviderFactory providerFactory,
        RhinoToolHost toolHost,
        ApprovalService approvals,
        SkillStore skillStore,
        AgentMemoryUpdateService memoryUpdater)
    {
        Document = document;
        ProviderFactory = providerFactory;
        ToolHost = toolHost;
        Approvals = approvals;
        SkillStore = skillStore;
        MemoryUpdater = memoryUpdater;
    }

    public RhinoDoc Document { get; }
    public ProviderFactory ProviderFactory { get; }
    public RhinoToolHost ToolHost { get; }
    public ApprovalService Approvals { get; }
    public SkillStore SkillStore { get; }
    public AgentMemoryUpdateService MemoryUpdater { get; }

    public static AgentServices Create(AgentConfig config, RhinoDoc doc)
    {
        var resolver = new CommandResolver();
        var auth = new AuthService(resolver);
        var providerFactory = new ProviderFactory(config, resolver, auth, doc);
        var skillStore = new SkillStore();
        var memoryUpdater = new AgentMemoryUpdateService(
            doc,
            config,
            () => providerFactory.ResolveMaintenanceProvider(config));
        return new AgentServices(
            doc,
            providerFactory,
            new RhinoToolHost(doc, config, skillStore),
            new ApprovalService(config),
            skillStore,
            memoryUpdater);
    }
}
