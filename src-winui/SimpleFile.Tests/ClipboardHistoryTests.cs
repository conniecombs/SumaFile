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

public class ClipboardHistoryTests
{
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
