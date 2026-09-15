using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

internal static partial class PreviewHtmlRenderer
{
    private const int MaxTableRows = 80;
    private const int MaxTableColumns = 16;

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

    private static string RenderMarkdown(string markdown)
    {
        var html = new StringBuilder();
        var paragraph = new List<string>();
        string? listTag = null;
        var inCode = false;
        var code = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            html.Append("<p>");
            html.Append(string.Join("<br>", paragraph.Select(RenderInlineMarkdown)));
            html.AppendLine("</p>");
            paragraph.Clear();
        }

        void CloseList()
        {
            if (listTag is null)
            {
                return;
            }

            html.Append("</");
            html.Append(listTag);
            html.AppendLine(">");
            listTag = null;
        }

        foreach (var rawLine in NormalizeLines(markdown))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    html.Append("<pre><code>");
                    html.Append(WebUtility.HtmlEncode(code.ToString().TrimEnd('\r', '\n')));
                    html.AppendLine("</code></pre>");
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    FlushParagraph();
                    CloseList();
                    inCode = true;
                }

                continue;
            }

            if (inCode)
            {
                code.AppendLine(line);
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                CloseList();
                continue;
            }

            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                FlushParagraph();
                CloseList();
                var level = heading.Groups[1].Value.Length;
                html.Append("<h");
                html.Append(level);
                html.Append('>');
                html.Append(RenderInlineMarkdown(heading.Groups[2].Value.Trim()));
                html.Append("</h");
                html.Append(level);
                html.AppendLine(">");
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                CloseList();
                html.Append("<blockquote>");
                html.Append(RenderInlineMarkdown(trimmed[2..].Trim()));
                html.AppendLine("</blockquote>");
                continue;
            }

            var unordered = UnorderedListRegex().Match(trimmed);
            var ordered = OrderedListRegex().Match(trimmed);
            if (unordered.Success || ordered.Success)
            {
                FlushParagraph();
                var targetList = unordered.Success ? "ul" : "ol";
                if (!string.Equals(listTag, targetList, StringComparison.Ordinal))
                {
                    CloseList();
                    listTag = targetList;
                    html.Append('<');
                    html.Append(listTag);
                    html.AppendLine(">");
                }

                var item = unordered.Success ? unordered.Groups[1].Value : ordered.Groups[1].Value;
                html.Append("<li>");
                html.Append(RenderInlineMarkdown(item.Trim()));
                html.AppendLine("</li>");
                continue;
            }

            paragraph.Add(line.Trim());
        }

        if (inCode)
        {
            html.Append("<pre><code>");
            html.Append(WebUtility.HtmlEncode(code.ToString().TrimEnd('\r', '\n')));
            html.AppendLine("</code></pre>");
        }

        FlushParagraph();
        CloseList();
        return html.ToString();
    }

    private static string RenderInlineMarkdown(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        encoded = InlineCodeRegex().Replace(encoded, "<code>$1</code>");
        encoded = BoldRegex().Replace(encoded, "<strong>$1</strong>");
        encoded = ItalicRegex().Replace(encoded, "<em>$1</em>");
        encoded = LinkRegex().Replace(encoded, match =>
        {
            var label = match.Groups["label"].Value;
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            return IsSafeHref(href)
                ? $"""<a href="{WebUtility.HtmlEncode(href)}">{label}</a>"""
                : label;
        });
        return encoded;
    }

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

    private static bool IsSafeHref(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp
                || uri.Scheme == Uri.UriSchemeHttps
                || uri.Scheme == Uri.UriSchemeMailto);

    [GeneratedRegex("^(#{1,6})\\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex("^[*+-]\\s+(.+)$")]
    private static partial Regex UnorderedListRegex();

    [GeneratedRegex("^\\d+[.)]\\s+(.+)$")]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex("\\*\\*([^*]+)\\*\\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex("(?<!\\*)\\*([^*]+)\\*(?!\\*)")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex("\\[(?<label>[^\\]]+)\\]\\((?<href>[^\\)]+)\\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex("<\\s*(script|iframe|object|embed|link|meta)[^>]*>.*?<\\s*/\\s*\\1\\s*>|<\\s*(script|iframe|object|embed|link|meta)[^>]*/?\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UnsafeElementRegex();

    [GeneratedRegex("\\s+on[a-z]+\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttributeRegex();

    [GeneratedRegex("(href|src)\\s*=\\s*(\"|')?\\s*(javascript:|data:text/html)", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousUrlRegex();

    [GeneratedRegex("@import\\s+url|expression\\s*\\(", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousCssRegex();
}
