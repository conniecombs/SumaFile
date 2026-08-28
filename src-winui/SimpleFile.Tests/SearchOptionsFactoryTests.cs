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

public class SearchOptionsFactoryTests
{
    [Fact]
    public void SearchOptionsFactory_CreatesRunCopyWithoutMutatingTemplate()
    {
        var template = new SearchOptions
        {
            Query = "invoice",
            SearchPath = "",
            CaseSensitive = true,
            IncludeHidden = true,
            FileTypes = ["pdf", "docx"],
            MaxResults = 200,
            MaxDepth = 4,
            SearchId = "saved-template",
            ContentSearch = true,
            MinSize = 1024,
            MaxSize = 4096,
            DateAfter = "2026-01-01",
            DateBefore = "2026-12-31",
        };

        var run = SearchOptionsFactory.ForRun(template, "run-42", @"C:\Work");

        Assert.Equal(@"C:\Work", run.SearchPath);
        Assert.Equal("run-42", run.SearchId);
        Assert.Equal("saved-template", template.SearchId);
        Assert.Equal("", template.SearchPath);
        Assert.NotSame(template.FileTypes, run.FileTypes);
        Assert.Equal(template.FileTypes, run.FileTypes);
    }
}
