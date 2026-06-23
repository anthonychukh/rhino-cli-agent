using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;
using RhinoAgent.Config;

namespace RhinoAgent.Commands;

[Guid("64FBEE39-54F9-4DD6-AC5F-30F15D922F3D")]
public sealed class AgentConfigCommand : Command
{
    public override string EnglishName => "AgentConfig";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var config = AgentConfigStore.Load();
        var provider = config.Provider;
        var processMode = config.ProviderProcessMode;
        var permissions = config.PermissionMode;

        var getter = new GetOption();
        getter.SetCommandPrompt("RhinoAgent config");
        getter.AddOptionEnumList("Provider", provider);
        getter.AddOptionEnumList("ProcessMode", processMode);
        getter.AddOptionEnumList("PermissionMode", permissions);
        getter.AddOption("Show");
        getter.AddOption("Save");
        getter.AcceptNothing(true);

        while (true)
        {
            var result = getter.Get();
            if (result == Rhino.Input.GetResult.Cancel)
                return Result.Cancel;
            if (result == Rhino.Input.GetResult.Nothing)
                break;

            var option = getter.Option();
            if (option is null)
                continue;

            switch (option.EnglishName)
            {
                case "Provider":
                    provider = (AgentProviderKind)option.CurrentListOptionIndex;
                    RhinoApp.WriteLine($"Provider = {provider}");
                    break;
                case "ProcessMode":
                    processMode = (AgentProviderProcessMode)option.CurrentListOptionIndex;
                    RhinoApp.WriteLine($"ProcessMode = {processMode}");
                    break;
                case "PermissionMode":
                    permissions = (AgentPermissionMode)option.CurrentListOptionIndex;
                    RhinoApp.WriteLine($"PermissionMode = {permissions}");
                    break;
                case "Show":
                    PrintConfig(config);
                    break;
                case "Save":
                    Save(config, provider, processMode, permissions);
                    return Result.Success;
            }
        }

        Save(config, provider, processMode, permissions);
        return Result.Success;
    }

    private static void Save(
        AgentConfig config,
        AgentProviderKind provider,
        AgentProviderProcessMode processMode,
        AgentPermissionMode permissions)
    {
        config.Provider = provider;
        config.ProviderProcessMode = processMode;
        config.PermissionMode = permissions;
        AgentConfigStore.Save(config);
        RhinoApp.WriteLine("RhinoAgent config saved.");
    }

    private static void PrintConfig(AgentConfig config)
    {
        RhinoApp.WriteLine($"Config path: {AgentConfigStore.ConfigPath}");
        RhinoApp.WriteLine($"Provider: {config.Provider}");
        RhinoApp.WriteLine($"ProcessMode: {config.ProviderProcessMode}");
        RhinoApp.WriteLine($"PermissionMode: {config.PermissionMode}");
        RhinoApp.WriteLine($"ClaudeModel: {config.ClaudeModel}");
        RhinoApp.WriteLine($"CodexModel: {config.CodexModel}");
        RhinoApp.WriteLine($"WorkingDirectory: {config.WorkingDirectory ?? "(document folder or home)"}");
    }
}
