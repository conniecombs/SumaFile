using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class PreviewHtmlRendererTests
{
    [Fact]
    public void MarkdownDocument_RendersGitHubFlavoredTaskListsAndTables()
    {
        var preview = new FilePreview
        {
            FileType = "text",
            MimeType = "text/markdown",
            Content = """
                # Release checklist

                - [x] Write tests
                - [ ] Ship renderer

                | State | Count |
                | --- | ---: |
                | Done | 1 |
                """,
        };

        var html = PreviewHtmlRenderer.RenderDocument(@"C:\notes\checklist.md", preview);

        Assert.Contains("<h1", html);
        Assert.Contains("Release checklist", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("checked=\"checked\"", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<th", html);
        Assert.DoesNotContain("[ ] Ship renderer", html);
    }

    [Fact]
    public void MarkdownDocument_RendersRepositoryCleanupFileAsMarkdown()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "CODE_CLEANUP_CANDIDATES.md"));
        var preview = new FilePreview
        {
            FileType = "text",
            MimeType = "text/markdown",
            Content = File.ReadAllText(sourcePath),
        };

        var html = PreviewHtmlRenderer.RenderDocument(sourcePath, preview);

        Assert.Contains("<h1", html);
        Assert.Contains("Code Cleanup Candidates", html);
        Assert.Contains("<h2", html);
        Assert.Contains("<ol>", html);
        Assert.DoesNotContain("<pre><code data-language=\"text\"># Code Cleanup Candidates", html);
    }
}
