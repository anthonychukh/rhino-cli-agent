using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using RhinoAgent.Runtime;

namespace RhinoAgent.Attachments;

public interface IAgentAttachmentInterpreter
{
    string Name { get; }
    string Formats { get; }
    string Behavior { get; }
    bool CanInterpret(AgentAttachment attachment);
    AttachmentInspection Inspect(AgentAttachment attachment);
}

public static class AgentAttachmentInspector
{
    private const int TextPreviewCharacters = 12000;
    private const int BinaryProbeBytes = 4096;
    private const int MaximumArchiveEntries = 500;

    private static readonly IReadOnlyList<IAgentAttachmentInterpreter> Interpreters =
    [
        new DelegateAttachmentInterpreter(
            "rhino-headless-model",
            "3DM, STEP/STP, STL, and other recognized Rhino-importable 3D formats",
            "Read or import into a disposable headless Rhino document and return structured geometry facts.",
            attachment => attachment.Kind == AgentAttachmentKind.Model3D,
            InspectModel),
        new DelegateAttachmentInterpreter(
            "zip-listing",
            "ZIP",
            $"Read-only entry listing capped at {MaximumArchiveEntries} entries; never extracts or executes files.",
            attachment => attachment.Kind == AgentAttachmentKind.Archive && attachment.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase),
            InspectZip),
        new DelegateAttachmentInterpreter(
            "image-metadata",
            "PNG, JPEG, GIF, WebP, and other recognized raster images",
            "Dimensions and pixel format when System.Drawing can decode the image; supported images also use native provider vision.",
            attachment => attachment.Kind == AgentAttachmentKind.Image,
            InspectImage),
        new DelegateAttachmentInterpreter(
            "text-preview",
            "Text, source, config, logs, and files whose leading bytes are text-like",
            $"Encoding-aware preview capped at {TextPreviewCharacters} characters.",
            attachment => attachment.Kind == AgentAttachmentKind.Text || LooksLikeText(attachment.LocalPath),
            InspectText),
        new DelegateAttachmentInterpreter(
            "binary-probe",
            "Any other regular file",
            $"File signature, leading bytes, and bounded printable strings from the first {BinaryProbeBytes} bytes.",
            _ => true,
            InspectBinary)
    ];

    public static string ListInterpreters() => JsonSerializer.Serialize(
        Interpreters.Select(interpreter => new
        {
            name = interpreter.Name,
            formats = interpreter.Formats,
            behavior = interpreter.Behavior
        }),
        JsonOptions.Loose);

    public static AttachmentInspection Inspect(AgentAttachment attachment, string detail = "standard")
    {
        if (!File.Exists(attachment.LocalPath))
            return AttachmentInspection.Failure(attachment, "attachment-metadata", "The attached file no longer exists.");

        try
        {
            var interpreter = Interpreters.First(value => value.CanInterpret(attachment));
            return interpreter.Inspect(attachment);
        }
        catch (Exception ex)
        {
            return AttachmentInspection.Failure(attachment, "attachment-inspector", ex.Message);
        }
    }

    public static AttachmentImportResult ImportInto(RhinoDoc doc, AgentAttachment attachment)
    {
        if (!File.Exists(attachment.LocalPath))
            return new AttachmentImportResult(false, attachment.Placeholder, attachment.FileName, 0, [], "The attached file no longer exists.");
        if (attachment.Kind != AgentAttachmentKind.Model3D)
            return new AttachmentImportResult(false, attachment.Placeholder, attachment.FileName, 0, [], "Only recognized 3D model attachments can be imported into Rhino.");

        RhinoDoc? stagingDocument = null;
        try
        {
            stagingDocument = OpenModelHeadlessly(attachment, out var stagingError);
            if (stagingDocument is null)
            {
                return new AttachmentImportResult(
                    false,
                    attachment.Placeholder,
                    attachment.FileName,
                    0,
                    [],
                    stagingError);
            }

            var added = TransferStagedObjects(stagingDocument, doc);
            doc.Views.Redraw();

            return new AttachmentImportResult(
                true,
                attachment.Placeholder,
                attachment.FileName,
                added.Length,
                added,
                "Imported into the active Rhino document.");
        }
        catch (Exception ex)
        {
            return new AttachmentImportResult(false, attachment.Placeholder, attachment.FileName, 0, [], ex.Message);
        }
        finally
        {
            stagingDocument?.Dispose();
        }
    }

    private static RhinoDoc? OpenModelHeadlessly(AgentAttachment attachment, out string error)
    {
        error = "";
        if (attachment.Extension.Equals(".3dm", StringComparison.OrdinalIgnoreCase))
        {
            var opened = RhinoDoc.OpenHeadless(attachment.LocalPath);
            if (opened is null)
                error = "Rhino could not open the 3DM attachment headlessly.";
            return opened;
        }

        var document = RhinoDoc.CreateHeadless(null);
        if (document is null)
        {
            error = "Rhino could not create a headless staging document for the attachment.";
            return null;
        }

        try
        {
            if (ReadModelDirectly(attachment, document))
                return document;

            error = $"No installed Rhino importer accepted {attachment.Extension}.";
            document.Dispose();
            return null;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static bool ReadModelDirectly(AgentAttachment attachment, RhinoDoc document)
    {
        var path = attachment.LocalPath;
        return attachment.Extension.ToLowerInvariant() switch
        {
            ".3ds" => File3ds.Read(path, document, new File3dsReadOptions()),
            ".dwg" or ".dxf" => FileDwg.Read(path, document, new FileDwgReadOptions()),
            ".fbx" => FileFbx.Read(path, document, new FileFbxReadOptions()),
            ".obj" => ReadObjDirectly(path, document),
            ".ply" => FilePly.Read(path, document, new FilePlyReadOptions()),
            ".skp" => FileSkp.Read(path, document, new FileSkpReadOptions()),
            ".stl" => FileStl.Read(path, document, new FileStlReadOptions()),
            ".stp" or ".step" => FileStp.Read(path, document, new FileStpReadOptions()),
            _ => document.Import(path)
        };
    }

    private static bool ReadObjDirectly(string path, RhinoDoc document)
    {
        using var readOptions = new FileReadOptions
        {
            ImportMode = true,
            BatchMode = true,
            UseScaleGeometry = true,
            ScaleGeometry = true
        };
        return FileObj.Read(path, document, new FileObjReadOptions(readOptions));
    }

    private static Guid[] TransferStagedObjects(RhinoDoc source, RhinoDoc destination)
    {
        var layerMap = new Dictionary<int, int>();
        var materialMap = new Dictionary<int, int>();
        var linetypeMap = new Dictionary<int, int>();
        var groupMap = new Dictionary<int, int>();
        var added = new List<Guid>();
        var undoRecord = destination.BeginUndoRecord("Import Agent attachment");

        try
        {
            var sourceObjects = source.Objects
                .Where(value => !value.IsDeleted && !value.IsInstanceDefinitionGeometry)
                .ToArray();

            foreach (var sourceObject in sourceObjects)
            {
                if (sourceObject is InstanceObject instance)
                {
                    instance.Explode(
                        explodeNestedInstances: true,
                        out var pieces,
                        out var pieceAttributes,
                        out var pieceTransforms);

                    for (var i = 0; i < pieces.Length; i++)
                    {
                        AddTransferredObject(
                            pieces[i],
                            pieceAttributes[i],
                            pieceTransforms[i],
                            source,
                            destination,
                            layerMap,
                            materialMap,
                            linetypeMap,
                            groupMap,
                            added);
                    }
                }
                else
                {
                    AddTransferredObject(
                        sourceObject,
                        sourceObject.Attributes,
                        Transform.Identity,
                        source,
                        destination,
                        layerMap,
                        materialMap,
                        linetypeMap,
                        groupMap,
                        added);
                }
            }

            return added.ToArray();
        }
        catch
        {
            foreach (var id in added)
                destination.Objects.Delete(id, quiet: true);
            throw;
        }
        finally
        {
            if (undoRecord != 0)
                destination.EndUndoRecord(undoRecord);
        }
    }

    private static void AddTransferredObject(
        RhinoObject sourceObject,
        ObjectAttributes sourceAttributes,
        Transform transform,
        RhinoDoc source,
        RhinoDoc destination,
        Dictionary<int, int> layerMap,
        Dictionary<int, int> materialMap,
        Dictionary<int, int> linetypeMap,
        Dictionary<int, int> groupMap,
        List<Guid> added)
    {
        var geometry = sourceObject.Geometry?.Duplicate()
            ?? throw new InvalidOperationException("An imported object did not contain transferable geometry.");
        if (!geometry.Transform(transform))
            throw new InvalidOperationException("Rhino could not apply an imported instance transform.");

        var attributes = sourceAttributes.Duplicate();
        attributes.LayerIndex = MapLayer(source, destination, sourceAttributes.LayerIndex, layerMap, materialMap, linetypeMap);
        attributes.MaterialIndex = MapMaterial(source, destination, sourceAttributes.MaterialIndex, materialMap);
        attributes.LinetypeIndex = MapLinetype(source, destination, sourceAttributes.LinetypeIndex, linetypeMap);

        var sourceGroups = sourceAttributes.GetGroupList() ?? [];
        attributes.RemoveFromAllGroups();
        foreach (var sourceGroup in sourceGroups)
            attributes.AddToGroup(MapGroup(source, destination, sourceGroup, groupMap));

        var id = destination.Objects.Add(geometry, attributes);
        if (id == Guid.Empty)
            throw new InvalidOperationException($"Rhino could not transfer imported {sourceObject.ObjectType} geometry.");
        added.Add(id);
    }

    private static int MapLayer(
        RhinoDoc source,
        RhinoDoc destination,
        int sourceIndex,
        Dictionary<int, int> layerMap,
        Dictionary<int, int> materialMap,
        Dictionary<int, int> linetypeMap)
    {
        if (layerMap.TryGetValue(sourceIndex, out var mapped))
            return mapped;

        var sourceLayer = source.Layers.FindIndex(sourceIndex);
        if (sourceLayer is null)
            return destination.Layers.CurrentLayerIndex;

        var existing = destination.Layers.FindByFullPath(sourceLayer.FullPath, -1);
        if (existing >= 0)
            return layerMap[sourceIndex] = existing;

        var parentId = Guid.Empty;
        if (sourceLayer.ParentLayerId != Guid.Empty)
        {
            var sourceParentIndex = source.Layers.Find(sourceLayer.ParentLayerId, true, -1);
            if (sourceParentIndex >= 0)
            {
                var destinationParentIndex = MapLayer(
                    source,
                    destination,
                    sourceParentIndex,
                    layerMap,
                    materialMap,
                    linetypeMap);
                parentId = destination.Layers.FindIndex(destinationParentIndex)?.Id ?? Guid.Empty;
            }
        }

        var newLayer = new Layer();
        newLayer.CopyAttributesFrom(sourceLayer);
        newLayer.Name = sourceLayer.Name;
        newLayer.Id = Guid.NewGuid();
        newLayer.ParentLayerId = parentId;
        newLayer.RenderMaterialIndex = MapMaterial(source, destination, sourceLayer.RenderMaterialIndex, materialMap);
        newLayer.LinetypeIndex = MapLinetype(source, destination, sourceLayer.LinetypeIndex, linetypeMap);

        var newIndex = destination.Layers.Add(newLayer);
        if (newIndex < 0)
            throw new InvalidOperationException($"Rhino could not transfer imported layer '{sourceLayer.FullPath}'.");
        return layerMap[sourceIndex] = newIndex;
    }

    private static int MapMaterial(
        RhinoDoc source,
        RhinoDoc destination,
        int sourceIndex,
        Dictionary<int, int> materialMap)
    {
        if (sourceIndex < 0)
            return sourceIndex;
        if (materialMap.TryGetValue(sourceIndex, out var mapped))
            return mapped;

        var sourceMaterial = source.Materials.FindIndex(sourceIndex);
        if (sourceMaterial is null)
            return -1;

        var existing = destination.Materials.Find(sourceMaterial, true);
        var destinationIndex = existing >= 0
            ? existing
            : destination.Materials.Add(sourceMaterial);
        if (destinationIndex < 0)
            throw new InvalidOperationException($"Rhino could not transfer imported material '{sourceMaterial.Name}'.");
        return materialMap[sourceIndex] = destinationIndex;
    }

    private static int MapLinetype(
        RhinoDoc source,
        RhinoDoc destination,
        int sourceIndex,
        Dictionary<int, int> linetypeMap)
    {
        if (sourceIndex < 0)
            return sourceIndex;
        if (linetypeMap.TryGetValue(sourceIndex, out var mapped))
            return mapped;

        var sourceLinetype = source.Linetypes.FindIndex(sourceIndex);
        if (sourceLinetype is null)
            return -1;

        var existing = destination.Linetypes.Find(sourceLinetype.Name);
        var destinationIndex = existing >= 0
            ? existing
            : destination.Linetypes.Add(sourceLinetype);
        if (destinationIndex < 0)
            throw new InvalidOperationException($"Rhino could not transfer imported linetype '{sourceLinetype.Name}'.");
        return linetypeMap[sourceIndex] = destinationIndex;
    }

    private static int MapGroup(
        RhinoDoc source,
        RhinoDoc destination,
        int sourceIndex,
        Dictionary<int, int> groupMap)
    {
        if (groupMap.TryGetValue(sourceIndex, out var mapped))
            return mapped;

        var sourceGroup = source.Groups.FindIndex(sourceIndex);
        var existing = string.IsNullOrWhiteSpace(sourceGroup?.Name)
            ? -1
            : destination.Groups.Find(sourceGroup.Name);
        var destinationIndex = existing >= 0
            ? existing
            : string.IsNullOrWhiteSpace(sourceGroup?.Name)
                ? destination.Groups.Add()
                : destination.Groups.Add(sourceGroup.Name);
        if (destinationIndex < 0)
            throw new InvalidOperationException("Rhino could not transfer an imported object group.");
        return groupMap[sourceIndex] = destinationIndex;
    }

    private static AttachmentInspection InspectModel(AgentAttachment attachment)
    {
        RhinoDoc? headless = null;
        try
        {
            headless = OpenModelHeadlessly(attachment, out var error);
            if (headless is null)
                return AttachmentInspection.Failure(attachment, "rhino-headless-model", error);

            var objects = headless.Objects.Where(value => !value.IsDeleted).ToArray();
            var boundingBox = BoundingBox.Empty;
            foreach (var obj in objects)
            {
                var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
                if (box.IsValid)
                    boundingBox.Union(box);
            }

            var meshes = objects.Select(value => value.Geometry).OfType<Mesh>().ToArray();
            var breps = objects.Select(value => value.Geometry).OfType<Brep>().ToArray();
            var extrusions = objects.Select(value => value.Geometry).OfType<Extrusion>().ToArray();
            var curves = objects.Select(value => value.Geometry).OfType<Curve>().ToArray();
            var layers = objects
                .Select(value => headless.Layers[value.Attributes.LayerIndex]?.FullPath ?? "")
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToArray();

            var data = new Dictionary<string, object?>
            {
                ["format"] = FormatName(attachment),
                ["units"] = headless.ModelUnitSystem.ToString(),
                ["absoluteTolerance"] = headless.ModelAbsoluteTolerance,
                ["layerCount"] = headless.Layers.Count,
                ["layers"] = layers,
                ["objectCount"] = objects.Length,
                ["objectsByType"] = objects
                    .GroupBy(value => value.Geometry?.GetType().Name ?? value.ObjectType.ToString())
                    .OrderByDescending(value => value.Count())
                    .ToDictionary(value => value.Key, value => value.Count()),
                ["boundingBox"] = boundingBox.IsValid ? boundingBox.ToString() : null,
                ["boundingBoxDiagonal"] = boundingBox.IsValid ? boundingBox.Diagonal.Length : null,
                ["meshCount"] = meshes.Length,
                ["meshVertices"] = meshes.Sum(value => value.Vertices.Count),
                ["meshFaces"] = meshes.Sum(value => value.Faces.Count),
                ["closedMeshes"] = meshes.Count(value => value.IsClosed),
                ["brepCount"] = breps.Length,
                ["brepFaces"] = breps.Sum(value => value.Faces.Count),
                ["brepEdges"] = breps.Sum(value => value.Edges.Count),
                ["solidBreps"] = breps.Count(value => value.IsSolid),
                ["extrusionCount"] = extrusions.Length,
                ["solidExtrusions"] = extrusions.Count(value => value.IsSolid),
                ["curveCount"] = curves.Length,
                ["closedCurves"] = curves.Count(value => value.IsClosed),
                ["namedObjects"] = objects
                    .Where(value => !string.IsNullOrWhiteSpace(value.Name))
                    .Select(value => new { id = value.Id, name = value.Name, type = value.Geometry?.GetType().Name })
                    .Take(200)
                    .ToArray()
            };
            return AttachmentInspection.Success(attachment, "rhino-headless-model", data);
        }
        finally
        {
            headless?.Dispose();
        }
    }

    private static AttachmentInspection InspectText(AgentAttachment attachment)
    {
        using var stream = new FileStream(attachment.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[TextPreviewCharacters + 1];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        var truncated = count > TextPreviewCharacters || !reader.EndOfStream;
        var previewLength = Math.Min(count, TextPreviewCharacters);
        var data = new Dictionary<string, object?>
        {
            ["format"] = FormatName(attachment),
            ["preview"] = new string(buffer, 0, previewLength),
            ["truncated"] = truncated,
            ["previewCharacters"] = previewLength
        };
        return AttachmentInspection.Success(attachment, "text-preview", data);
    }

    private static AttachmentInspection InspectZip(AgentAttachment attachment)
    {
        using var archive = ZipFile.OpenRead(attachment.LocalPath);
        var entries = archive.Entries.Take(MaximumArchiveEntries).Select(entry => new
        {
            name = entry.FullName,
            size = entry.Length,
            compressedSize = entry.CompressedLength,
            modified = entry.LastWriteTime
        }).ToArray();
        var data = new Dictionary<string, object?>
        {
            ["format"] = "ZIP",
            ["entryCount"] = archive.Entries.Count,
            ["entries"] = entries,
            ["truncated"] = archive.Entries.Count > entries.Length,
            ["extracted"] = false
        };
        return AttachmentInspection.Success(attachment, "zip-listing", data);
    }

    private static AttachmentInspection InspectImage(AgentAttachment attachment)
    {
        var data = new Dictionary<string, object?>
        {
            ["format"] = FormatName(attachment),
            ["nativeProviderImage"] = attachment.CanSendToProviderAsImage
        };

        try
        {
            using var image = System.Drawing.Image.FromFile(attachment.LocalPath);
            data["width"] = image.Width;
            data["height"] = image.Height;
            data["pixelFormat"] = image.PixelFormat.ToString();
        }
        catch (Exception ex)
        {
            data["decoderWarning"] = ex.Message;
        }

        return AttachmentInspection.Success(attachment, "image-metadata", data);
    }

    private static AttachmentInspection InspectBinary(AgentAttachment attachment)
    {
        var bytes = ReadLeadingBytes(attachment.LocalPath, BinaryProbeBytes);
        var printable = ExtractPrintableStrings(bytes, minimumLength: 4).Take(40).ToArray();
        var data = new Dictionary<string, object?>
        {
            ["format"] = DetectSignature(bytes, attachment),
            ["headerHex"] = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 64))),
            ["printableStrings"] = printable,
            ["probedBytes"] = bytes.Length,
            ["interpretation"] = "No specialized interpreter was available. This is a bounded binary probe, not a complete content interpretation."
        };
        return AttachmentInspection.Success(attachment, "binary-probe", data);
    }

    private static bool LooksLikeText(string path)
    {
        var bytes = ReadLeadingBytes(path, BinaryProbeBytes);
        if (bytes.Length == 0)
            return true;
        if (bytes.Any(value => value == 0))
            return false;

        var printable = bytes.Count(value => value is 9 or 10 or 13 || value >= 32);
        return printable / (double)bytes.Length >= 0.85;
    }

    private static byte[] ReadLeadingBytes(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(maximum, stream.Length > int.MaxValue ? maximum : (int)stream.Length)];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                break;
            total += read;
        }
        return total == buffer.Length ? buffer : buffer[..total];
    }

    private static IEnumerable<string> ExtractPrintableStrings(byte[] bytes, int minimumLength)
    {
        var builder = new StringBuilder();
        foreach (var value in bytes)
        {
            if (value is >= 32 and <= 126)
            {
                builder.Append((char)value);
                continue;
            }

            if (builder.Length >= minimumLength)
                yield return builder.ToString();
            builder.Clear();
        }
        if (builder.Length >= minimumLength)
            yield return builder.ToString();
    }

    private static string DetectSignature(byte[] bytes, AgentAttachment attachment)
    {
        if (bytes.AsSpan().StartsWith("%PDF"u8))
            return "PDF";
        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B)
            return "ZIP-compatible container";
        if (bytes.AsSpan().StartsWith("glTF"u8))
            return "GLB";
        if (bytes.AsSpan().StartsWith("solid "u8))
            return "possible ASCII STL";
        return FormatName(attachment);
    }

    private static string FormatName(AgentAttachment attachment) => attachment.Extension.ToLowerInvariant() switch
    {
        ".3dm" => "Rhino 3DM",
        ".stp" or ".step" => "STEP",
        ".stl" => "STL",
        ".obj" => "Wavefront OBJ",
        ".fbx" => "FBX",
        ".iges" or ".igs" => "IGES",
        ".png" => "PNG",
        ".jpg" or ".jpeg" => "JPEG",
        ".gif" => "GIF",
        ".webp" => "WebP",
        ".pdf" => "PDF",
        ".zip" => "ZIP",
        "" => "extensionless",
        _ => attachment.Extension.TrimStart('.').ToUpperInvariant()
    };

    private sealed record DelegateAttachmentInterpreter(
        string Name,
        string Formats,
        string Behavior,
        Func<AgentAttachment, bool> Predicate,
        Func<AgentAttachment, AttachmentInspection> Inspector) : IAgentAttachmentInterpreter
    {
        public bool CanInterpret(AgentAttachment attachment) => Predicate(attachment);
        public AttachmentInspection Inspect(AgentAttachment attachment) => Inspector(attachment);
    }
}

public sealed record AttachmentInspection(
    bool Ok,
    string Attachment,
    string Id,
    string FileName,
    string Path,
    string Extension,
    string MediaType,
    long SizeBytes,
    AgentAttachmentKind Kind,
    string Interpreter,
    IReadOnlyDictionary<string, object?> Data,
    string? Error)
{
    public static AttachmentInspection Success(AgentAttachment attachment, string interpreter, IReadOnlyDictionary<string, object?> data) =>
        new(true, attachment.Placeholder, attachment.Id, attachment.FileName, attachment.LocalPath, attachment.Extension,
            attachment.MediaType, attachment.SizeBytes, attachment.Kind, interpreter, data, null);

    public static AttachmentInspection Failure(AgentAttachment attachment, string interpreter, string error) =>
        new(false, attachment.Placeholder, attachment.Id, attachment.FileName, attachment.LocalPath, attachment.Extension,
            attachment.MediaType, attachment.SizeBytes, attachment.Kind, interpreter,
            new Dictionary<string, object?>(), error);
}

public sealed record AttachmentImportResult(
    bool Ok,
    string Attachment,
    string FileName,
    int AddedObjectCount,
    IReadOnlyList<Guid> AddedObjectIds,
    string Message);
