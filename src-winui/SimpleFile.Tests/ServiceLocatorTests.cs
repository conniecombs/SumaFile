using System;
using System.IO;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public sealed class ServiceLocatorTests
{
    [Fact]
    public void TheBuildWrittenLastIsStartedWhetherDebugOrRelease()
    {
        var root = Path.Combine(Path.GetTempPath(), "sumafile-service-locator-" + Guid.NewGuid().ToString("N"));
        var debug = Path.Combine(root, "target", "debug", "simplefile-service.exe");
        var release = Path.Combine(root, "target", "release", "simplefile-service.exe");
        try
        {
            Directory.CreateDirectory(root);
            Assert.Null(ServiceLocator.FindServiceExecutable([root]));

            Directory.CreateDirectory(Path.GetDirectoryName(debug)!);
            Directory.CreateDirectory(Path.GetDirectoryName(release)!);
            File.WriteAllText(debug, "");
            File.WriteAllText(release, "");
            var older = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(debug, older);
            File.SetLastWriteTimeUtc(release, older.AddMinutes(1));
            Assert.Equal(release, ServiceLocator.FindServiceExecutable([root]));

            File.SetLastWriteTimeUtc(debug, older.AddMinutes(2));
            Assert.Equal(debug, ServiceLocator.FindServiceExecutable([root]));

            File.Delete(debug);
            Assert.Equal(release, ServiceLocator.FindServiceExecutable([root]));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AnInstalledServiceBesideTheAppIsFoundAndTheNearestRootWins()
    {
        var root = Path.Combine(Path.GetTempPath(), "sumafile-service-locator-" + Guid.NewGuid().ToString("N"));
        var nearer = Path.Combine(root, "app");
        var beside = Path.Combine(nearer, "simplefile-service.exe");
        var farther = Path.Combine(root, "target", "release", "simplefile-service.exe");
        try
        {
            Directory.CreateDirectory(nearer);
            Directory.CreateDirectory(Path.GetDirectoryName(farther)!);
            File.WriteAllText(beside, "");
            File.WriteAllText(farther, "");
            Assert.Equal(beside, ServiceLocator.FindServiceExecutable([nearer, root]));
            Assert.Equal(farther, ServiceLocator.FindServiceExecutable([root, nearer]));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
