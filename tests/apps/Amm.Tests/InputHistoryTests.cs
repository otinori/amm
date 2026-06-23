using Amm.Core;

namespace Amm.Tests;

public class InputHistoryTests
{
    [Fact]
    public void NavigateUp_EmptyHistory_ReturnsNull()
    {
        var h = new InputHistory();
        Assert.Null(h.NavigateUp());
    }

    [Fact]
    public void NavigateDown_EmptyHistory_ReturnsNull()
    {
        var h = new InputHistory();
        Assert.Null(h.NavigateDown());
    }

    [Fact]
    public void Add_Whitespace_Ignored()
    {
        var h = new InputHistory();
        h.Add("");
        h.Add("   ");
        h.Add("\t\n");
        Assert.Null(h.NavigateUp());
    }

    [Fact]
    public void Add_ConsecutiveDuplicates_Deduplicated()
    {
        var h = new InputHistory();
        h.Add("ls");
        h.Add("ls");
        h.Add("ls");
        Assert.Equal("ls", h.NavigateUp());
        Assert.Equal("ls", h.NavigateUp());
    }

    [Fact]
    public void NavigateUp_CyclesBackward()
    {
        var h = new InputHistory();
        h.Add("a");
        h.Add("b");
        h.Add("c");
        Assert.Equal("c", h.NavigateUp());
        Assert.Equal("b", h.NavigateUp());
        Assert.Equal("a", h.NavigateUp());
        Assert.Equal("a", h.NavigateUp());
    }

    [Fact]
    public void NavigateDown_PastEnd_ReturnsEmptyString()
    {
        var h = new InputHistory();
        h.Add("a");
        h.Add("b");
        h.NavigateUp();
        h.NavigateUp();
        Assert.Equal("b", h.NavigateDown());
        Assert.Equal("", h.NavigateDown());
    }

    [Fact]
    public void Add_ExceedsMax_DropsOldest()
    {
        var h = new InputHistory(maxEntries: 3);
        h.Add("a");
        h.Add("b");
        h.Add("c");
        h.Add("d");

        Assert.Equal("d", h.NavigateUp());
        Assert.Equal("c", h.NavigateUp());
        Assert.Equal("b", h.NavigateUp());
        Assert.Equal("b", h.NavigateUp());
    }

    [Fact]
    public void Add_NonConsecutiveDuplicate_OlderInstanceRemoved()
    {
        var h = new InputHistory();
        h.Add("a");
        h.Add("b");
        h.Add("a"); // 再度 "a" — 古い "a" は取り除き末尾に 1 件だけ残す

        Assert.Equal("a", h.NavigateUp());
        Assert.Equal("b", h.NavigateUp());
        // 2 件しかないので更に上には行かない
        Assert.Equal("b", h.NavigateUp());
    }

    [Fact]
    public void ResetCursor_ReturnsToEnd()
    {
        var h = new InputHistory();
        h.Add("a");
        h.Add("b");
        h.NavigateUp();
        h.ResetCursor();
        Assert.Equal("b", h.NavigateUp());
    }

    [Fact]
    public void DefaultMaxEntries_Is500()
    {
        var h = new InputHistory();
        Assert.Equal(500, h.MaxEntries);
        Assert.Equal(500, InputHistory.DefaultMaxEntries);
    }

    [Fact]
    public void SetMaxEntries_ShrinksFromOldest()
    {
        var h = new InputHistory(maxEntries: 10);
        h.Add("a"); h.Add("b"); h.Add("c"); h.Add("d");
        h.SetMaxEntries(2);
        Assert.Equal(2, h.Count);
        Assert.Equal("d", h.NavigateUp());
        Assert.Equal("c", h.NavigateUp());
        Assert.Equal("c", h.NavigateUp()); // 2 件しかない
    }

    [Fact]
    public void SetMaxEntries_FloorIsOne()
    {
        var h = new InputHistory(maxEntries: 5);
        h.Add("a"); h.Add("b");
        h.SetMaxEntries(0); // 0 や負数は 1 にクランプ
        Assert.Equal(1, h.MaxEntries);
        Assert.Equal(1, h.Count);
        Assert.Equal("b", h.NavigateUp());
    }

    [Fact]
    public void LoadFromOldestFirst_RestoresOrder()
    {
        var h = new InputHistory();
        h.LoadFromOldestFirst(new[] { "a", "b", "c" });
        // 末尾から順に navigate up していく
        Assert.Equal("c", h.NavigateUp());
        Assert.Equal("b", h.NavigateUp());
        Assert.Equal("a", h.NavigateUp());
    }

    [Fact]
    public void LoadFromOldestFirst_RespectsMaxEntries_DropsOldest()
    {
        var h = new InputHistory(maxEntries: 2);
        h.LoadFromOldestFirst(new[] { "a", "b", "c", "d" });
        Assert.Equal(2, h.Count);
        Assert.Equal("d", h.NavigateUp());
        Assert.Equal("c", h.NavigateUp());
    }

    [Fact]
    public void LoadFromOldestFirst_FiltersBlank()
    {
        var h = new InputHistory();
        h.LoadFromOldestFirst(new[] { "", "  ", "a", "\t" });
        Assert.Equal(1, h.Count);
        Assert.Equal("a", h.NavigateUp());
    }

    [Fact]
    public void SnapshotOldestFirst_RoundTrips()
    {
        var h1 = new InputHistory();
        h1.Add("a"); h1.Add("b"); h1.Add("c");
        var snapshot = h1.SnapshotOldestFirst();
        var h2 = new InputHistory();
        h2.LoadFromOldestFirst(snapshot);
        Assert.Equal("c", h2.NavigateUp());
        Assert.Equal("b", h2.NavigateUp());
        Assert.Equal("a", h2.NavigateUp());
    }
}
