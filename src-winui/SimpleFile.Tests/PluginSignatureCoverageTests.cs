using System;
using System.IO;
using System.Linq;
using System.Text;
using SimpleFile.PluginHost.Manifest;
using SimpleFile.PluginHost.Trust;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Final fix wave, item 2: a Signed state speaks for every file in the plugin folder. A regular file that
/// <c>signature.json</c> does not list (recursively; the root <c>plugin.json</c> and <c>signature.json</c> aside) makes
/// the state Invalid, naming it. <c>signature.json</c> is read bounded at 1 MiB and may start with a UTF-8 BOM.
/// </summary>
public sealed class PluginSignatureCoverageTests : IDisposable
{
    // Fixtures/signed-plugin, signed once by scripts/sign-plugin.mjs (see PluginTrustTests.SignedFixture_*).
    private const string Fingerprint = "aea57a90c57b3693";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string FixtureFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml")) && Directory.Exists(Path.Combine(dir.FullName, "src-winui")))
                return Path.Combine(dir.FullName, "src-winui", "SimpleFile.Tests", "Fixtures", "signed-plugin");
        }

        throw new InvalidOperationException("No folder above " + AppContext.BaseDirectory + " holds both Cargo.toml and src-winui.");
    }

    /// <summary>A copy of the signed fixture, verified as it is before the test changes it.</summary>
    private string SignedCopy()
    {
        var copy = Path.Combine(_temp, "signed-plugin");
        Directory.CreateDirectory(copy);
        foreach (var source in Directory.GetFiles(FixtureFolder()))
            File.Copy(source, Path.Combine(copy, Path.GetFileName(source)));
        Assert.Equal(SignatureStatus.Verified, Verify(copy).Status);
        return copy;
    }

    private static PluginManifest Manifest(string folder) =>
        PluginManifestReader.Read(File.ReadAllText(Path.Combine(folder, "plugin.json")), "signed-plugin").Manifest!;

    private static SignatureState Verify(string folder) => PluginSignatureVerifier.Verify(folder, Manifest(folder));

    [Fact]
    public void AnUnlistedFileAtTheRoot_IsInvalidAndNamed()
    {
        var copy = SignedCopy();
        File.WriteAllText(Path.Combine(copy, "version.dll"), "planted");

        var state = Verify(copy);

        Assert.Equal(SignatureStatus.Invalid, state.Status);
        Assert.Equal("\"version.dll\" is in the plugin folder but not listed in signature.json files.", state.Error);
        // The signature itself checked out: the key that signed the listed files is still shown.
        Assert.Equal(Fingerprint, state.KeyFingerprint);
    }

    [Fact]
    public void AnUnlistedFileInASubfolder_IsInvalidAndNamedWithForwardSlashes()
    {
        var copy = SignedCopy();
        Directory.CreateDirectory(Path.Combine(copy, "lib", "x64"));
        File.WriteAllText(Path.Combine(copy, "lib", "x64", "extra.dll"), "planted");

        var state = Verify(copy);

        Assert.Equal(SignatureStatus.Invalid, state.Status);
        Assert.Equal("\"lib/x64/extra.dll\" is in the plugin folder but not listed in signature.json files.", state.Error);
    }

    [Fact]
    public void SeveralUnlistedFiles_NameTheFirstAndCountTheRest()
    {
        var copy = SignedCopy();
        File.WriteAllText(Path.Combine(copy, "b.dll"), "planted");
        File.WriteAllText(Path.Combine(copy, "a.dll"), "planted");
        File.WriteAllText(Path.Combine(copy, "c.dll"), "planted");

        var state = Verify(copy);

        Assert.Equal(SignatureStatus.Invalid, state.Status);
        Assert.Equal("\"a.dll\" and 2 other files in the plugin folder are not listed in signature.json files.", state.Error);
    }

    [Fact]
    public void AHiddenUnlistedFile_StillCounts()
    {
        var copy = SignedCopy();
        var hidden = Path.Combine(copy, ".hidden.dll");
        File.WriteAllText(hidden, "planted");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden | FileAttributes.System);

        var state = Verify(copy);

        Assert.Equal(SignatureStatus.Invalid, state.Status);
        Assert.Contains("\".hidden.dll\"", state.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void APluginJsonInASubfolder_IsNotExempt()
    {
        var copy = SignedCopy();
        Directory.CreateDirectory(Path.Combine(copy, "sub"));
        File.WriteAllText(Path.Combine(copy, "sub", "plugin.json"), "{}");

        var state = Verify(copy);

        Assert.Equal(SignatureStatus.Invalid, state.Status);
        Assert.Equal("\"sub/plugin.json\" is in the plugin folder but not listed in signature.json files.", state.Error);
    }

    [Fact]
    public void AnEmptySubfolder_ChangesNothing()
    {
        var copy = SignedCopy();
        Directory.CreateDirectory(Path.Combine(copy, "empty", "nested"));

        Assert.Equal(SignatureStatus.Verified, Verify(copy).Status);
    }

    [Fact]
    public void ALinkToAFolder_IsInvalid()
    {
        var copy = SignedCopy();
        var elsewhere = Path.Combine(_temp, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "code.dll"), "outside");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(copy, "linked"), elsewhere);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Windows without the symbolic link privilege (no Developer Mode): nothing to check here.
            return;
        }

        var state = Verify(copy);

        Assert.Equal(SignatureStatus.Invalid, state.Status);
        Assert.Equal("\"linked\" is in the plugin folder but not listed in signature.json files.", state.Error);
    }

    [Fact]
    public void ASignatureJsonWithAUtf8Bom_StillVerifies()
    {
        var copy = SignedCopy();
        var path = Path.Combine(copy, "signature.json");
        File.WriteAllBytes(path, [.. Encoding.UTF8.Preamble, .. File.ReadAllBytes(path)]);

        var state = Verify(copy);

        Assert.Equal(SignatureStatus.Verified, state.Status);
        Assert.Equal(Fingerprint, state.KeyFingerprint);
    }

    [Fact]
    public void ASignatureJsonLargerThan1MiB_IsInvalidWithoutBeingParsed()
    {
        var copy = SignedCopy();
        var path = Path.Combine(copy, "signature.json");
        var original = File.ReadAllText(path);
        // Valid JSON either way: only the size decides.
        File.WriteAllText(path, original + new string(' ', PluginSignatureVerifier.MaxSignatureBytes - Encoding.UTF8.GetByteCount(original)));
        Assert.Equal(SignatureStatus.Verified, Verify(copy).Status);

        File.AppendAllText(path, " ");
        var state = Verify(copy);

        Assert.Equal(SignatureStatus.Invalid, state.Status);
        Assert.Equal("signature.json is larger than 1 MiB.", state.Error);
    }

    [Fact]
    public void ListedFiles_AreHashedAsStreams()
    {
        // Source shape: a large listed file is never read whole into memory.
        var source = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(FixtureFolder())!, "..", "..", "SimpleFile.PluginHost", "Trust", "PluginSignatureVerifier.cs"));

        Assert.Contains("SHA256.HashData(stream)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SHA256.HashData(File.ReadAllBytes(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadManifestBytes_FromASeekableStream_AllocatesTheBytesOnce()
    {
        // Item 9: the seekable path used to read into a buffer one byte too long and copy it out.
        var data = Enumerable.Repeat((byte)'x', 512 * 1024).ToArray();
        using var stream = new MemoryStream(data);
        Assert.True(PluginManifestReader.TryReadManifestBytes(new MemoryStream(data), out _)); // JIT outside the measure

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(PluginManifestReader.TryReadManifestBytes(stream, out var bytes));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(data, bytes);
        Assert.True(allocated < data.Length * 3L / 2, $"{allocated} bytes allocated for {data.Length}.");
    }

    [Fact]
    public void TryReadManifestBytes_AFileThatGrowsPastItsLength_IsStillCaught()
    {
        // A stream whose Length understates what it yields (a file that grew after its length was read).
        var data = new byte[PluginManifestReader.MaxManifestBytes + 10];
        using var stream = new UnderstatedLengthStream(data, reportedLength: 100);

        Assert.False(PluginManifestReader.TryReadManifestBytes(stream, out var bytes));
        Assert.Empty(bytes);

        using var small = new UnderstatedLengthStream(new byte[300], reportedLength: 100);
        Assert.True(PluginManifestReader.TryReadManifestBytes(small, out var all));
        Assert.Equal(300, all.Length);
    }

    private sealed class UnderstatedLengthStream(byte[] data, long reportedLength) : MemoryStream(data)
    {
        public override long Length => reportedLength;
    }
}
