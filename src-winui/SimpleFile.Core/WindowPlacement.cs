using System.Text.Json;

namespace SimpleFile.Core;

/// <summary>A rectangle on the virtual screen, in physical pixels.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// Where the main window was when it was last closed: its bounds while not maximized, and whether
/// it was maximized.
/// </summary>
public sealed record WindowPlacement(ScreenRect Bounds, bool Maximized)
{
    public const int MinWidth = 640;
    public const int MinHeight = 400;

    /// <summary>
    /// How far a window may reach past the work area and still count as on it. Windows reports a
    /// snapped or edge-aligned window with its invisible resize border outside the screen.
    /// </summary>
    public const int EdgeTolerance = 16;

    /// <summary>
    /// The bounds to open with on a screen whose work area is <paramref name="workArea"/>. A window
    /// that is on that screen stays where it was. One that is not, because its monitor is gone or
    /// the resolution changed, is made no larger than the work area and moved wholly inside it.
    /// </summary>
    public ScreenRect FitTo(ScreenRect workArea)
    {
        var width = Math.Max(Bounds.Width, Math.Min(MinWidth, workArea.Width));
        var height = Math.Max(Bounds.Height, Math.Min(MinHeight, workArea.Height));
        var sized = new ScreenRect(Bounds.X, Bounds.Y, width, height);
        if (sized.X >= workArea.X - EdgeTolerance
            && sized.Y >= workArea.Y - EdgeTolerance
            && sized.Right <= workArea.Right + EdgeTolerance
            && sized.Bottom <= workArea.Bottom + EdgeTolerance)
        {
            return sized;
        }

        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);
        return new ScreenRect(
            Math.Clamp(sized.X, workArea.X, workArea.Right - width),
            Math.Clamp(sized.Y, workArea.Y, workArea.Bottom - height),
            width,
            height);
    }
}

/// <summary>
/// Keeps the window placement in a small file of its own. It is read before the window is shown,
/// when the file service that holds the settings has not been started yet.
/// </summary>
public static class WindowPlacementStore
{
    private const int MinSide = 100;
    private const int MaxSide = 32_767;
    private const int MaxOffset = 1_000_000;

    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SumaFile",
            "window.json");

    /// <summary>The saved placement, or null when there is none that can be used.</summary>
    public static WindowPlacement? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadInt(root, "x", out var x)
                || !TryReadInt(root, "y", out var y)
                || !TryReadInt(root, "width", out var width)
                || !TryReadInt(root, "height", out var height)
                || width is < MinSide or > MaxSide
                || height is < MinSide or > MaxSide
                || Math.Abs((long)x) > MaxOffset
                || Math.Abs((long)y) > MaxOffset)
            {
                return null;
            }

            var maximized = root.TryGetProperty("maximized", out var value) && value.ValueKind == JsonValueKind.True;
            return new WindowPlacement(new ScreenRect(x, y, width, height), maximized);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the placement beside the old file and then replaces it, so a stop in between leaves
    /// the old file. Returns false when it could not be written; the window then opens at its
    /// default size next time.
    /// </summary>
    public static bool Save(string path, WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var temporary = path + ".tmp";
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("x", placement.Bounds.X);
                writer.WriteNumber("y", placement.Bounds.Y);
                writer.WriteNumber("width", placement.Bounds.Width);
                writer.WriteNumber("height", placement.Bounds.Height);
                writer.WriteBoolean("maximized", placement.Maximized);
                writer.WriteEndObject();
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Nothing more to do; the next save writes the temporary file again.
            }

            return false;
        }
    }

    private static bool TryReadInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }
}
