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

public class MarqueeSelectionTests
{
    [Fact]
    public void Marquee_IntersectsVerticalRange()
    {
        Assert.True(MarqueeSelection.Intersects(0, 10, 100, 30, 20, 40));
        Assert.False(MarqueeSelection.Intersects(0, 10, 100, 5, 40, 50));
    }
}
