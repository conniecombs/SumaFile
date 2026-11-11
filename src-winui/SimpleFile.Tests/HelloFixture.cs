using System;
using System.IO;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>Shared access to the Hello fixture plugin copied to the test output.</summary>
internal static class HelloFixture
{
    public const string Id = "com.sumafile.hello";

    /// <summary>Tests that point <c>SUMAFILE_HELLO_MARKER_DIR</c> somewhere join this collection, since the variable is process-wide.</summary>
    public const string Collection = "Hello fixture marker directory";

    public static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "plugins");

    public static string Folder => Path.Combine(Root, Id);

    /// <summary>Copies the fixture folder into <paramref name="root"/> and returns the copy's path.</summary>
    public static string CopyTo(string root)
    {
        var target = Path.Combine(root, Id);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(Folder))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        return target;
    }

    /// <summary>Points the fixture's marker files at a fresh temp folder until disposed.</summary>
    public sealed class MarkerScope : IDisposable
    {
        private const string Variable = "SUMAFILE_HELLO_MARKER_DIR";
        private readonly string? _previous = Environment.GetEnvironmentVariable(Variable);

        public MarkerScope()
        {
            Directory = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(Variable, Directory);
        }

        public string Directory { get; }

        public string Activated => Path.Combine(Directory, "activated.txt");

        public string CommandsLog => Path.Combine(Directory, "commands.log");

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Variable, _previous);
            try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
        }
    }
}

[CollectionDefinition(HelloFixture.Collection)]
public sealed class HelloFixtureCollection;
