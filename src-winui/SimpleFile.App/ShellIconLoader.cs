using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleFile.Core;

namespace SimpleFile.App;

internal static class ShellIconLoader
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint ShgfiSysIconIndex = 0x000004000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const int IldTransparent = 0x00000001;
    private const int ShilLarge = 0;
    private const int ShilSmall = 1;
    private const int ShilExtraLarge = 2;
    private const int ShilJumbo = 4;

    private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapImage? ForEntry(string path, bool isDirectory, int iconSize = 16)
    {
        var requestedSize = NormalizeIconSize(iconSize);
        var fileSpecific = !isDirectory && ShouldUseActualFileIcon(path);
        var key = isDirectory
            ? "dir"
            : fileSpecific
                ? $"path:{path}"
                : System.IO.Path.GetExtension(path) is { Length: > 0 } extension
                    ? extension
                    : "file";
        var cacheKey = $"entry:{requestedSize}:{key}";

        try
        {
            return Cache.GetOrAdd(
                cacheKey,
                k =>
                {
                    // Use SHGFI_USEFILEATTRIBUTES (useAttributes: true) for the initial call.
                    // This never touches the filesystem and returns instantly.
                    var icon = LoadFromSystemImageList(path, isDirectory, requestedSize, useAttributes: true)
                        ?? Load(path, isDirectory, requestedSize, useAttributes: true)
                        ?? CreateFallbackIcon(path, isDirectory, requestedSize);

                    // For file-specific icons, queue async extraction in the background.
                    // The cache entry will be updated when the real icon arrives.
                    if (fileSpecific)
                    {
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                var realIcon = LoadFromSystemImageList(path, isDirectory, requestedSize, useAttributes: false)
                                    ?? Load(path, isDirectory, requestedSize, useAttributes: false);
                                if (realIcon is not null)
                                {
                                    Cache[cacheKey] = realIcon;
                                }
                            }
                            catch
                            {
                                // Best-effort; keep the extension-based icon.
                            }
                        });
                    }

                    return icon;
                });
        }
        catch
        {
            return TryCreateFallbackIcon(path, isDirectory, requestedSize);
        }
    }

    public static BitmapImage? ForPath(string path, int iconSize = 16, bool isDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var requestedSize = NormalizeIconSize(iconSize);
        var treatAsDirectory = isDirectory || IsLikelyDirectoryPath(path);
        var cacheKey = $"path:{requestedSize}:{(treatAsDirectory ? "dir" : "file")}:{path}";
        try
        {
            return Cache.GetOrAdd(cacheKey, _ =>
            {
                // Avoid Directory.Exists() which triggers network I/O. Instead, infer
                // from path shape unless the caller already knows this is a directory.
                return LoadFromSystemImageList(path, treatAsDirectory, requestedSize, useAttributes: true)
                    ?? Load(path, treatAsDirectory, requestedSize, useAttributes: true)
                    ?? CreateFallbackIcon(path, treatAsDirectory, requestedSize);
            });
        }
        catch
        {
            return TryCreateFallbackIcon(path, treatAsDirectory, requestedSize);
        }
    }

    private static int NormalizeIconSize(int iconSize)
        => UiSettings.NormalizeIconSize(iconSize);

    private static bool ShouldUseActualFileIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".exe" or ".ico" or ".cur" or ".lnk" or ".url" => true,
            _ => false,
        };
    }

    private static bool IsLikelyDirectoryPath(string path) =>
        path.EndsWith('\\') || path.EndsWith('/');

    private static bool ShouldRejectGenericIconIndex(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return true;
        }

        return System.IO.Path.GetExtension(path) is { Length: > 0 };
    }

    private static BitmapImage CreateFallbackIcon(string path, bool isDirectory, int iconSize)
    {
        var size = Math.Clamp(iconSize, UiSettings.IconSizeMin, UiSettings.IconSizeMax);
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        if (isDirectory)
        {
            DrawFolderFallback(graphics, size);
        }
        else
        {
            DrawFileFallback(graphics, size, ExtensionLabel(path));
        }

        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Png);
        memory.Position = 0;
        var image = new BitmapImage();
        image.SetSource(memory.AsRandomAccessStream());
        return image;
    }

    private static BitmapImage? TryCreateFallbackIcon(string path, bool isDirectory, int iconSize)
    {
        try
        {
            return CreateFallbackIcon(path, isDirectory, iconSize);
        }
        catch
        {
            return null;
        }
    }

    private static void DrawFolderFallback(Graphics graphics, int size)
    {
        var scale = size / 96f;
        using var shadow = new SolidBrush(Color.FromArgb(28, 0, 0, 0));
        using var back = new SolidBrush(Color.FromArgb(255, 218, 177, 76));
        using var front = new SolidBrush(Color.FromArgb(255, 244, 194, 82));
        using var stroke = new Pen(Color.FromArgb(255, 166, 126, 44), Math.Max(1f, scale));
        var radius = 7f * scale;
        FillRoundedRectangle(graphics, shadow, 12 * scale, 31 * scale, 72 * scale, 46 * scale, radius);
        FillRoundedRectangle(graphics, back, 10 * scale, 25 * scale, 76 * scale, 47 * scale, radius);
        FillRoundedRectangle(graphics, front, 10 * scale, 34 * scale, 76 * scale, 43 * scale, radius);
        graphics.DrawPath(stroke, RoundedRectanglePath(10 * scale, 34 * scale, 76 * scale, 43 * scale, radius));
        using var tabPath = new GraphicsPath();
        tabPath.AddLine(13 * scale, 25 * scale, 36 * scale, 25 * scale);
        tabPath.AddLine(43 * scale, 34 * scale, 13 * scale, 34 * scale);
        tabPath.CloseFigure();
        graphics.FillPath(back, tabPath);
    }

    private static void DrawFileFallback(Graphics graphics, int size, string label)
    {
        var scale = size / 96f;
        using var page = new SolidBrush(Color.FromArgb(255, 245, 247, 251));
        using var fold = new SolidBrush(Color.FromArgb(255, 221, 227, 237));
        using var stroke = new Pen(Color.FromArgb(255, 116, 126, 146), Math.Max(1f, scale));
        using var labelBrush = new SolidBrush(Color.FromArgb(255, 50, 61, 79));
        using var labelBack = new SolidBrush(Color.FromArgb(255, 222, 232, 247));

        var left = 23 * scale;
        var top = 10 * scale;
        var width = 50 * scale;
        var height = 76 * scale;
        var foldSize = 16 * scale;

        using var outline = new GraphicsPath();
        outline.AddLine(left, top, left + width - foldSize, top);
        outline.AddLine(left + width, top + foldSize, left + width, top + height);
        outline.AddLine(left + width, top + height, left, top + height);
        outline.AddLine(left, top + height, left, top);
        outline.CloseFigure();
        graphics.FillPath(page, outline);
        graphics.DrawPath(stroke, outline);

        using var foldPath = new GraphicsPath();
        foldPath.AddLine(left + width - foldSize, top, left + width - foldSize, top + foldSize);
        foldPath.AddLine(left + width - foldSize, top + foldSize, left + width, top + foldSize);
        foldPath.CloseFigure();
        graphics.FillPath(fold, foldPath);
        graphics.DrawPath(stroke, foldPath);

        FillRoundedRectangle(graphics, labelBack, 28 * scale, 57 * scale, 40 * scale, 17 * scale, 4 * scale);
        using var font = new Font("Segoe UI", Math.Max(6f, 9f * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        graphics.DrawString(label, font, labelBrush, new RectangleF(28 * scale, 57 * scale, 40 * scale, 17 * scale), format);
    }

    private static void FillRoundedRectangle(Graphics graphics, Brush brush, float x, float y, float width, float height, float radius)
    {
        using var path = RoundedRectanglePath(x, y, width, height, radius);
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRectanglePath(float x, float y, float width, float height, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string ExtensionLabel(string path)
    {
        var extension = System.IO.Path.GetExtension(path).Trim().TrimStart('.');
        return extension.Length switch
        {
            0 => "FILE",
            <= 4 => extension.ToUpperInvariant(),
            _ => extension[..4].ToUpperInvariant(),
        };
    }

    private static BitmapImage? Load(string path, bool isDirectory, int iconSize, bool useAttributes = true)
    {
        var info = new ShFileInfo();
        var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        var flags = ShgfiIcon | ShgfiSysIconIndex | (iconSize <= 16 ? ShgfiSmallIcon : ShgfiLargeIcon);

        if (useAttributes)
        {
            flags |= ShgfiUseFileAttributes;
        }

        var result = SHGetFileInfo(string.IsNullOrWhiteSpace(path) ? "file" : path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        if (info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (result == IntPtr.Zero
                || (useAttributes && info.iIcon == 0 && ShouldRejectGenericIconIndex(path, isDirectory)))
            {
                return null;
            }

            using var icon = Icon.FromHandle(info.hIcon);
            using var bitmap = icon.ToBitmap();
            return BitmapToBitmapImage(bitmap, iconSize);
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = DestroyIcon(info.hIcon);
        }
    }

    private static BitmapImage? LoadFromSystemImageList(string path, bool isDirectory, int iconSize, bool useAttributes)
    {
        IImageList? imageList = null;
        try
        {
            var info = new ShFileInfo();
            var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
            var flags = ShgfiSysIconIndex;
            if (useAttributes)
            {
                flags |= ShgfiUseFileAttributes;
            }

            var result = SHGetFileInfo(string.IsNullOrWhiteSpace(path) ? "file" : path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
            if (result == IntPtr.Zero
                || (useAttributes && info.iIcon == 0 && ShouldRejectGenericIconIndex(path, isDirectory)))
            {
                return null;
            }

            var imageListSize = iconSize switch
            {
                <= 16 => ShilSmall,
                <= 32 => ShilLarge,
                <= 48 => ShilExtraLarge,
                _ => ShilJumbo,
            };

            var iidImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            if (SHGetImageList(imageListSize, ref iidImageList, out imageList) != 0 || imageList is null)
            {
                return null;
            }

            return imageList.GetIcon(info.iIcon, IldTransparent, out var hIcon) == 0 && hIcon != IntPtr.Zero
                ? IconToBitmapImage(hIcon, iconSize)
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (imageList is not null)
            {
                Marshal.ReleaseComObject(imageList);
            }
        }
    }

    private static BitmapImage? IconToBitmapImage(IntPtr hIcon, int iconSize)
    {
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            using var bitmap = icon.ToBitmap();
            return BitmapToBitmapImage(bitmap, iconSize);
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = DestroyIcon(hIcon);
        }
    }

    private static BitmapImage BitmapToBitmapImage(Bitmap bitmap, int iconSize)
    {
        using var normalized = NormalizeIconBitmap(bitmap, iconSize);
        using var memory = new MemoryStream();
        normalized.Save(memory, ImageFormat.Png);
        memory.Position = 0;
        var image = new BitmapImage();
        image.SetSource(memory.AsRandomAccessStream());
        return image;
    }

    private static Bitmap NormalizeIconBitmap(Bitmap source, int iconSize)
    {
        var outputSize = Math.Clamp(iconSize, UiSettings.IconSizeMin, UiSettings.IconSizeMax);
        var content = FindOpaqueBounds(source);
        if (content.IsEmpty)
        {
            content = new Rectangle(0, 0, source.Width, source.Height);
        }

        var padding = Math.Max(2, (int)Math.Round(Math.Max(content.Width, content.Height) * 0.10));
        content.Inflate(padding, padding);
        content.Intersect(new Rectangle(0, 0, source.Width, source.Height));

        var destination = new Bitmap(outputSize, outputSize, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(destination);
        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var scale = Math.Min(outputSize / (float)content.Width, outputSize / (float)content.Height);
        var width = Math.Max(1, content.Width * scale);
        var height = Math.Max(1, content.Height * scale);
        var target = new RectangleF((outputSize - width) / 2f, (outputSize - height) / 2f, width, height);
        var sourceRect = new RectangleF(content.X, content.Y, content.Width, content.Height);
        graphics.DrawImage(source, target, sourceRect, GraphicsUnit.Pixel);
        return destination;
    }

    private static Rectangle FindOpaqueBounds(Bitmap bitmap)
    {
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A <= 8)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < minX || maxY < minY
            ? Rectangle.Empty
            : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(
        int iImageList,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IImageList? ppv);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig]
        int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);

        [PreserveSig]
        int ReplaceIcon(int i, IntPtr hicon, ref int pi);

        [PreserveSig]
        int SetOverlayImage(int iImage, int iOverlay);

        [PreserveSig]
        int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);

        [PreserveSig]
        int AddMasked(IntPtr hbmImage, int crMask, ref int pi);

        [PreserveSig]
        int Draw(IntPtr pimldp);

        [PreserveSig]
        int Remove(int i);

        [PreserveSig]
        int GetIcon(int i, int flags, out IntPtr picon);
    }
}

public sealed class ShellIconImage : Microsoft.UI.Xaml.Controls.UserControl
{
    public static readonly Microsoft.UI.Xaml.DependencyProperty PathProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            nameof(Path),
            typeof(string),
            typeof(ShellIconImage),
            new Microsoft.UI.Xaml.PropertyMetadata(null, OnIconPropertyChanged));

    public static readonly Microsoft.UI.Xaml.DependencyProperty IconSizeProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            nameof(IconSize),
            typeof(int),
            typeof(ShellIconImage),
            new Microsoft.UI.Xaml.PropertyMetadata(16, OnIconPropertyChanged));

    private readonly Microsoft.UI.Xaml.Controls.Image _image = new()
    {
        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
    };

    public ShellIconImage()
    {
        Content = _image;
        IsTabStop = false;
        IsHitTestVisible = false;
        Width = 16;
        Height = 16;
    }

    public string? Path
    {
        get => (string?)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public int IconSize
    {
        get => (int)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    private static void OnIconPropertyChanged(Microsoft.UI.Xaml.DependencyObject sender, Microsoft.UI.Xaml.DependencyPropertyChangedEventArgs args)
    {
        if (sender is ShellIconImage image)
        {
            image.RefreshIcon();
        }
    }

    private void RefreshIcon()
    {
        _image.Width = IconSize;
        _image.Height = IconSize;
        _image.Source = string.IsNullOrWhiteSpace(Path)
            ? null
            : ShellIconLoader.ForPath(Path, IconSize, IsDirectory);
    }

    public static readonly Microsoft.UI.Xaml.DependencyProperty IsDirectoryProperty =
        Microsoft.UI.Xaml.DependencyProperty.Register(
            nameof(IsDirectory),
            typeof(bool),
            typeof(ShellIconImage),
            new Microsoft.UI.Xaml.PropertyMetadata(false, OnIconPropertyChanged));

    public bool IsDirectory
    {
        get => (bool)GetValue(IsDirectoryProperty);
        set => SetValue(IsDirectoryProperty, value);
    }
}
