using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>What a transfer row says about the files it is copying right now.</summary>
public sealed class TransferActiveFilesTests
{
    private static TransferOperationViewModel Operation()
    {
        var operation = new TransferOperationViewModel(false, [@"D:\Movies"], @"V:\sync",
            (_, _) => Task.FromResult(TransferOperationStatus.Completed));
        operation.SetOperationId("job");
        return operation;
    }

    private static TuningUpdate Active(params string[] paths) => new()
    {
        OperationId = "job",
        Concurrency = paths.Length,
        ActiveFiles = [.. paths.Select(path => new ActiveFile { Path = path, Current = 1, Total = 10 })],
    };

    // Path.Combine, so the formatter finds the file name with either platform's separator.
    private static readonly string MovieA = Path.Combine("Movies", "Movie A.mkv");

    private static ProgressUpdate Running(string currentItem) => new()
    {
        OperationId = "job",
        Status = "running",
        Current = 10,
        Total = 100,
        CurrentItem = currentItem,
    };

    [Fact]
    public void AnActiveFileSplitsIntoItsFolderAndItsNameOnAnyPlatform()
    {
        var windows = new ActiveTransferFile(@"D:\Movies\2024\Movie A.mkv");
        Assert.Equal("Movie A.mkv", windows.Name);
        Assert.Equal(@"D:\Movies\2024\", windows.Folder);

        var share = new ActiveTransferFile(@"\\nas\media\clip.mp4");
        Assert.Equal("clip.mp4", share.Name);
        Assert.Equal(@"\\nas\media\", share.Folder);

        var forward = new ActiveTransferFile("/home/user/a.txt");
        Assert.Equal("a.txt", forward.Name);
        Assert.Equal("/home/user/", forward.Folder);

        var bare = new ActiveTransferFile("image.jpg");
        Assert.Equal("image.jpg", bare.Name);
        Assert.Equal("", bare.Folder);
    }

    [Fact]
    public void ActiveFilesAreGroupedByFolderWithFoldersAndNamesInOrder()
    {
        var operation = Operation();
        operation.ApplyTuning(Active(
            @"D:\Movies\2024\Movie B.mkv",
            @"D:\Movies\2023\Movie C.mkv",
            @"D:\Movies\2024\Movie A.mkv",
            @"D:\Movies\2024\movie a2.mkv"));

        Assert.Equal([@"D:\Movies\2023\", @"D:\Movies\2024\"], operation.ActiveFolders.Select(folder => folder.Folder));
        Assert.Equal(["Movie C.mkv"], operation.ActiveFolders[0].Files.Select(file => file.Name));
        Assert.Equal(["Movie A.mkv", "movie a2.mkv", "Movie B.mkv"], operation.ActiveFolders[1].Files.Select(file => file.Name));
        // The flat list is unchanged: one row per file, in the order the service reported them.
        Assert.Equal(4, operation.ActiveFiles.Count);
        Assert.Same(operation.ActiveFiles[1], operation.ActiveFolders[0].Files[0]);
    }

    [Fact]
    public void FoldersThatDifferOnlyInCaseAreOneGroup()
    {
        var operation = Operation();
        operation.ApplyTuning(Active(@"D:\Movies\a.mkv", @"d:\movies\b.mkv"));

        var folder = Assert.Single(operation.ActiveFolders);
        Assert.Equal(["a.mkv", "b.mkv"], folder.Files.Select(file => file.Name));
    }

    [Fact]
    public void AnUpdateKeepsTheRowsThatAreStillActiveAndDropsEmptyFolders()
    {
        var operation = Operation();
        operation.ApplyTuning(Active(@"D:\a\1.bin", @"D:\a\2.bin", @"D:\b\3.bin"));
        var folderA = operation.ActiveFolders[0];
        var second = folderA.Files[1];

        operation.ApplyTuning(Active(@"D:\a\2.bin", @"D:\a\0.bin", @"D:\c\4.bin"));

        Assert.Equal([@"D:\a\", @"D:\c\"], operation.ActiveFolders.Select(folder => folder.Folder));
        Assert.Same(folderA, operation.ActiveFolders[0]);
        Assert.Equal(["0.bin", "2.bin"], folderA.Files.Select(file => file.Name));
        Assert.Same(second, folderA.Files[1]);
    }

    [Fact]
    public void CompletionEmptiesTheFolders()
    {
        var operation = Operation();
        operation.ApplyTuning(Active(@"D:\a\1.bin", @"D:\b\2.bin"));
        operation.Complete(TransferOperationStatus.Completed);

        Assert.Empty(operation.ActiveFolders);
        Assert.Empty(operation.ActiveFiles);
    }

    [Fact]
    public void TheRowNamesOneFileAndCountsTheOthers()
    {
        var operation = Operation();
        operation.ApplyProgress(Running(MovieA));
        Assert.Equal("Movie A.mkv", operation.CurrentItemText);
        Assert.Equal("", operation.CurrentItemSuffix);

        operation.ApplyTuning(Active(Enumerable.Range(0, 8).Select(i => $@"D:\Movies\Movie {i}.mkv").ToArray()));
        Assert.Equal(" and 7 more files", operation.CurrentItemSuffix);
        Assert.Equal("Movie A.mkv and 7 more files", operation.CurrentItemLine);

        operation.ApplyTuning(Active(@"D:\Movies\Movie 0.mkv", @"D:\Movies\Movie 1.mkv"));
        Assert.Equal(" and 1 more file", operation.CurrentItemSuffix);

        operation.ApplyTuning(Active(@"D:\Movies\Movie 0.mkv"));
        Assert.Equal("", operation.CurrentItemSuffix);
        Assert.Equal("Movie A.mkv", operation.CurrentItemLine);
    }

    [Fact]
    public void TheCountIsOfEveryActiveFileNotOnlyTheRowsShown()
    {
        var operation = Operation();
        operation.ApplyProgress(Running(MovieA));
        operation.ApplyTuning(Active(Enumerable.Range(0, 40).Select(i => $@"D:\many\{i}.bin").ToArray()));

        Assert.Equal(32, operation.ActiveFiles.Count);
        Assert.Equal(" and 39 more files", operation.CurrentItemSuffix);
    }

    [Fact]
    public void TheCountIsNotAddedToAStatusLine()
    {
        var operation = Operation();
        // No current item yet: the line is a status ("Preparing transfer"), not a file name.
        operation.ApplyProgress(Running(""));
        operation.ApplyTuning(Active(@"D:\a\1.bin", @"D:\a\2.bin"));
        Assert.Equal("", operation.CurrentItemSuffix);

        operation.ApplyProgress(Running(MovieA));
        Assert.Equal(" and 1 more file", operation.CurrentItemSuffix);

        operation.Complete(TransferOperationStatus.Completed);
        Assert.Equal("Transfer complete", operation.CurrentItemText);
        Assert.Equal("", operation.CurrentItemSuffix);
        Assert.Equal("Transfer complete", operation.CurrentItemLine);
    }

    [Fact]
    public void ChangingTheCountRaisesTheLineSoTheWindowRedrawsIt()
    {
        var operation = Operation();
        operation.ApplyProgress(Running(MovieA));
        var raised = new List<string?>();
        operation.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        operation.ApplyTuning(Active(@"D:\a\1.bin", @"D:\a\2.bin"));

        Assert.Contains(nameof(TransferOperationViewModel.CurrentItemSuffix), raised);
        Assert.Contains(nameof(TransferOperationViewModel.CurrentItemLine), raised);
    }
}
