using System.Text;
using Rhino;
using Rhino.Geometry;

namespace RhinoAgent.Tools;

public static class RhinoDocumentSummarizer
{
    public static string Summarize(RhinoDoc doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"name: {doc.Name ?? "(unsaved)"}");
        sb.AppendLine($"path: {doc.Path ?? "(unsaved)"}");
        sb.AppendLine($"units: {doc.ModelUnitSystem}");
        sb.AppendLine($"tolerance: {doc.ModelAbsoluteTolerance}");
        sb.AppendLine($"layers: {doc.Layers.Count}");
        sb.AppendLine($"objects: {doc.Objects.Count}");

        var typeCounts = doc.Objects
            .Where(o => !o.IsDeleted)
            .GroupBy(o => o.Geometry?.GetType().Name ?? o.ObjectType.ToString())
            .OrderByDescending(g => g.Count())
            .Take(12)
            .Select(g => $"{g.Key}={g.Count()}");
        sb.AppendLine($"objects_by_type: {string.Join(", ", typeCounts)}");

        var bbox = BoundingBox.Empty;
        foreach (var obj in doc.Objects.Where(o => !o.IsDeleted))
        {
            var objectBox = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (objectBox.IsValid)
                bbox.Union(objectBox);
        }

        sb.AppendLine($"bounding_box: {(bbox.IsValid ? bbox.ToString() : "(empty)")}");
        return sb.ToString();
    }
}
