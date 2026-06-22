using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Rhino;
using Rhino.Geometry;

namespace RhinoAgent.Tools;

public sealed class CSharpScriptGlobals
{
    public required RhinoDoc doc { get; init; }
    public required StringBuilder output { get; init; }
}

public static class CSharpScriptRunner
{
    private static readonly Lazy<ScriptOptions> Options = new(BuildOptions);

    public static async Task<ToolExecutionResult> ExecuteAsync(string tool, RhinoDoc doc, string code)
    {
        var output = new StringBuilder();
        try
        {
            var globals = new CSharpScriptGlobals { doc = doc, output = output };
            var result = await CSharpScript.EvaluateAsync<object?>(code, Options.Value, globals, typeof(CSharpScriptGlobals));
            if (result is not null)
                output.AppendLine(result.ToString());
            return new ToolExecutionResult(tool, true, output.ToString(), true, false);
        }
        catch (CompilationErrorException ex)
        {
            foreach (var diagnostic in ex.Diagnostics)
                output.AppendLine(diagnostic.ToString());
            return new ToolExecutionResult(tool, false, output.ToString(), true, false);
        }
        catch (Exception ex)
        {
            output.AppendLine(ex.ToString());
            return new ToolExecutionResult(tool, false, output.ToString(), true, false);
        }
    }

    private static ScriptOptions BuildOptions()
    {
        return ScriptOptions.Default
            .AddReferences(
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(RhinoDoc).Assembly,
                typeof(Point3d).Assembly,
                Assembly.Load("System.Runtime"))
            .AddImports(
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "System.Text",
                "Rhino",
                "Rhino.Geometry",
                "Rhino.DocObjects",
                "Rhino.Commands");
    }
}
