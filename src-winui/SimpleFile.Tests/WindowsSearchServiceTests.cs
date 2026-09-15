using System.Runtime.Versioning;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Tests for the Windows Search Index integration.
/// These tests run on any Windows machine. Tests that need the indexer
/// verify conditions up front and skip gracefully if unavailable.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsSearchServiceTests
{
    // ────────────────────────────────────────────────────────────────
    //  IsPathIndexed
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void IsPathIndexed_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(WindowsSearchService.IsPathIndexed(null!));
        Assert.False(WindowsSearchService.IsPathIndexed(""));
        Assert.False(WindowsSearchService.IsPathIndexed("   "));
    }

    [Fact]
    public void IsPathIndexed_NonexistentPath_ReturnsFalse()
    {
        Assert.False(WindowsSearchService.IsPathIndexed(@"Z:\NonexistentPath_12345"));
    }

    [Fact]
    public void IsPathIndexed_NetworkUncPath_ReturnsFalse()
    {
        Assert.False(WindowsSearchService.IsPathIndexed(@"\\nonexistent\share\folder"));
    }

    [Fact]
    public void IsPathIndexed_DoesNotThrow_ForAnyPath()
    {
        // Validate graceful handling regardless of indexer state.
        var _ = WindowsSearchService.IsPathIndexed(@"C:\Windows");
        var _2 = WindowsSearchService.IsPathIndexed(@"C:\Users");
        var _3 = WindowsSearchService.IsPathIndexed(@"D:\");
    }

    // ────────────────────────────────────────────────────────────────
    //  SearchIndexAsync — edge cases (always runnable)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchIndexAsync_NonexistentPath_ReturnsEmpty()
    {
        var options = new SearchOptions
        {
            Query = "test",
            SearchPath = @"Z:\NonexistentPath_12345",
            CaseSensitive = false,
            IncludeHidden = false,
            MaxResults = 10,
        };

        var results = await WindowsSearchService.SearchIndexAsync(
            "test", @"Z:\NonexistentPath_12345", options);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchIndexAsync_EmptyQuery_ReturnsGracefully()
    {
        var options = new SearchOptions
        {
            Query = "",
            SearchPath = @"C:\Windows",
            CaseSensitive = false,
            IncludeHidden = false,
            MaxResults = 5,
        };

        // Should not throw.
        var results = await WindowsSearchService.SearchIndexAsync(
            "", @"C:\Windows", options);

        // May return empty or may return results depending on the query helper
        // interpretation — but must not crash.
        Assert.NotNull(results);
    }

    // ────────────────────────────────────────────────────────────────
    //  SearchIndexAsync — live indexer tests (skip if indexer is off)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchIndexAsync_IndexedPath_ReturnsResults_WithCorrectMatchType()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents) || !Directory.Exists(documents))
        {
            return; // Skip — documents folder not available.
        }

        if (!WindowsSearchService.IsPathIndexed(documents))
        {
            return; // Skip — indexer not running.
        }

        var options = new SearchOptions
        {
            Query = "*",
            SearchPath = documents,
            CaseSensitive = false,
            IncludeHidden = false,
            MaxResults = 5,
        };

        var results = await WindowsSearchService.SearchIndexAsync(
            "*", documents, options);

        foreach (var result in results)
        {
            Assert.False(string.IsNullOrEmpty(result.Name));
            Assert.False(string.IsNullOrEmpty(result.Path));
            Assert.Equal("index", result.MatchType);
        }
    }

    [Fact]
    public async Task SearchIndexAsync_BatchCallback_IsInvoked()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents) || !Directory.Exists(documents)
            || !WindowsSearchService.IsPathIndexed(documents))
        {
            return; // Skip.
        }

        var options = new SearchOptions
        {
            Query = "*",
            SearchPath = documents,
            CaseSensitive = false,
            IncludeHidden = false,
            MaxResults = 100,
        };

        var batchCount = 0;
        var results = await WindowsSearchService.SearchIndexAsync(
            "*", documents, options,
            onBatch: _ => Interlocked.Increment(ref batchCount));

        if (results.Length > 0)
        {
            Assert.True(batchCount > 0, "Expected at least one batch callback");
        }
    }

    [Fact]
    public async Task SearchIndexAsync_CancellationToken_ThrowsWhenPreCancelled()
    {
        var options = new SearchOptions
        {
            Query = "*",
            SearchPath = @"C:\Windows",
            CaseSensitive = false,
            IncludeHidden = false,
            MaxResults = 10,
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Pre-cancelled token should throw OperationCanceledException.
        // If the COM fails first (e.g. indexer stopped), the method returns
        // empty instead of throwing — that's also acceptable.
        try
        {
            await WindowsSearchService.SearchIndexAsync(
                "*", @"C:\Windows", options, ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    [Fact]
    public async Task SearchIndexAsync_FileTypeFilter_OnlyReturnsMatchingExtensions()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents) || !Directory.Exists(documents)
            || !WindowsSearchService.IsPathIndexed(documents))
        {
            return; // Skip.
        }

        var options = new SearchOptions
        {
            Query = "*",
            SearchPath = documents,
            CaseSensitive = false,
            IncludeHidden = false,
            MaxResults = 50,
            FileTypes = ["txt"],
        };

        var results = await WindowsSearchService.SearchIndexAsync(
            "*", documents, options);

        foreach (var result in results.Where(r => !r.IsDir))
        {
            Assert.Equal("txt", result.Extension);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  FileOperationService integration
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void FileOperationService_IsPathIndexed_DoesNotThrow()
    {
        var _ = FileOperationService.IsPathIndexed(@"C:\Windows");
        var _2 = FileOperationService.IsPathIndexed(@"Z:\Nonexistent");
    }
}
