using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Undo and Redo leave a file alone only when the file system says it is missing. A check that fails (access
/// denied, a share that is offline, a path over 260 characters) used to read as "gone".
/// </summary>
public sealed class NativePathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sumafile-nativepath-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Theory]
    [InlineData(2)] // ERROR_FILE_NOT_FOUND
    [InlineData(3)] // ERROR_PATH_NOT_FOUND
    public void AFailedCheckIsMissingOnlyWhenTheFileSystemSaysNothingIsThere(int error)
    {
        Assert.Equal(PathExistence.Missing, NativePath.FromWin32Error(error));
    }

    [Theory]
    [InlineData(5)] // ERROR_ACCESS_DENIED
    [InlineData(21)] // ERROR_NOT_READY
    [InlineData(53)] // ERROR_BAD_NETPATH
    [InlineData(67)] // ERROR_BAD_NET_NAME
    [InlineData(123)] // ERROR_INVALID_NAME
    [InlineData(206)] // ERROR_FILENAME_EXCED_RANGE
    public void AnyOtherFailedCheckIsUnknown(int error)
    {
        Assert.Equal(PathExistence.Unknown, NativePath.FromWin32Error(error));
    }

    [Theory]
    [InlineData(@"C:\work\file.txt", @"\\?\C:\work\file.txt")]
    [InlineData(@"\\server\share\file.txt", @"\\?\UNC\server\share\file.txt")]
    [InlineData(@"\\.\C:\work\file.txt", @"\\?\C:\work\file.txt")]
    [InlineData(@"\\?\C:\work\file.txt", @"\\?\C:\work\file.txt")]
    [InlineData(@"\\?\UNC\server\share\file.txt", @"\\?\UNC\server\share\file.txt")]
    [InlineData(@"\??\C:\work\file.txt", @"\??\C:\work\file.txt")]
    [InlineData(@"relative\file.txt", @"relative\file.txt")]
    public void ALongPathIsGivenThePrefixThatLiftsTheLengthLimit(string path, string expected)
    {
        Assert.Equal(expected, NativePath.VerbatimForm(path));
    }

    [Fact]
    public void AShortPathIsGivenToWin32AsItIs()
    {
        Assert.Equal(@"C:\work\file.txt", NativePath.Win32Path(@"C:\work\file.txt"));
        var verbatim = @"\\?\C:\" + new string('a', 300);
        Assert.Equal(verbatim, NativePath.Win32Path(verbatim));
    }

    [Fact]
    public void ExistenceTellsAFileFromAMissingOneAndFromOneThatCannotBeChecked()
    {
        var file = Path.Combine(_root, "file.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(PathExistence.Exists, NativePath.Existence(file));
        Assert.Equal(PathExistence.Exists, NativePath.Existence(_root));
        Assert.Equal(PathExistence.Missing, NativePath.Existence(Path.Combine(_root, "missing.txt")));
        Assert.Equal(PathExistence.Missing, NativePath.Existence(Path.Combine(_root, "missing", "file.txt")));
        // A name no file system takes: the check fails, which says nothing about whether the file is there.
        Assert.Equal(PathExistence.Unknown, NativePath.Existence(Path.Combine(_root, new string('n', 300))));
        Assert.True(NativePath.ExistsNoFollow(file));
        Assert.False(NativePath.ExistsNoFollow(Path.Combine(_root, "missing.txt")));
    }

    [Fact]
    public void AFileWhosePathIsOver260CharactersExists()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The limit is Win32's; elsewhere there is nothing to lift.
            return;
        }

        var folder = _root;
        while (folder.Length < 300) folder = Path.Combine(folder, new string('d', 40));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "file.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(PathExistence.Exists, NativePath.Existence(file));
        Assert.Equal(PathExistence.Missing, NativePath.Existence(Path.Combine(folder, "missing.txt")));
        Assert.True(NativePath.IsDirectoryNoFollow(folder));
    }
}
