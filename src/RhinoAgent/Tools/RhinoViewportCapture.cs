using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Rhino;
using Rhino.DocObjects;
using Rhino.Display;
using Rhino.Geometry;
using RhinoAgent.Runtime;

namespace RhinoAgent.Tools;

public static class RhinoViewportCapture
{
    private const int DefaultWidth = 1600;
    private const int DefaultHeight = 1000;
    private const int MinSize = 512;
    private const int MaxSize = 4096;
    private const string ManifestFileName = "rhino-agent-viewport-capture.json";

    public static ToolExecutionResult Execute(RhinoDoc doc, ToolCallRequest call)
    {
        try
        {
            var request = CaptureRequest.From(call);
            var result = Capture(doc, request);
            return new ToolExecutionResult(call.Tool, result.Ok, result.CompactJson, true, false);
        }
        catch (Exception ex)
        {
            var output = JsonSerializer.Serialize(new
            {
                ok = false,
                reason = ex.Message
            }, JsonOptions.Loose);
            return new ToolExecutionResult(call.Tool, false, output, true, false);
        }
    }

    private static CaptureResult Capture(RhinoDoc doc, CaptureRequest request)
    {
        var warnings = new List<string>();
        var views = ResolveViews(doc, request.ViewNames, warnings);
        if (views.Count == 0)
            return CaptureResult.Failure("No Rhino viewport is available for capture.", warnings);

        var displayMode = ResolveDisplayMode(request.DisplayMode, warnings);
        var captureId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..25];
        var directory = Path.Combine(Path.GetTempPath(), "RhinoAgent", "captures", captureId);
        Directory.CreateDirectory(directory);

        var imagePaths = new List<string>();
        var captures = new List<Dictionary<string, object?>>();
        var documentBox = ComputeDocumentBoundingBox(doc);

        foreach (var view in views)
        {
            var fileName = $"rhino-agent-{SanitizeFileName(ViewName(view)).ToLowerInvariant()}.png";
            var imagePath = Path.Combine(directory, fileName);
            var capture = CaptureView(doc, view, request, displayMode, documentBox, imagePath, warnings);
            captures.Add(capture);
            imagePaths.Add(imagePath);
        }

