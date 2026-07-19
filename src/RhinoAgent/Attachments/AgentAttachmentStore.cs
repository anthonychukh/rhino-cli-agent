namespace RhinoAgent.Attachments;

public sealed class AgentAttachmentStore : IDisposable
{
    public const long LargeFileWarningBytes = 250L * 1024 * 1024;
    private const long MaximumProviderImageBytes = 20L * 1024 * 1024;
    private static readonly TimeSpan StaleArtifactAge = TimeSpan.FromHours(24);

    private static readonly IReadOnlyDictionary<string, string> ImageMediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp"
        };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml",
        ".toml", ".ini", ".cfg", ".config", ".log", ".cs", ".py", ".js", ".ts", ".tsx",
        ".jsx", ".html", ".css", ".scss", ".sql", ".ps1", ".cmd", ".bat", ".sh", ".svg"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".rtf", ".xls", ".xlsx", ".ppt", ".pptx"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz"
    };

    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3dm", ".stp", ".step", ".stl", ".obj", ".fbx", ".iges", ".igs", ".dwg", ".dxf",
        ".skp", ".gltf", ".glb", ".ply", ".3ds", ".dae", ".sat", ".x_t", ".x_b", ".ifc"
    };

    private readonly Dictionary<string, AgentAttachment> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentAttachment> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _extensionCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;

    public AgentAttachmentStore(string? rootDirectory = null)
    {
        AttachmentRoot = Path.GetFullPath(rootDirectory ?? Path.Combine(
            Path.GetTempPath(),
            "RhinoAgent",
            "attachments"));
        SessionRoot = Path.Combine(AttachmentRoot, Guid.NewGuid().ToString("N"));
        SweepStaleArtifacts(AttachmentRoot, SessionRoot, DateTimeOffset.UtcNow);
    }

    public string AttachmentRoot { get; }
    public string SessionRoot { get; }

    public static string? GetSizeWarning(AgentAttachment attachment) =>
        attachment.SizeBytes >= LargeFileWarningBytes
            ? "Large attachment: interpretation may take significant time and memory."
            : null;

    public IReadOnlyList<AgentAttachment> Attachments
    {
        get
        {
            lock (_gate)
                return _byId.Values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public string CreateTemporaryFilePath(string fileName)
    {
        ThrowIfDisposed();
        var safeName = string.Concat((fileName ?? "attachment.bin")
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "attachment.bin";

        var directory = Path.Combine(SessionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, safeName);
    }

    public bool TryRegister(
        string path,
        bool isTemporary,
        out AgentAttachment attachment,
        out string error)
    {
        attachment = null!;
        error = "";
        ThrowIfDisposed();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim().Trim('"', '\''));
        }
        catch (Exception ex)
        {
            error = $"Invalid attachment path: {ex.Message}";
            return false;
        }

        if (IsDevicePath(fullPath))
        {
            error = $"Device paths cannot be attached: {fullPath}";
            return false;
        }

        FileInfo file;
        try
        {
            file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                error = $"Attachment file was not found: {fullPath}";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"Attachment could not be read: {ex.Message}";
            return false;
        }

        if (isTemporary && !IsWithinRoot(fullPath, SessionRoot))
        {
            error = "RhinoAgent will only own temporary files inside its session attachment directory.";
            return false;
        }

        lock (_gate)
        {
            if (_byPath.TryGetValue(fullPath, out var existing))
            {
                attachment = existing;
                return true;
            }

            var extension = NormalizeExtension(file.Extension);
            var counterKey = extension.Length == 0 ? "file" : extension;
            var number = _extensionCounters.TryGetValue(counterKey, out var current) ? current + 1 : 1;
            _extensionCounters[counterKey] = number;

            var kind = Classify(extension);
            var mediaType = ResolveMediaType(extension, kind);
            var placeholder = extension.Length == 0
                ? $"[file {number}]"
                : $"[{extension} {number}]";
            var canSendAsImage = kind == AgentAttachmentKind.Image
                && file.Length > 0
                && file.Length <= MaximumProviderImageBytes
                && ImageMediaTypes.ContainsKey(extension);

            attachment = new AgentAttachment(
                $"att-{Guid.NewGuid():N}",
                number,
                placeholder,
                fullPath,
                file.Name,
                extension,
                mediaType,
                file.Length,
                kind,
                isTemporary,
                canSendAsImage);
            _byId[attachment.Id] = attachment;
            _byPath[fullPath] = attachment;
            return true;
        }
    }

    public bool TryResolve(string reference, out AgentAttachment attachment)
    {
        attachment = null!;
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        lock (_gate)
        {
            if (_byId.TryGetValue(reference.Trim(), out attachment!))
                return File.Exists(attachment.LocalPath);

            attachment = _byId.Values.FirstOrDefault(value =>
                value.Placeholder.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase))!;
            return attachment is not null && File.Exists(attachment.LocalPath);
        }
    }

    public void ReleaseTemporary(IEnumerable<AgentAttachment> attachments)
    {
        foreach (var attachment in attachments.Where(value => value.IsTemporary).DistinctBy(value => value.Id))
            ReleaseTemporary(attachment);
    }

    public void Clear()
    {
        AgentAttachment[] temporary;
        lock (_gate)
        {
            temporary = _byId.Values.Where(value => value.IsTemporary).ToArray();
            _byId.Clear();
            _byPath.Clear();
            _extensionCounters.Clear();
        }

        foreach (var attachment in temporary)
            DeleteOwnedFile(attachment.LocalPath);
        DeleteOwnedDirectory(SessionRoot);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Clear();
        _disposed = true;
    }

    internal static void SweepStaleArtifacts(string root, string activeSessionRoot, DateTimeOffset now)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var directory in Directory.GetDirectories(root))
        {
            try
            {
                var fullPath = Path.GetFullPath(directory);
                if (fullPath.Equals(Path.GetFullPath(activeSessionRoot), StringComparison.OrdinalIgnoreCase)
                    || !IsWithinRoot(fullPath, root))
                    continue;

                var lastWrite = Directory.GetLastWriteTimeUtc(fullPath);
                if (now.UtcDateTime - lastWrite < StaleArtifactAge)
                    continue;

                Directory.Delete(fullPath, recursive: true);
            }
            catch
            {
                // Crash cleanup is best effort and never blocks a new Agent session.
            }
        }
    }

    private void ReleaseTemporary(AgentAttachment attachment)
    {
        lock (_gate)
        {
            if (!_byId.Remove(attachment.Id))
                return;
            _byPath.Remove(attachment.LocalPath);
        }

        DeleteOwnedFile(attachment.LocalPath);
    }

    private void DeleteOwnedFile(string path)
    {
        if (!IsWithinRoot(path, SessionRoot))
            return;

        try
        {
            File.Delete(path);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && IsWithinRoot(directory, SessionRoot))
                Directory.Delete(directory, recursive: false);
        }
        catch
        {
            // The session-level Dispose path retries directory cleanup.
        }
    }

    private void DeleteOwnedDirectory(string path)
    {
        if (!IsWithinRoot(path, AttachmentRoot))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A startup sweep handles crash/lock leftovers after 24 hours.
        }
    }

    private static AgentAttachmentKind Classify(string extension)
    {
        if (ImageMediaTypes.ContainsKey(extension))
            return AgentAttachmentKind.Image;
        if (TextExtensions.Contains(extension))
            return AgentAttachmentKind.Text;
        if (DocumentExtensions.Contains(extension))
            return AgentAttachmentKind.Document;
        if (ArchiveExtensions.Contains(extension))
            return AgentAttachmentKind.Archive;
        if (ModelExtensions.Contains(extension))
            return AgentAttachmentKind.Model3D;
        return AgentAttachmentKind.Binary;
    }

    private static string ResolveMediaType(string extension, AgentAttachmentKind kind)
    {
        if (ImageMediaTypes.TryGetValue(extension, out var imageType))
            return imageType;

        return (kind, extension) switch
        {
            (AgentAttachmentKind.Text, ".json") => "application/json",
            (AgentAttachmentKind.Text, ".xml") => "application/xml",
            (AgentAttachmentKind.Text, ".csv") => "text/csv",
            (AgentAttachmentKind.Text, _) => "text/plain",
            (AgentAttachmentKind.Document, ".pdf") => "application/pdf",
            (AgentAttachmentKind.Archive, ".zip") => "application/zip",
            (AgentAttachmentKind.Model3D, ".3dm") => "model/vnd.rhino",
            (AgentAttachmentKind.Model3D, ".stl") => "model/stl",
            (AgentAttachmentKind.Model3D, ".obj") => "model/obj",
            _ => "application/octet-stream"
        };
    }

    private static string NormalizeExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension) ? "" : extension.ToLowerInvariant();

    private static bool IsDevicePath(string path) =>
        path.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("\\\\?\\GLOBALROOT", StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
