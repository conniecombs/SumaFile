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

public class ColumnLayoutTests
{
    [Fact]
    public void ColumnLayout_ClampsResizeAndAppliesPresets()
    {
        var columns = new ColumnLayout();
        columns.Resize("size", 10);
        Assert.Equal(36, columns.WidthOf("size"));
        columns.Resize("size", 800);
        Assert.Equal(800, columns.WidthOf("size"));
        columns.Resize("name", 40);
        Assert.Equal(80, columns.WidthOf("name"));
        columns.Resize("name", 500);
        Assert.Equal(500, columns.WidthOf("name"));
        Assert.Equal(columns.VisibleColumns.Sum(column => column.Width), columns.VisibleWidth);
        columns.ApplyPreset("developer");
        Assert.Contains("git", columns.VisibleIds);
        Assert.Equal(["name", "size", "date", "extension", "git", "symlink", "path"], columns.VisibleColumns.Select(column => column.Id));
        columns.RestoreWidths(new Dictionary<string, double> { ["name"] = 300 });
        Assert.Equal(300, columns.WidthOf("name"));
    }
}
