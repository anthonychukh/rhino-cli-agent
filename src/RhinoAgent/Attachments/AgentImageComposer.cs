using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Eto.Drawing;
using Eto.Forms;
using DrawingImage = System.Drawing.Image;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

namespace RhinoAgent.Attachments;

internal sealed partial class AgentImageComposer : IDisposable
{
    private const int MaximumImages = 8;
    private const long MaximumImageBytes = 20L * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> SupportedMediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp"
        };

    private readonly List<AgentImageAttachment> _attachments = [];
    private readonly HashSet<string> _ownedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public string? LastCaptureError { get; private set; }

    public bool TryCaptureClipboard(out string insertion)
    {
        insertion = "";
        LastCaptureError = null;

        try
        {
            var clipboard = Clipboard.Instance;
            if (clipboard.ContainsImage)
            {
                if (TrySaveClipboardImage(clipboard, out var path, out var error)
                    && TryAttachCapturedImage(path, out insertion, out error))
                    return true;

                LastCaptureError = error;
            }

            var nativeError = "";
            if (OperatingSystem.IsWindows()
                && TrySaveWindowsClipboardImage(out var nativePath, out nativeError)
                && TryAttachCapturedImage(nativePath, out insertion, out nativeError))
                return true;

            if (!clipboard.ContainsUris)
            {
                LastCaptureError ??= string.IsNullOrWhiteSpace(nativeError)
                    ? "The clipboard does not expose supported image data."
                    : nativeError;
                return false;
            }

            var placeholders = new List<string>();
            foreach (var uri in clipboard.Uris ?? [])
            {
                if (!uri.IsFile
                    || !TryAttachPath(uri.LocalPath, isTemporary: false, out var attachment, out _))
                    continue;

                placeholders.Add(attachment.Placeholder);
                if (_attachments.Count >= MaximumImages)
                    break;
            }

            if (placeholders.Count == 0)
                return false;

            insertion = string.Join(" ", placeholders);
            return true;
        }
        catch (Exception ex)
        {
            LastCaptureError = ex.Message;
            return false;
        }
    }

    private bool TryAttachCapturedImage(string path, out string insertion, out string error)
    {
        insertion = "";
        if (!TryAttachPath(path, isTemporary: true, out var attachment, out error))
        {
            TryDeleteFileAndDirectory(path);
            return false;
        }

        insertion = attachment.Placeholder;
        return true;
    }

    public AgentUserMessage Compose(string input)
    {
        var text = ResolveImagePaths(input.Trim());
        var selected = _attachments
            .Where(attachment => text.Contains(attachment.Placeholder, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new AgentUserMessage(text, selected);
    }

    internal bool TryAttachPath(
        string path,
        bool isTemporary,
        out AgentImageAttachment attachment,
        out string error)
    {
        attachment = null!;
        error = "";
        if (_attachments.Count >= MaximumImages)
        {
            error = $"A prompt can contain at most {MaximumImages} images.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        var existing = _attachments.FirstOrDefault(item =>
            string.Equals(item.LocalPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            attachment = existing;
            return true;
        }

        var extension = Path.GetExtension(fullPath);
        if (!SupportedMediaTypes.TryGetValue(extension, out var mediaType))
        {
            error = $"Unsupported image type: {extension}. Use PNG, JPEG, GIF, or WebP.";
            return false;
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            error = $"Image file was not found: {fullPath}";
            return false;
        }

        if (file.Length <= 0 || file.Length > MaximumImageBytes)
        {
            error = $"Image must be between 1 byte and {MaximumImageBytes / (1024 * 1024)} MB: {file.Name}";
            return false;
        }

        attachment = new AgentImageAttachment(
            _attachments.Count + 1,
            fullPath,
            file.Name,
            mediaType,
            file.Length,
            isTemporary);
        _attachments.Add(attachment);
        if (isTemporary)
            _ownedPaths.Add(fullPath);
        return true;
    }

    private string ResolveImagePaths(string input)
    {
        if (input.Length == 0)
            return input;

        var wholePath = input.Trim().Trim('"');
        if (File.Exists(wholePath)
            && TryAttachPath(wholePath, isTemporary: false, out var wholeAttachment, out _))
            return wholeAttachment.Placeholder;

        var text = QuotedImagePathRegex().Replace(input, match =>
            TryAttachPath(match.Groups["path"].Value, isTemporary: false, out var attachment, out _)
                ? attachment.Placeholder
                : match.Value);

        return UnquotedImagePathRegex().Replace(text, match =>
            TryAttachPath(match.Groups["path"].Value, isTemporary: false, out var attachment, out _)
                ? attachment.Placeholder
                : match.Value);
    }

    private bool TrySaveClipboardImage(Clipboard clipboard, out string path, out string error)
    {
        path = CreateClipboardImagePath();
        error = "";
        var directory = Path.GetDirectoryName(path)!;

        try
        {
            Directory.CreateDirectory(directory);
            using var image = clipboard.Image;
            if (image is null)
            {
                error = "The clipboard reported an image but did not return image data.";
                TryDeleteEmptyDirectory(directory);
                return false;
            }

            if (image is Bitmap bitmap)
            {
                bitmap.Save(path, ImageFormat.Png);
            }
            else
            {
                using var converted = new Bitmap(image, image.Width, image.Height, ImageInterpolation.High);
                converted.Save(path, ImageFormat.Png);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not capture the clipboard image: {ex.Message}";
            TryDeleteEmptyDirectory(directory);
            return false;
        }
    }

    private static bool TrySaveWindowsClipboardImage(out string path, out string error)
    {
        path = CreateClipboardImagePath();
        error = "";
        var directory = Path.GetDirectoryName(path)!;

        try
        {
            Directory.CreateDirectory(directory);
            if (!OpenClipboard(IntPtr.Zero))
            {
                error = $"Windows could not open the clipboard (error {Marshal.GetLastPInvokeError()}).";
                TryDeleteEmptyDirectory(directory);
                return false;
            }

            try
            {
                var bitmapHandle = GetClipboardData(ClipboardFormatBitmap);
                if (bitmapHandle != IntPtr.Zero)
                {
                    using var bitmapImage = DrawingImage.FromHbitmap(bitmapHandle);
                    bitmapImage.Save(path, DrawingImageFormat.Png);
                    return true;
                }

                var dibHandle = GetClipboardData(ClipboardFormatDibV5);
                if (dibHandle == IntPtr.Zero)
                    dibHandle = GetClipboardData(ClipboardFormatDib);
                if (dibHandle == IntPtr.Zero)
                {
                    error = "Windows clipboard has no CF_BITMAP, CF_DIB, or CF_DIBV5 image.";
                    return false;
                }

                var size = GlobalSize(dibHandle).ToUInt64();
                if (size < 40 || size > 100L * 1024 * 1024 || size > int.MaxValue)
                {
                    error = $"Windows clipboard DIB size is unsupported: {size} bytes.";
                    return false;
                }

                var pointer = GlobalLock(dibHandle);
                if (pointer == IntPtr.Zero)
                {
                    error = $"Windows could not lock clipboard image data (error {Marshal.GetLastPInvokeError()}).";
                    return false;
                }

                byte[] dib;
                try
                {
                    dib = new byte[(int)size];
                    Marshal.Copy(pointer, dib, 0, dib.Length);
                }
                finally
                {
                    GlobalUnlock(dibHandle);
                }

                var bitmap = BuildBitmapFile(dib);
                using var stream = new MemoryStream(bitmap, writable: false);
                using var dibImage = DrawingImage.FromStream(stream);
                dibImage.Save(path, DrawingImageFormat.Png);
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            error = $"Could not capture the native Windows clipboard image: {ex.Message}";
            TryDeleteFileAndDirectory(path);
            return false;
        }
        finally
        {
            if (!File.Exists(path))
                TryDeleteEmptyDirectory(directory);
        }
    }

    private static byte[] BuildBitmapFile(byte[] dib)
    {
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(0, 4));
        if (headerSize < 40 || headerSize > dib.Length)
            throw new InvalidDataException($"Unsupported DIB header size: {headerSize}.");

        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16, 4));
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32, 4));
        var pixelOffset = checked((int)headerSize);

        if (headerSize == 40 && compression is 3 or 6)
            pixelOffset += compression == 6 ? 16 : 12;

        var colorCount = colorsUsed > 0
            ? colorsUsed
            : bitCount <= 8 ? 1u << bitCount : 0;
        pixelOffset = checked(pixelOffset + (int)(colorCount * 4));
        if (pixelOffset < 0 || pixelOffset >= dib.Length)
            throw new InvalidDataException("Clipboard DIB pixel offset is outside the image data.");

        var file = new byte[checked(dib.Length + 14)];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2, 4), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10, 4), (uint)(14 + pixelOffset));
        Buffer.BlockCopy(dib, 0, file, 14, dib.Length);
        return file;
    }

    private static string CreateClipboardImagePath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "RhinoAgent",
            "attachments",
            Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "clipboard.png");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var path in _ownedPaths)
        {
            try
            {
                File.Delete(path);
                TryDeleteEmptyDirectory(Path.GetDirectoryName(path));
            }
            catch
            {
                // Temporary clipboard images are best-effort cleanup.
            }
        }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch
        {
            // The directory may contain a provider artifact or another capture.
        }
    }

    private static void TryDeleteFileAndDirectory(string path)
    {
        try
        {
            File.Delete(path);
            TryDeleteEmptyDirectory(Path.GetDirectoryName(path));
        }
        catch
        {
            // Failed clipboard captures are best-effort cleanup.
        }
    }

    [GeneratedRegex("[\\\"'](?<path>(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\\"']+?\\.(?:png|jpe?g|gif|webp))[\\\"']", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedImagePathRegex();

    [GeneratedRegex("(?<path>(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\s\\\"'<>|?*]+\\.(?:png|jpe?g|gif|webp))", RegexOptions.IgnoreCase)]
    private static partial Regex UnquotedImagePathRegex();

    private const uint ClipboardFormatBitmap = 2;
    private const uint ClipboardFormatDib = 8;
    private const uint ClipboardFormatDibV5 = 17;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr memory);
}
