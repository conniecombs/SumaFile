namespace SimpleFile.Core;

public static class PreviewPathSupport
{
    public static bool CanUsePathBackedPreview(string? path, string? fileType)
    {
        if (!IsPathBackedPreviewType(fileType) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPathBackedPreviewType(string? fileType)
    {
        return IsPdfPreviewType(fileType) || IsMediaPreviewType(fileType);
    }

    public static bool IsPdfPreviewType(string? fileType)
    {
        return string.Equals(fileType, "pdf", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMediaPreviewType(string? fileType)
    {
        return string.Equals(fileType, "audio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileType, "video", StringComparison.OrdinalIgnoreCase);
    }
}