        var manifestPath = Path.Combine(directory, ManifestFileName);
        var manifest = new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["captureId"] = captureId,
            ["directory"] = directory,
            ["manifestPath"] = manifestPath,
            ["imagePaths"] = imagePaths,
            ["request"] = request.ToManifestDictionary(),
            ["document"] = new Dictionary<string, object?>
            {
                ["name"] = doc.Name ?? "(unsaved)",
                ["path"] = doc.Path ?? "(unsaved)",
                ["units"] = doc.ModelUnitSystem.ToString(),
                ["objects"] = doc.Objects.Count(obj => !obj.IsDeleted),
                ["bbox"] = BoundingBoxToDictionary(documentBox)
            },
            ["captures"] = captures,
            ["warnings"] = warnings
        };

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        var compact = JsonSerializer.Serialize(new
        {
            ok = true,
            captureId,
            directory,
            manifestPath,
            imagePaths,
            width = request.Width,
            height = request.Height,
            views = captures.Select(capture => capture["view"]).ToArray(),
            displayMode = request.DisplayMode,
            fit = request.Fit,
            selectedOnly = request.SelectedOnly,
            documentBoundingBox = BoundingBoxToDictionary(documentBox),
            pixelSummaries = captures.Select(capture => capture["pixels"]).ToArray(),
            note = "PNG files are written to disk for visual inspection; use the manifest for camera, bbox, and pixel-summary metadata.",
            warnings
        }, JsonOptions.Loose);

        return new CaptureResult(true, compact);
    }

    private static Dictionary<string, object?> CaptureView(
        RhinoDoc doc,
        RhinoView view,
        CaptureRequest request,
        DisplayModeDescription? displayMode,
        BoundingBox documentBox,
        string imagePath,
        List<string> warnings)
    {
        using var settings = new ViewCaptureSettings(view, new Size(request.Width, request.Height), 96.0)
        {
            ViewArea = string.Equals(request.Fit, "current", StringComparison.OrdinalIgnoreCase)
                ? ViewCaptureSettings.ViewAreaMapping.View
                : ViewCaptureSettings.ViewAreaMapping.Extents,
            DrawGrid = request.DrawGrid,
            DrawAxis = request.DrawAxes,
            DrawBackground = !request.TransparentBackground,
            DrawSelectedObjectsOnly = request.SelectedOnly,
            DrawLockedObjects = true,
            DrawLights = true,
            DrawWallpaper = false,
            DrawBackgroundBitmap = false
        };

        var viewport = settings.GetViewport();
        if (viewport is not null && displayMode is not null)
            viewport.DisplayMode = displayMode;

        using var bitmap = ViewCapture.CaptureToBitmap(settings);
        if (bitmap is null)
            throw new InvalidOperationException($"Viewport capture returned no bitmap for view '{ViewName(view)}'.");

        var pixelSummary = AnalyzeBitmap(bitmap);
        var camera = CameraToDictionary(viewport ?? view.MainViewport);
        var method = "view-capture-settings";
        bitmap.Save(imagePath, ImageFormat.Png);

        if (!HasVisualVariation(pixelSummary))
        {
            var fallback = TryLiveViewportCapture(doc, view, request, displayMode, documentBox, imagePath);
            if (fallback is not null && HasVisualVariation(fallback.PixelSummary))
            {
                pixelSummary = fallback.PixelSummary;
                camera = fallback.Camera;
                method = fallback.Method;
                warnings.Add($"Capture for view '{ViewName(view)}' used live-view fallback because the settings capture was blank.");
            }
        }

        var fileInfo = new FileInfo(imagePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
            throw new InvalidOperationException($"Viewport capture wrote an empty image for view '{ViewName(view)}'.");

        return new Dictionary<string, object?>
        {
            ["view"] = ViewName(view),
            ["path"] = imagePath,
            ["width"] = bitmap.Width,
            ["height"] = bitmap.Height,
            ["bytes"] = fileInfo.Length,
            ["method"] = method,
            ["displayMode"] = displayMode?.EnglishName ?? view.MainViewport.DisplayMode?.EnglishName ?? "current",
            ["fit"] = request.Fit,
            ["selectedOnly"] = request.SelectedOnly,
            ["camera"] = camera,
            ["pixels"] = pixelSummary,
            ["documentRuntimeSerialNumber"] = doc.RuntimeSerialNumber
        };
    }

    private static LiveCaptureResult? TryLiveViewportCapture(
        RhinoDoc doc,
        RhinoView view,
        CaptureRequest request,
        DisplayModeDescription? displayMode,
        BoundingBox documentBox,
        string imagePath)
    {
        var viewport = view.MainViewport;
        var originalProjection = new ViewportInfo(viewport);
        var originalDisplayMode = viewport.DisplayMode;

        try
        {
            if (displayMode is not null)
                viewport.DisplayMode = displayMode;
            if (!string.Equals(request.Fit, "current", StringComparison.OrdinalIgnoreCase) && documentBox.IsValid)
                viewport.ZoomBoundingBox(documentBox);

            view.Redraw();
            doc.Views.Redraw();

            using var bitmap = view.CaptureToBitmap(
                new Size(request.Width, request.Height),
                request.DrawGrid,
                request.DrawAxes,
                request.DrawAxes);
            if (bitmap is null)
                return null;

            var pixelSummary = AnalyzeBitmap(bitmap);
            var camera = CameraToDictionary(viewport);
            bitmap.Save(imagePath, ImageFormat.Png);
            return new LiveCaptureResult("live-view-capture", pixelSummary, camera);
        }
        catch
        {
            return null;
        }
        finally
        {
            viewport.SetViewProjection(originalProjection, true);
            if (originalDisplayMode is not null)
                viewport.DisplayMode = originalDisplayMode;
            view.Redraw();
        }
    }

    private static List<RhinoView> ResolveViews(RhinoDoc doc, IReadOnlyList<string> requested, List<string> warnings)
    {
        var views = new List<RhinoView>();
        var seen = new HashSet<uint>();

        void Add(RhinoView? view)
        {
            if (view is null)
                return;
            if (seen.Add(view.RuntimeSerialNumber))
                views.Add(view);
        }

        var active = doc.Views.ActiveView;
        if (requested.Count == 0 || requested.Any(IsActiveViewName))
            Add(active);

        var wantsStandard = requested.Any(name =>
            string.Equals(name, "standard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "all", StringComparison.OrdinalIgnoreCase));

        if (wantsStandard)
        {
            foreach (var name in new[] { "Perspective", "Top", "Front", "Right" })
                Add(FindView(doc, name));
        }

        foreach (var name in requested)
        {
            if (IsActiveViewName(name)
                || string.Equals(name, "standard", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
                continue;

            var view = FindView(doc, name);
            if (view is null)
                warnings.Add($"View '{name}' was not found.");
            Add(view);
        }

        if (views.Count == 0)
            Add(active);

        return views;
    }

    private static RhinoView? FindView(RhinoDoc doc, string name)
    {
        var found = doc.Views.Find(name, false);
        if (found is not null)
            return found;

        return doc.Views.GetStandardRhinoViews()
            .FirstOrDefault(view => string.Equals(ViewName(view), name, StringComparison.OrdinalIgnoreCase));
    }

    private static DisplayModeDescription? ResolveDisplayMode(string name, List<string> warnings)
    {
        if (string.Equals(name, "current", StringComparison.OrdinalIgnoreCase))
            return null;

        var displayMode = DisplayModeDescription.FindByName(name);
        if (displayMode is not null)
            return displayMode;

        warnings.Add($"Display mode '{name}' was not found; using the view's current display mode.");
        return null;
    }

    private static Dictionary<string, object?> CameraToDictionary(RhinoViewport viewport)
    {
        var frustum = new Dictionary<string, object?>();
        if (viewport.GetFrustum(out var left, out var right, out var bottom, out var top, out var near, out var far))
        {
            frustum["left"] = left;
            frustum["right"] = right;
            frustum["bottom"] = bottom;
            frustum["top"] = top;
            frustum["near"] = near;
            frustum["far"] = far;
        }

        return new Dictionary<string, object?>
        {
            ["name"] = viewport.Name,
            ["projection"] = viewport.IsParallelProjection ? "parallel" : viewport.IsPerspectiveProjection ? "perspective" : "unknown",
            ["position"] = PointToArray(viewport.CameraLocation),
            ["target"] = PointToArray(viewport.CameraTarget),
            ["direction"] = VectorToArray(viewport.CameraDirection),
            ["up"] = VectorToArray(viewport.CameraUp),
            ["lensLength"] = viewport.Camera35mmLensLength,
            ["frustum"] = frustum
        };
    }

    private static Dictionary<string, object> AnalyzeBitmap(Bitmap bitmap)
    {
        var background = bitmap.GetPixel(0, 0);
        var sampleCount = 0;
        var nonBackgroundCount = 0;
        var min = new[] { 255, 255, 255 };
        var max = new[] { 0, 0, 0 };
        var stepX = Math.Max(1, bitmap.Width / 160);
        var stepY = Math.Max(1, bitmap.Height / 100);

        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var pixel = bitmap.GetPixel(x, y);
                sampleCount++;
                min[0] = Math.Min(min[0], pixel.R);
                min[1] = Math.Min(min[1], pixel.G);
                min[2] = Math.Min(min[2], pixel.B);
                max[0] = Math.Max(max[0], pixel.R);
                max[1] = Math.Max(max[1], pixel.G);
                max[2] = Math.Max(max[2], pixel.B);

                var distance = Math.Abs(pixel.R - background.R)
                    + Math.Abs(pixel.G - background.G)
                    + Math.Abs(pixel.B - background.B)
                    + Math.Abs(pixel.A - background.A);
                if (distance > 24)
                    nonBackgroundCount++;
            }
        }

        var ratio = sampleCount == 0 ? 0 : nonBackgroundCount / (double)sampleCount;
        return new Dictionary<string, object>
        {
            ["sampleCount"] = sampleCount,
            ["nonBackgroundSamples"] = nonBackgroundCount,
            ["nonBackgroundRatio"] = Math.Round(ratio, 6),
            ["backgroundRgba"] = new[] { (int)background.R, (int)background.G, (int)background.B, (int)background.A },
            ["minRgb"] = min,
            ["maxRgb"] = max
        };
    }

    private static bool HasVisualVariation(Dictionary<string, object> pixelSummary) =>
        pixelSummary.TryGetValue("nonBackgroundSamples", out var value)
        && value is int count
        && count > 0;

    private static BoundingBox ComputeDocumentBoundingBox(RhinoDoc doc)
    {
        var bbox = BoundingBox.Empty;
        foreach (var obj in doc.Objects.Where(obj => !obj.IsDeleted))
        {
            var objectBox = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (objectBox.IsValid)
                bbox.Union(objectBox);
        }

        return bbox;
    }

    private static Dictionary<string, object?> BoundingBoxToDictionary(BoundingBox bbox)
    {
        var result = new Dictionary<string, object?>
        {
            ["valid"] = bbox.IsValid
        };
        if (!bbox.IsValid)
            return result;

        result["min"] = PointToArray(bbox.Min);
        result["max"] = PointToArray(bbox.Max);
        result["size"] = VectorToArray(bbox.Diagonal);
        return result;
    }

    private static string ViewName(RhinoView view) =>
        string.IsNullOrWhiteSpace(view.MainViewport.Name) ? "Active" : view.MainViewport.Name;

    private static bool IsActiveViewName(string name) =>
        string.Equals(name, "active", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "current", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value.Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch).ToArray();
        var sanitized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "viewport" : sanitized;
    }

    private static double[] PointToArray(Point3d point) => [point.X, point.Y, point.Z];
    private static double[] VectorToArray(Vector3d vector) => [vector.X, vector.Y, vector.Z];

    private sealed record CaptureResult(bool Ok, string CompactJson)
    {
        public static CaptureResult Failure(string reason, IReadOnlyList<string> warnings)
        {
            var compact = JsonSerializer.Serialize(new
            {
                ok = false,
                reason,
                warnings
            }, JsonOptions.Loose);
            return new CaptureResult(false, compact);
        }
    }

    private sealed record LiveCaptureResult(
        string Method,
        Dictionary<string, object> PixelSummary,
        Dictionary<string, object?> Camera);

    private sealed class CaptureRequest
    {
        public IReadOnlyList<string> ViewNames { get; private init; } = [];
        public string DisplayMode { get; private init; } = "current";
        public string Fit { get; private init; } = "extents";
        public int Width { get; private init; } = DefaultWidth;
        public int Height { get; private init; } = DefaultHeight;
        public bool DrawGrid { get; private init; }
        public bool DrawAxes { get; private init; }
        public bool TransparentBackground { get; private init; }
        public bool SelectedOnly { get; private init; }

        public static CaptureRequest From(ToolCallRequest call)
        {
            var fit = ReadString(call, "fit") ?? "extents";
            if (!string.Equals(fit, "current", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fit, "extents", StringComparison.OrdinalIgnoreCase))
                fit = "extents";

            return new CaptureRequest
            {
                ViewNames = ReadStringList(call, "views").Concat(ReadStringList(call, "view")).ToArray(),
                DisplayMode = NormalizeDisplayMode(ReadString(call, "display_mode") ?? ReadString(call, "displayMode") ?? "current"),
                Fit = fit.ToLowerInvariant(),
                Width = Math.Clamp(ReadInt(call, "width") ?? DefaultWidth, MinSize, MaxSize),
                Height = Math.Clamp(ReadInt(call, "height") ?? DefaultHeight, MinSize, MaxSize),
                DrawGrid = ReadBool(call, "draw_grid") ?? ReadBool(call, "drawGrid") ?? false,
                DrawAxes = ReadBool(call, "draw_axes") ?? ReadBool(call, "drawAxes") ?? false,
                TransparentBackground = ReadBool(call, "transparent_background") ?? ReadBool(call, "transparentBackground") ?? false,
                SelectedOnly = ReadBool(call, "selected_only") ?? ReadBool(call, "selectedOnly") ?? false
            };
        }

        public Dictionary<string, object?> ToManifestDictionary() =>
            new()
            {
                ["views"] = ViewNames,
                ["displayMode"] = DisplayMode,
                ["fit"] = Fit,
                ["width"] = Width,
                ["height"] = Height,
                ["drawGrid"] = DrawGrid,
                ["drawAxes"] = DrawAxes,
                ["transparentBackground"] = TransparentBackground,
                ["selectedOnly"] = SelectedOnly
            };

        private static string NormalizeDisplayMode(string value) =>
            value.Trim().ToLowerInvariant() switch
            {
                "" => "current",
                "wire" => "wireframe",
                "wireframe" => "wireframe",
                "shade" => "shaded",
                "shaded" => "shaded",
                "render" => "rendered",
                "rendered" => "rendered",
                "current" => "current",
                _ => value.Trim()
            };

        private static string? ReadString(ToolCallRequest call, string key) =>
            call.Arguments.TryGetValue(key, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;

        private static int? ReadInt(ToolCallRequest call, string key)
        {
            if (!call.Arguments.TryGetValue(key, out var value) || value is null)
                return null;
            if (value is JsonElement el && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i))
                return i;
            return int.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;
        }

        private static bool? ReadBool(ToolCallRequest call, string key)
        {
            if (!call.Arguments.TryGetValue(key, out var value) || value is null)
                return null;
            if (value is JsonElement el && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
                return el.GetBoolean();
            return bool.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;
        }

        private static IEnumerable<string> ReadStringList(ToolCallRequest call, string key)
        {
            if (!call.Arguments.TryGetValue(key, out var value) || value is null)
                return [];

            if (value is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Array)
                    return el.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))!;
                if (el.ValueKind == JsonValueKind.String)
                    return SplitViews(el.GetString());
            }

            return SplitViews(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        private static IEnumerable<string> SplitViews(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? []
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
