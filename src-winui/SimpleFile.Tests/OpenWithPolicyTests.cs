using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class OpenWithPolicyTests
{
    [Fact]
    public void DeniedExtensionsComeFromGeneratedPolicy()
    {
        Assert.Contains(".ps1", OpenWithPolicyGenerated.DeniedTargetExtensions);
        Assert.DoesNotContain(".txt", OpenWithPolicyGenerated.DeniedTargetExtensions);
    }

    [Theory]
    [InlineData(".ps1")]
    [InlineData("ps1")]
    [InlineData(".exe")]
    [InlineData(".lnk")]
    [InlineData(".msi")]
    public void DeniesScriptAndPayloadExtensions(string extension)
    {
        Assert.True(OpenWithPolicy.IsDeniedTargetExtension(extension));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".png")]
    [InlineData(".zip")]
    [InlineData(".md")]
    public void AllowsOrdinaryDocumentExtensions(string extension)
    {
        Assert.False(OpenWithPolicy.IsDeniedTargetExtension(extension));
    }

    [Fact]
    public void TrustsSystem32NotepadAndWindowsAppsByPrefix()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.True(OpenWithPolicy.IsTrustedApplicationRoot(Path.Combine(system32, "notepad.exe")));
        Assert.True(OpenWithPolicy.IsTrustedApplicationRoot(Path.Combine(programFiles, @"WindowsApps\Microsoft.Windows.Photos\Photos.exe")));
        Assert.False(OpenWithPolicy.IsTrustedApplicationRoot(@"C:\Users\me\AppData\Roaming\payload.exe"));
        Assert.False(OpenWithPolicy.IsTrustedApplicationRoot(@"\\server\share\app.exe"));
        Assert.False(OpenWithPolicy.IsTrustedApplicationRoot(@"C:\Temp\..\Windows\System32\notepad.exe"));
    }

    [Fact]
    public void DoesNotTreatProgramFilesSiblingAsTrusted()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var sibling = programFiles + " Evil\\app.exe";
        Assert.False(OpenWithPolicy.IsTrustedApplicationRoot(sibling));
    }

    [Fact]
    public void ContextMenuDisablesOpenWithForDeniedPayloads()
    {
        var apps = new[]
        {
            OpenWithApplication.FromPath(@"C:\Program Files\Microsoft VS Code\Code.exe", "Visual Studio Code", "suggested"),
        };

        var denied = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            AllSelectedAreFiles = true,
            SelectedExtension = ".ps1",
            OpenWithApplications = apps,
        });
        Assert.DoesNotContain(denied, entry => entry.Id == "ctx-open-with");

        var allowed = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            AllSelectedAreFiles = true,
            SelectedExtension = ".txt",
            OpenWithApplications = apps,
        });
        var openWith = Assert.Single(allowed, entry => entry.Id == "ctx-open-with");
        Assert.False(openWith.Disabled);
        Assert.Contains(openWith.Children, entry => entry.Id == "ctx-open-with-app-0");
    }
}
