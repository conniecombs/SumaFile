using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class ParityFeaturesTests
{
    [Fact]
    public void PlacesStore_AddsRemovesAndCapsRecents()
    {
        var bookmarks = PlacesStore.AddBookmark([], @"C:\Work");
        bookmarks = PlacesStore.AddBookmark(bookmarks, @"C:\Temp");
        Assert.Equal(@"C:\Temp", bookmarks[0].Path);
        bookmarks = PlacesStore.RemoveBookmark(bookmarks, @"C:\Temp");
        Assert.Equal(@"C:\Work", bookmarks.Single().Path);

        var recents = new List<string>();
        for (var index = 0; index < 20; index++)
        {
            recents = PlacesStore.RecordRecent(recents, $@"C:\item{index}");
        }

        Assert.Equal(PlacesStore.RecentLimit, recents.Count);
        Assert.Equal(@"C:\item19", recents[0]);
    }

    [Fact]
    public void TypeAhead_MatchesPrefixAndResetsAfterIdleWindow()
    {
        var entries = new[]
        {
            new FileEntry { Name = "Alpha.txt" },
            new FileEntry { Name = "Bravo.txt" },
        };
        Assert.Equal(1, TypeAhead.MatchIndex(entries, "br"));
        var buffer = new TypeAheadBuffer();
        buffer.Append('A', TimeSpan.FromSeconds(1));
        Assert.Equal("A", buffer.Text);
    }

    [Fact]
    public void PhotoFolder_DetectsImageHeavyDirectory()
    {
        var photos = new[]
        {
            new FileEntry { Name = "a.png", Extension = "png" },
            new FileEntry { Name = "b.jpg", Extension = "jpg" },
            new FileEntry { Name = "c.txt", Extension = "txt" },
        };
        Assert.True(PhotoFolder.IsPhotoFolder(photos, 60));
        Assert.False(PhotoFolder.IsPhotoFolder(photos, 80));
    }

    [Fact]
    public void AdvancedRename_FindReplacePrefixNumber()
    {
        var plan = new AdvancedRenamePlan { Find = "img", Replace = "shot", Prefix = "x-", StartNumber = 1 };
        Assert.Equal("x-shot1.png", AdvancedRename.Apply("img.png", plan, 0));
        var requests = AdvancedRename.Build(
            [new FileEntry { Name = "img.png", Path = @"C:\img.png" }],
            plan);
        Assert.Equal("x-shot1.png", requests[0].NewName);
    }

    [Fact]
    public void AdvancedRename_TemplateTokensKeepExtensionAndUseNumberSettings()
    {
        var plan = new AdvancedRenamePlan
        {
            TemplateEnabled = true,
            TemplatePattern = "{parent}_{base}_{n}_{date}_{time}",
            TemplateKeepExt = true,
            NumberStart = 5,
            NumberStep = 2,
            NumberPad = 2,
        };
        var now = new DateTimeOffset(2026, 8, 20, 13, 14, 15, TimeSpan.Zero);

        var next = AdvancedRename.BuildName(
            new FileEntry { Name = "IMG_0001.JPG", Path = @"C:\Photos\IMG_0001.JPG" },
            1,
            plan,
            now);

        Assert.Equal("Photos_IMG_0001_07_2026-08-20_131415.JPG", next);
    }

    [Fact]
    public void AdvancedRename_AppliesLegacyOperationOrder()
    {
        var plan = new AdvancedRenamePlan
        {
            ApplyPart = "base",
            RemoveEnabled = true,
            RemoveString = "draft",
            ReplaceEnabled = true,
            ReplaceFind = "report",
            ReplaceWith = "summary",
            TrimEnabled = true,
            TrimCollapse = true,
            AddEnabled = true,
            AddString = "final ",
            AddPosition = "prefix",
            CapitalizeEnabled = true,
            CapitalizeMode = "title",
            SeparatorEnabled = true,
            SeparatorMode = "spaces-to-underscores",
            SeparatorCollapse = true,
            NumberEnabled = true,
            NumberStart = 3,
            NumberStep = 1,
            NumberPad = 2,
            NumberPosition = "before-ext",
            NumberSeparator = "-",
            ExtensionEnabled = true,
            ExtensionMode = "upper",
        };

        var next = AdvancedRename.BuildName(
            new FileEntry { Name = "  draft report  .txt", Path = @"C:\Docs\  draft report  .txt" },
            0,
            plan);

        Assert.Equal("Final_Summary-03.TXT", next);
    }

    [Theory]
    [InlineData("words", "UPDATER_RELEASE.md", "Updater_Release.md")]
    [InlineData("title", "CODE_OF_CONDUCT.md", "Code_Of_Conduct.md")]
    [InlineData("title", "mixed CASE-file.TXT", "Mixed Case-File.TXT")]
    public void AdvancedRename_NormalizesWordCapitalizationAndPreservesExtension(
        string mode,
        string originalName,
        string expectedName)
    {
        var plan = new AdvancedRenamePlan
        {
            CapitalizeEnabled = true,
            CapitalizeMode = mode,
        };

        var next = AdvancedRename.BuildName(
            new FileEntry { Name = originalName, Path = $@"C:\Docs\{originalName}" },
            0,
            plan);

        Assert.Equal(expectedName, next);
    }

    [Fact]
    public void AdvancedRename_CapitalizationCanStillTargetExtensionExplicitly()
    {
        var plan = new AdvancedRenamePlan
        {
            ApplyPart = "extension",
            CapitalizeEnabled = true,
            CapitalizeMode = "title",
        };

        var next = AdvancedRename.BuildName(
            new FileEntry { Name = "release_notes.md", Path = @"C:\Docs\release_notes.md" },
            0,
            plan);

        Assert.Equal("release_notes.Md", next);
    }

    [Fact]
    public void AdvancedRename_SentenceCaseKeepsFirstLetterModeDistinct()
    {
        var sentencePlan = new AdvancedRenamePlan
        {
            CapitalizeEnabled = true,
            CapitalizeMode = "sentence",
        };
        var firstLetterPlan = new AdvancedRenamePlan
        {
            CapitalizeEnabled = true,
            CapitalizeMode = "first",
        };

        var sentence = AdvancedRename.BuildName(
            new FileEntry { Name = "UPDATER RELEASE.md", Path = @"C:\Docs\UPDATER RELEASE.md" },
            0,
            sentencePlan);
        var firstLetter = AdvancedRename.BuildName(
            new FileEntry { Name = "UPDATER RELEASE.md", Path = @"C:\Docs\UPDATER RELEASE.md" },
            0,
            firstLetterPlan);

        Assert.Equal("Updater release.md", sentence);
        Assert.Equal("UPDATER RELEASE.md", firstLetter);
    }

    [Fact]
    public void AdvancedRename_FilterPreviewValidatesDuplicateAndInvalidNames()
    {
        var entries = new[]
        {
            new AdvancedRenameTarget
            {
                Entry = new FileEntry { Name = "one.jpg", Path = @"C:\Pics\one.jpg" },
                ParentPath = @"C:\Pics",
                Index = 0,
            },
            new AdvancedRenameTarget
            {
                Entry = new FileEntry { Name = "two.jpg", Path = @"C:\Pics\two.jpg" },
                ParentPath = @"C:\Pics",
                Index = 1,
            },
            new AdvancedRenameTarget
            {
                Entry = new FileEntry { Name = "note.txt", Path = @"C:\Pics\note.txt" },
                ParentPath = @"C:\Pics",
                Index = 2,
            },
        };
        var plan = new AdvancedRenamePlan
        {
            FilterEnabled = true,
            FilterExtensions = "jpg",
            TemplateEnabled = true,
            TemplatePattern = "album",
            TemplateKeepExt = false,
        };

        var preview = AdvancedRename.BuildPreview(entries, plan);

        Assert.Equal("rows", preview.Mode);
        Assert.Equal(2, preview.TotalRows);
        Assert.Equal(2, preview.InvalidCount);
        Assert.All(preview.AllRows, row => Assert.Equal("Duplicate target name", row.Error));
    }

    [Fact]
    public void AdvancedRename_SanitizeAndValidateWindowsFileNames()
    {
        var sanitized = AdvancedRename.SanitizeFileName("bad:name?.txt", "_");
        Assert.Equal("bad_name_.txt", sanitized);
        Assert.True(AdvancedRename.IsValidFileName(sanitized));
        Assert.False(AdvancedRename.IsValidFileName("CON.txt"));
        Assert.False(AdvancedRename.IsValidFileName("trailing-space .txt "));
    }

    [Fact]
    public async Task AdvancedRename_CollectTargetsRecursesAndSkipsDotfiles()
    {
        var listing = new DirectoryListing
        {
            Path = @"C:\Root\Folder",
            Entries =
            [
                new FileEntry { Name = "child.txt", Path = @"C:\Root\Folder\child.txt" },
                new FileEntry { Name = ".secret", Path = @"C:\Root\Folder\.secret" },
            ],
        };
        var plan = new AdvancedRenamePlan { ScopeRecursive = true };
        var selected = new[]
        {
            new FileEntry { Name = "Folder", Path = @"C:\Root\Folder", IsDir = true },
        };

        var targets = await AdvancedRename.CollectTargetsAsync(
            selected,
            @"C:\Root",
            plan,
            (path, _) =>
            {
                Assert.Equal(@"C:\Root\Folder", path);
                return Task.FromResult(listing);
            });

        Assert.Equal(["Folder", "child.txt"], targets.Select(target => target.Entry.Name));
        Assert.Equal([0, 1], targets.Select(target => target.Index));
    }

    [Fact]
    public void Marquee_IntersectsVerticalRange()
    {
        Assert.True(MarqueeSelection.Intersects(0, 10, 100, 30, 20, 40));
        Assert.False(MarqueeSelection.Intersects(0, 10, 100, 5, 40, 50));
    }

    [Fact]
    public void FolderTree_FlattensExpandedChildren()
    {
        var roots = new[]
        {
            new TreeNode
            {
                Name = "Users",
                Path = @"C:\Users",
                HasChildren = true,
                Children = [new TreeNode { Name = "test", Path = @"C:\Users\test" }],
            },
        };
        var flat = FolderTree.Flatten(roots, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Users" });
        Assert.Equal(2, flat.Count);
        Assert.Equal(1, flat[1].Depth);
    }

    [Fact]
    public void ClipboardHistory_KeepsLatestFirst()
    {
        var history = new ClipboardHistory();
        history.Push(ClipboardOperation.Copy, [@"C:\a"]);
        history.Push(ClipboardOperation.Cut, [@"C:\b"]);
        Assert.Equal(ClipboardOperation.Cut, history.Items[0].Operation);
        Assert.Equal(@"C:\b", history.Items[0].Paths[0]);
    }
}
