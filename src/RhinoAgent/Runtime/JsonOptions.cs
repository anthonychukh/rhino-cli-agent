using System.Text.Json;

namespace RhinoAgent.Runtime;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Loose = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
