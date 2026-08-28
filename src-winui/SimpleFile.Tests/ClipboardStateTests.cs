using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class ClipboardStateTests
{
    [Fact]
    public void InitialState_HasNoItems()
    {
        var state = new ClipboardState();
        Assert.False(state.HasItems);
        Assert.Empty(state.SourcePaths);
    }

    [Fact]
    public void SetCopy_StoresPathsAndSetsOperationToCopy()
    {
        var state = new ClipboardState();
        var paths = new[] { @"C:\test1.txt", @"C:\test2.txt" };
        
        state.SetCopy(paths);
        
        Assert.True(state.HasItems);
        Assert.Equal(ClipboardOperation.Copy, state.Operation);
        Assert.Equal(paths, state.SourcePaths);
    }

    [Fact]
    public void SetCut_StoresPathsAndSetsOperationToCut()
    {
        var state = new ClipboardState();
        var paths = new[] { @"C:\test.txt" };
        
        state.SetCut(paths);
        
        Assert.True(state.HasItems);
        Assert.Equal(ClipboardOperation.Cut, state.Operation);
        Assert.Equal(paths, state.SourcePaths);
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var state = new ClipboardState();
        state.SetCopy(new[] { @"C:\test.txt" });
        
        state.Clear();
        
        Assert.False(state.HasItems);
        Assert.Empty(state.SourcePaths);
    }

    [Fact]
    public void SetCutThenClear_HasItemsIsFalse()
    {
        var state = new ClipboardState();
        state.SetCut(new[] { @"C:\test.txt" });
        state.Clear();
        
        Assert.False(state.HasItems);
    }

    [Fact]
    public void SetCopyThenSetCut_OverridesOperation()
    {
        var state = new ClipboardState();
        state.SetCopy(new[] { @"C:\test1.txt" });
        state.SetCut(new[] { @"C:\test2.txt" });
        
        Assert.Equal(ClipboardOperation.Cut, state.Operation);
        Assert.Equal(new[] { @"C:\test2.txt" }, state.SourcePaths);
    }
}
