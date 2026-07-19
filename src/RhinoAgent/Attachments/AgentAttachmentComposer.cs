using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Eto.Drawing;
using Eto.Forms;
using DrawingImage = System.Drawing.Image;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

namespace RhinoAgent.Attachments;

internal sealed partial class AgentAttachmentComposer
{
    private readonly AgentAttachmentStore _store;

    public AgentAttachmentComposer(AgentAttachmentStore store)
    {
        _store = store;
    }

    public string? LastCaptureError { get; private set; }

    public bool TryCaptureClipboard(out string insertion)
    {
        insertion = "";
        LastCaptureError = null;

        try
        {
            var clipboard = Clipboard.Instance;
            if (clipboard.ContainsUris)
            {
                var placeholders = new List<string>();
                foreach (var uri in clipboard.Uris ?? [])
                {
                    if (!uri.IsFile
                        || !TryAttachPath(uri.LocalPath, isTemporary: false, out var attachment, out _))
                        continue;

                    placeholders.Add(attachment.Placeholder);
                }

                if (placeholders.Count > 0)
                {
                    insertion = string.Join(" ", placeholders.Distinct(StringComparer.OrdinalIgnoreCase));
                    return true;
                }
            }

            if (clipboard.ContainsImage)
            {
                if (TrySaveClipboardImage(clipboard, out var path, out var error)
                    && TryAttachCapturedFile(path, out insertion, out error))
                    return true;

                LastCaptureError = error;
            }

            var nativeError = "";
            if (OperatingSystem.IsWindows()
                && TrySaveWindowsClipboardImage(out var nativePath, out nativeError)
                && TryAttachCapturedFile(nativePath, out insertion, out nativeError))
                return true;

            LastCaptureError ??= string.IsNullOrWhiteSpace(nativeError)
                ? "The clipboard does not expose file or image data."
                : nativeError;
            return false;
        }
        catch (Exception ex)
        {
            LastCaptureError = ex.Message;
            return false;
        }
    }

    private bool TryAttachCapturedFile(string path, out string insertion, out string error)
    {
        insertion = "";
        if (!TryAttachPath(path, isTemporary: true, out var attachment, out error))
        {
            TryDeleteCapturedFile(path);
            return false;
        }

        insertion = attachment.Placeholder;
        return true;
    }

    public AgentUserMessage Compose(string input)
    {
        var text = ResolveAttachmentPaths(input.Trim());
        var selected = _store.Attachments
            .Where(attachment => text.Contains(attachment.Placeholder, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new AgentUserMessage(text, selected);
    }

    internal bool TryAttachPath(
        string path,
        bool isTemporary,
        out AgentAttachment attachment,
        out string error)
    {
        return _store.TryRegister(path, isTemporary, out attachment, out error);
    }

    private string ResolveAttachmentPaths(string input)
    {
        if (input.Length == 0)
            return input;

        var wholePath = input.Trim().Trim('"');
        if (File.Exists(wholePath)
            && TryAttachPath(wholePath, isTemporary: false, out var wholeAttachment, out _))
            return wholeAttachment.Placeholder;

        var text = QuotedAttachmentPathRegex().Replace(input, match =>
            TryAttachPath(match.Groups["path"].Value, isTemporary: false, out var attachment, out _)
                ? attachment.Placeholder
                : match.Value);

        return UnquotedAttachmentPathRegex().Replace(text, match =>
        {
            var candidate = match.Groups["path"].Value;
            if (TryAttachPath(candidate, isTemporary: false, out var attachment, out _))
                return attachment.Placeholder;

            candidate = candidate.TrimEnd(',', ';', ':', '.', ')', ']', '}');
            return TryAttachPath(candidate, isTemporary: false, out attachment, out _)
                ? attachment.Placeholder + match.Groups["path"].Value[candidate.Length..]
                : match.Value;
        });
    }

    private bool TrySaveClipboardImage(Clipboard clipboard, out string path, out string error)
    {
        path = _store.CreateTemporaryFilePath("clipboard.png");
        error = "";
        var directory = Path.GetDirectoryName(path)!;

        try
        {
            Directory.CreateDirectory(directory);
            using var image = clipboard.Image;
            if (image is null)
            {
                error = "The clipboard reported an image but did not return image data.";
                TryDeleteCapturedFile(path);
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
            TryDeleteCapturedFile(path);
            return false;
        }
    }

    private bool TrySaveWindowsClipboardImage(out string path, out string error)
    {
        path = _store.CreateTemporaryFilePath("clipboard.png");
        error = "";
        var directory = Path.GetDirectoryName(path)!;

        try
        {
            Directory.CreateDirectory(directory);
            if (!OpenClipboard(IntPtr.Zero))
            {
                error = $"Windows could not open the clipboard (error {Marshal.GetLastPInvokeError()}).";
                TryDeleteCapturedFile(path);
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
            TryDeleteCapturedFile(path);
            return false;
        }
        finally
        {
            if (!File.Exists(path))
                TryDeleteCapturedFile(path);
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

    private void TryDeleteCapturedFile(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(_store.SessionRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            File.Delete(path);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: false);
        }
        catch
        {
            // Failed clipboard captures are best-effort cleanup.
        }
    }

    [GeneratedRegex("[\\\"'](?<path>(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedAttachmentPathRegex();

    [GeneratedRegex("(?<path>(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\s\\\"'<>|?*]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UnquotedAttachmentPathRegex();

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
