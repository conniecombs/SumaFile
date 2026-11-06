using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// The omnibar is the single text input of the toolbar: it owns path, search, filter and
/// command entry. The former "Find in folder" and "Filter list" boxes only mirrored it, and the
/// pane context bar already shows the active filter and search.
/// </summary>
public class OmnibarConsolidationTests
{
    [Fact]
    public void Toolbar_HasExactlyOneTextInput()
    {
        var toolbar = UiAuditSprint3Tests.Read("PrimaryToolbarView.xaml");

        Assert.DoesNotContain("x:Name=\"SearchBox\"", toolbar);
        Assert.DoesNotContain("x:Name=\"QuickFilterBox\"", toolbar);
        Assert.DoesNotContain("x:Name=\"PrimarySearchHost\"", toolbar);
        Assert.Single(Regex.Matches(toolbar, @"<TextBox\b"));
    }

    [Fact]
    public void OmnibarSearchMode_HostsContentSearchAndCancelControls()
    {
        var toolbar = UiAuditSprint3Tests.Read("PrimaryToolbarView.xaml");
        var hostStart = toolbar.IndexOf("x:Name=\"OmnibarHost\"", StringComparison.Ordinal);
        var hostEnd = toolbar.IndexOf("x:Name=\"PrimaryActionsHost\"", StringComparison.Ordinal);
        var contentSearch = toolbar.IndexOf("x:Name=\"ContentSearchButton\"", StringComparison.Ordinal);
        var cancel = toolbar.IndexOf("x:Name=\"SearchCancelButton\"", StringComparison.Ordinal);

        Assert.True(hostStart >= 0 && hostEnd > hostStart);
        Assert.InRange(contentSearch, hostStart, hostEnd);
        Assert.InRange(cancel, hostStart, hostEnd);
    }

    [Fact]
    public void OmnibarFilterMode_AppliesTheFilterWhileTyping()
    {
        var toolbar = UiAuditSprint3Tests.Read("PrimaryToolbarView.xaml");
        var omnibar = UiAuditSprint3Tests.Read("MainWindow.Omnibar.cs");

        Assert.Contains("TextChanged=\"OnOmnibarTextChanged\"", toolbar);
        var handler = UiAuditSprint3Tests.Slice(omnibar, "private void OnOmnibarTextChanged(", "private void FocusOmnibar(");
        Assert.Contains("SetFilterQuery(", handler);
    }

    [Fact]
    public void OverflowPlanner_NoLongerManagesSearchOrFilter()
    {
        Assert.DoesNotContain("filter", ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.DoesNotContain("search", ToolbarOverflowPlanner.PrimaryHideOrder);

        var widths = ToolbarOverflowPlanner.PrimaryItemWidthsFor(1200, CommandSurfaceLayout.CreateDefault());
        Assert.DoesNotContain("filter", widths.Keys);
        Assert.DoesNotContain("search", widths.Keys);
    }

    [Fact]
    public void OverflowInputFlyout_IsRemovedWithTheBoxes()
    {
        var commands = UiAuditSprint3Tests.Read("MainWindow.Commands.cs");
        Assert.DoesNotContain("ShowOverflowInputFlyout", commands);
        Assert.DoesNotContain("ActiveToolbarQuickFilterBox", commands);
        Assert.DoesNotContain("ActiveToolbarSearchTextBox", commands);
    }
}
