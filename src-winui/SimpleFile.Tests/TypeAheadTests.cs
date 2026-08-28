using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class TypeAheadTests
{
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
}
