using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

internal static partial class PreviewHtmlRenderer
{
    private const int MaxTableRows = 80;
    private const int MaxTableColumns = 16;

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string RenderDocument(string path, FilePreview preview)
    {
        var extension = PreviewCapabilities.ExtensionFor(path);
        var content = preview.Content ?? "";
        var body = extension switch
        {
            "md" or "markdown" or "mdx" => RenderMarkdown(content),
            "html" or "htm" => SanitizeHtmlFragment(content),
            "json" or "jsonc" or "map" => RenderJson(content),
            "jsonl" or "ndjson" => RenderJsonLines(content),
            "csv" => RenderDelimited(content, ','),
            "tsv" => RenderDelimited(content, '\t'),
            "xml" or "xaml" => RenderCode(content, "xml"),
            "yaml" or "yml" => RenderCode(content, "yaml"),
            "toml" => RenderCode(content, "toml"),
            "adoc" or "asciidoc" or "rst" => RenderCode(content, "text"),
            _ => string.Equals(preview.MimeType, "text/html", StringComparison.OrdinalIgnoreCase)
                ? SanitizeHtmlFragment(content)
                : RenderCode(content, "text"),
        };

        return Wrap(path, body);
    }

    private static string Wrap(string path, string body)
    {
        var title = WebUtility.HtmlEncode(Path.GetFileName(path));
        return $$"""
            <!doctype html>
            <html>
            <head>
              <meta charset="utf-8">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data: file:; style-src 'unsafe-inline';">
              <style>
                :root { color-scheme: light dark; font-family: "Segoe UI", sans-serif; }
                body { margin: 16px; color: CanvasText; background: Canvas; line-height: 1.45; }
                h1, h2, h3, h4, h5, h6 { margin: 1.1em 0 0.35em; line-height: 1.2; }
                h1:first-child, h2:first-child, h3:first-child { margin-top: 0; }
                p { margin: 0.7em 0; }
                pre { overflow: auto; padding: 12px; border: 1px solid color-mix(in srgb, CanvasText 16%, transparent); border-radius: 6px; background: color-mix(in srgb, CanvasText 7%, Canvas); }
                code { font-family: Consolas, "Cascadia Mono", monospace; font-size: 0.92em; }
                blockquote { margin: 0.7em 0; padding-left: 12px; border-left: 3px solid Highlight; color: color-mix(in srgb, CanvasText 78%, transparent); }
                table { border-collapse: collapse; width: 100%; font-size: 0.92em; }
                th, td { border: 1px solid color-mix(in srgb, CanvasText 16%, transparent); padding: 5px 7px; text-align: left; vertical-align: top; }
                th { background: color-mix(in srgb, CanvasText 8%, Canvas); position: sticky; top: 0; }
                .muted { color: color-mix(in srgb, CanvasText 60%, transparent); font-size: 0.86em; }
              </style>
              <title>{{title}}</title>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;
    }

    private static string RenderMarkdown(string markdown) =>
        SanitizeHtmlFragment(Markdown.ToHtml(markdown, MarkdownPipeline));

    private static string RenderJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
            var formatted = JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
            return RenderCode(formatted, "json");
        }
        catch
        {
            return RenderCode(text, "json");
        }
    }

    private static string RenderJsonLines(string text)
    {
        var lines = NormalizeLines(text).Where(line => !string.IsNullOrWhiteSpace(line)).Take(200).ToList();
        var blocks = lines.Select(RenderJson);
        return string.Join("<hr>", blocks);
    }

    private static string RenderDelimited(string text, char delimiter)
    {
        var rows = NormalizeLines(text)
            .Where(line => line.Length > 0)
            .Take(MaxTableRows + 1)
            .Select(line => SplitDelimitedLine(line, delimiter).Take(MaxTableColumns).ToList())
            .ToList();
        if (rows.Count == 0)
        {
            return "<p class=\"muted\">No rows found.</p>";
        }

        var header = rows[0];
        var html = new StringBuilder();
        html.AppendLine("<table><thead><tr>");
        foreach (var cell in header)
        {
            html.Append("<th>");
            html.Append(WebUtility.HtmlEncode(cell));
            html.AppendLine("</th>");
        }

        html.AppendLine("</tr></thead><tbody>");
        foreach (var row in rows.Skip(1))
        {
            html.AppendLine("<tr>");
            foreach (var cell in row)
            {
                html.Append("<td>");
                html.Append(WebUtility.HtmlEncode(cell));
                html.AppendLine("</td>");
            }

            html.AppendLine("</tr>");
        }

        html.AppendLine("</tbody></table>");
        if (rows.Count > MaxTableRows)
        {
            html.Append("<p class=\"muted\">Preview truncated.</p>");
        }

        return html.ToString();
    }

    private static string RenderCode(string text, string language)
    {
        var encoded = WebUtility.HtmlEncode(text);
        return $"""<pre><code data-language="{WebUtility.HtmlEncode(language)}">{encoded}</code></pre>""";
    }

    private static string SanitizeHtmlFragment(string html)
    {
        var cleaned = UnsafeElementRegex().Replace(html, "");
        cleaned = EventAttributeRegex().Replace(cleaned, "");
        cleaned = DangerousUrlRegex().Replace(cleaned, "$1#");
        cleaned = DangerousCssRegex().Replace(cleaned, "");
        return cleaned;
    }

    private static List<string> SplitDelimitedLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (ch == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (ch == delimiter && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
                continue;
            }

            cell.Append(ch);
        }

        cells.Add(cell.ToString());
        return cells;
    }

    private static IEnumerable<string> NormalizeLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    [GeneratedRegex("<\\s*(script|iframe|object|embed|link|meta)[^>]*>.*?<\\s*/\\s*\\1\\s*>|<\\s*(script|iframe|object|embed|link|meta)[^>]*/?\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UnsafeElementRegex();

    [GeneratedRegex("\\s+on[a-z]+\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttributeRegex();

    [GeneratedRegex("(href|src)\\s*=\\s*(\"|')?\\s*(javascript:|data:text/html)", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousUrlRegex();

    [GeneratedRegex("@import\\s+url|expression\\s*\\(", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousCssRegex();
}
