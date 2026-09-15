using SimpleFile.Ipc;

namespace SimpleFile.Core;

public enum PreviewCapabilityKind
{
    IconOnly,
    RawText,
    RenderableHtml,
    ImageInline,
    PdfInline,
    MediaInline,
    MetadataRich,
}

public static class PreviewCapabilities
{
    private static readonly HashSet<string> HtmlRenderableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "md",
        "markdown",
        "mdx",
        "html",
        "htm",
        "json",
        "jsonc",
        "map",
        "jsonl",
        "ndjson",
        "csv",
        "tsv",
        "xml",
        "xaml",
        "yaml",
        "yml",
        "toml",
        "adoc",
        "asciidoc",
        "rst",
    };

    private static readonly HashSet<string> MetadataRichTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image",
        "pdf",
        "audio",
        "video",
        "office",
        "data",
        "archive",
        "font",
        "ebook",
        "email",
        "calendar",
        "contact",
        "certificate",
    };

    public static PreviewCapabilityKind Classify(string? path, FilePreview? preview)
    {
        if (preview is null)
        {
            return PreviewCapabilityKind.IconOnly;
        }

        if (CanRenderAsHtml(path, preview))
        {
            return PreviewCapabilityKind.RenderableHtml;
        }

        if (IsImage(preview))
        {
            return PreviewCapabilityKind.ImageInline;
        }

        if (PreviewPathSupport.IsPdfPreviewType(preview.FileType))
        {
            return PreviewCapabilityKind.PdfInline;
        }

        if (PreviewPathSupport.IsMediaPreviewType(preview.FileType))
        {
            return PreviewCapabilityKind.MediaInline;
        }

        if (string.Equals(preview.FileType, "text", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewCapabilityKind.RawText;
        }

        return MetadataRichTypes.Contains(preview.FileType)
            ? PreviewCapabilityKind.MetadataRich
            : PreviewCapabilityKind.IconOnly;
    }

    public static bool CanRenderAsHtml(string? path, FilePreview? preview)
    {
        if (preview?.Content is null)
        {
            return false;
        }

        var extension = ExtensionFor(path);
        return HtmlRenderableExtensions.Contains(extension)
            || string.Equals(preview.MimeType, "text/html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preview.MimeType, "text/markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preview.MimeType, "text/csv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preview.MimeType, "text/tab-separated-values", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preview.MimeType, "application/json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preview.MimeType, "application/xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(preview.MimeType, "application/yaml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsImage(FilePreview? preview) =>
        string.Equals(preview?.FileType, "image", StringComparison.OrdinalIgnoreCase);

    public static bool IsVideo(FilePreview? preview) =>
        string.Equals(preview?.FileType, "video", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldRenderHtml(UiSettings settings, string? path, FilePreview? preview) =>
        settings.PreviewRenderHtml && CanRenderAsHtml(path, preview);

    public static bool ShouldPreviewVideo(UiSettings settings, FilePreview? preview) =>
        settings.PreviewVideoPlaybackEnabled && IsVideo(preview);

    public static string ExtensionFor(string? path)
    {
        var extension = Path.GetExtension(path ?? "");
        return extension.Trim().TrimStart('.').ToLowerInvariant();
    }
}
