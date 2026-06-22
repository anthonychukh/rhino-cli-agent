using Rhino;

namespace RhinoAgent.Providers;

public static class WorkingDirectoryResolver
{
    public static string Resolve(RhinoDoc doc, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return configured;

        if (!string.IsNullOrWhiteSpace(doc.Path))
        {
            var dir = Path.GetDirectoryName(doc.Path);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
