using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class ListReplaceTests
{
    [Fact]
    public void Apply_AppendsWhenSourceGrowsAsPrefix()
    {
        var target = new List<string> { "a", "b" };
        var changed = ListReplace.Apply(target, ["a", "b", "c", "d"], string.Equals);
        Assert.True(changed);
        Assert.Equal(["a", "b", "c", "d"], target);
    }

    [Fact]
    public void Apply_ReplacesInPlaceWhenCountMatches()
    {
        var target = new List<string> { "a", "old" };
        var changed = ListReplace.Apply(target, ["a", "new"], string.Equals);
        Assert.True(changed);
        Assert.Equal(["a", "new"], target);
    }

    [Fact]
    public void Apply_ReturnsFalseWhenUnchanged()
    {
        var target = new List<string> { "a", "b" };
        Assert.False(ListReplace.Apply(target, ["a", "b"], string.Equals));
    }

    // Counts what a bound list (an ObservableCollection behind a ListView) would be told.
    private sealed class Recording : System.Collections.ObjectModel.Collection<string>
    {
        public Recording(IEnumerable<string> items)
        {
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }

        public List<string> Operations { get; } = [];

        protected override void InsertItem(int index, string item)
        {
            Operations.Add($"insert {index} {item}");
            base.InsertItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            Operations.Add($"remove {index} {this[index]}");
            base.RemoveItem(index);
        }

        protected override void SetItem(int index, string item)
        {
            Operations.Add($"set {index} {item}");
            base.SetItem(index, item);
        }

        protected override void ClearItems()
        {
            Operations.Add("clear");
            base.ClearItems();
        }
    }

    private static string[] Rows(int count) => [.. Enumerable.Range(0, count).Select(index => $"row{index:D4}")];

    [Fact]
    public void Apply_ARowRemovedFromALargeListIsOneRemoval()
    {
        var rows = Rows(5000);
        var target = new Recording(rows);

        var changed = ListReplace.Apply(target, [.. rows.Where(row => row != "row2500")], string.Equals);

        Assert.True(changed);
        Assert.Equal(["remove 2500 row2500"], target.Operations);
        Assert.Equal(4999, target.Count);
    }

    [Fact]
    public void Apply_ARowAddedInTheMiddleIsOneInsert()
    {
        var rows = Rows(5000);
        var target = new Recording(rows);
        List<string> source = [.. rows];
        source.Insert(10, "new");

        Assert.True(ListReplace.Apply(target, source, string.Equals));

        Assert.Equal(["insert 10 new"], target.Operations);
        Assert.Equal(source, target);
    }

    [Fact]
    public void Apply_ARenamedRowThatMovesIsASmallEdit()
    {
        var target = new Recording(["a", "b", "c", "d", "e"]);

        // "b" was renamed to "x" and now sorts last.
        Assert.True(ListReplace.Apply(target, ["a", "c", "d", "e", "x"], string.Equals));

        Assert.Equal(["a", "c", "d", "e", "x"], target);
        Assert.DoesNotContain("clear", target.Operations);
        Assert.True(target.Operations.Count <= 4, string.Join("; ", target.Operations));
    }

    [Fact]
    public void Apply_AShorterListThatIsAPrefixOnlyLosesItsTail()
    {
        var target = new Recording(["a", "b", "c", "d"]);

        Assert.True(ListReplace.Apply(target, ["a", "b"], string.Equals));

        Assert.Equal(["remove 3 d", "remove 2 c"], target.Operations);
    }

    [Fact]
    public void Apply_AnotherFoldersRowsReplaceTheListWhole()
    {
        var target = new Recording(["a", "b", "c"]);

        Assert.True(ListReplace.Apply(target, ["x", "y"], string.Equals));

        // Nothing in common: one reset, so the list starts again at the top.
        Assert.Equal(["clear", "insert 0 x", "insert 1 y"], target.Operations);
    }

    [Fact]
    public void Apply_AManyRowEditReplacesTheListWhole()
    {
        var rows = Rows(1000);
        var target = new Recording(rows);
        string[] source = [.. rows.Take(100), .. rows.Skip(100 + ListReplace.MaxEditRows + 1)];

        Assert.True(ListReplace.Apply(target, source, string.Equals));

        Assert.Equal("clear", target.Operations[0]);
        Assert.Equal(source, target);
    }

    [Fact]
    public void Apply_SameLengthListsStillUpdateOnlyTheRowsThatDiffer()
    {
        var target = new Recording(["a", "b", "c", "d"]);

        Assert.True(ListReplace.Apply(target, ["a", "x", "y", "d"], string.Equals));

        Assert.Equal(["set 1 x", "set 2 y"], target.Operations);
    }
}
