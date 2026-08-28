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

public class DrivePresentationTests
{
    [Fact]
    public void DrivePresentation_DescribesNetworkStateForSidebar()
    {
        var offline = new DriveInfo
        {
            Name = "Projects (Z:)",
            Path = @"Z:\",
            DriveType = "network",
            DriveStatus = "offline",
            StatusDetail = "The operation timed out.",
            RemotePath = @"\\nas\projects",
        };
        Assert.Equal("Offline", DrivePresentation.Badge(offline));
        Assert.Equal("Timed out · Retry to reconnect", DrivePresentation.Description(offline));
        Assert.False(DrivePresentation.IsAvailable(offline));

        var connected = new DriveInfo
        {
            Path = @"Y:\",
            DriveType = "network",
            DriveStatus = "available",
            RemotePath = @"\\nas\media",
        };
        Assert.Equal("", DrivePresentation.Badge(connected));
        Assert.Equal(@"\\nas\media", DrivePresentation.Description(connected));
        Assert.True(DrivePresentation.IsAvailable(connected));
    }
}
