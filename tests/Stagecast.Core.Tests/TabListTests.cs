using Stagecast.Core.Tabs;

namespace Stagecast.Core.Tests;

public class TabListTests
{
    private static TabList WithTabs(int count, int active = 0)
    {
        var list = new TabList([new TabInfo(Guid.NewGuid(), "Tab 1", TabRules.BlankUrl)], 0);
        for (int i = 1; i < count; i++) list.Add();
        list.Select(list.Items[active].Id);
        return list;
    }

    [Fact]
    public void AlwaysHasATab()
    {
        var list = new TabList([], 5);
        Assert.Single(list.Items);
        Assert.Equal(0, list.ActiveIndex);
        Assert.False(list.CanClose);
    }

    [Fact]
    public void Add_NamesTabsInOrder_AndStopsAtTheLimit()
    {
        var list = WithTabs(1);
        Assert.Equal("Tab 2", list.Add()!.Name);
        for (int i = list.Items.Count; i < TabRules.MaxTabs; i++) Assert.NotNull(list.Add());
        Assert.Null(list.Add());
        Assert.Equal(TabRules.MaxTabs, list.Items.Count);
    }

    [Fact]
    public void DefaultName_ReusesAFreedNumber()
    {
        var list = WithTabs(3);
        list.Close(list.Items[1].Id); // "Tab 2" goes away
        Assert.Equal("Tab 2", list.Add()!.Name);
    }

    [Theory]
    [InlineData("", TabNameError.Empty)]
    [InlineData("   ", TabNameError.Empty)]
    [InlineData(null, TabNameError.Empty)]
    [InlineData("Kuis Kahoot", TabNameError.None)]
    public void Rename_ChecksTheName(string? name, TabNameError expected)
    {
        var list = WithTabs(2);
        Assert.Equal(expected, list.Rename(list.Items[1].Id, name));
        Assert.Equal(expected == TabNameError.None ? name!.Trim() : "Tab 2", list.Items[1].Name);
    }

    [Fact]
    public void Rename_AllowsExactlyTheMaximumLength()
    {
        var list = WithTabs(1);
        Assert.Equal(TabNameError.None, list.Rename(list.Items[0].Id, new string('a', TabRules.MaxNameLength)));
        Assert.Equal(TabNameError.TooLong, list.Rename(list.Items[0].Id, new string('a', TabRules.MaxNameLength + 1)));
        Assert.Equal(TabNameError.None, list.Rename(list.Items[0].Id, "  " + new string('b', TabRules.MaxNameLength) + "  ")); // spaces do not count
    }

    [Fact]
    public void Close_ActiveTab_ActivatesTheNextOne_OrThePreviousAtTheEnd()
    {
        var list = WithTabs(3, active: 1);
        Assert.Equal(list.Items[2].Id, list.Close(list.Items[1].Id)!.Id);
        Assert.Equal(1, list.ActiveIndex);
        Assert.Equal(list.Items[0].Id, list.Close(list.Items[1].Id)!.Id); // it was last: step back
        Assert.Equal(0, list.ActiveIndex);
    }

    [Fact]
    public void Close_ATabBeforeTheActiveOne_KeepsTheSameTabActive()
    {
        var list = WithTabs(4, active: 2);
        var active = list.Active;
        list.Close(list.Items[0].Id);
        Assert.Equal(active, list.Active);
        list.Close(list.Items[^1].Id);
        Assert.Equal(active, list.Active);
    }

    [Fact]
    public void Close_RefusesTheLastTab()
    {
        var list = WithTabs(1);
        Assert.Null(list.Close(list.Items[0].Id));
        Assert.Single(list.Items);
    }

    [Fact]
    public void Move_KeepsTheActiveTabActive_AndStopsAtTheEnds()
    {
        var list = WithTabs(3, active: 1);
        var active = list.Active;
        Assert.True(list.Move(active.Id, -1));
        Assert.Equal(active, list.Active);
        Assert.Equal(0, list.ActiveIndex);
        Assert.False(list.Move(active.Id, -1));
        Assert.True(list.Move(list.Items[1].Id, +1)); // moves "Tab 1" behind "Tab 3"? it was at 1 after the first move
        Assert.Equal(active, list.Active);
    }

    [Fact]
    public void Move_OfAnotherTab_UpdatesTheActiveIndex()
    {
        var list = WithTabs(3, active: 0);
        var active = list.Active;
        list.Move(list.Items[2].Id, -2); // the last tab jumps in front of the active one
        Assert.Equal(active, list.Active);
        Assert.Equal(1, list.ActiveIndex);
    }

    [Fact]
    public void SetUrl_StoresRestorableAddressesOnly()
    {
        var list = WithTabs(1);
        var id = list.Items[0].Id;
        list.SetUrl(id, "https://www.canva.com/design/x");
        Assert.Equal("https://www.canva.com/design/x", list.Items[0].Url);
        list.SetUrl(id, "data:text/html,<body>hello</body>");
        Assert.Equal(TabRules.BlankUrl, list.Items[0].Url);
        list.SetUrl(id, null);
        Assert.Equal(TabRules.BlankUrl, list.Items[0].Url);
    }

    [Fact]
    public void Select_UnknownTab_IsRefused()
    {
        var list = WithTabs(2);
        Assert.False(list.Select(Guid.NewGuid()));
        Assert.Equal(0, list.ActiveIndex);
    }
}

public class TabProfileTests
{
    [Fact]
    public void SeparateProfile_GetsAnIdOnce_AndGoesBack()
    {
        var list = new TabList([new TabInfo(Guid.NewGuid(), "Tab 1", TabRules.BlankUrl)], 0);
        var id = list.Items[0].Id;
        Assert.Null(list.SetSeparateProfile(id, true));
        string profile = list.Items[0].ProfileId!;
        Assert.True(TabRules.IsValidProfileId(profile));
        Assert.Equal(profile, list.SetSeparateProfile(id, true)); // switching on again keeps the same profile
        Assert.Equal(profile, list.Items[0].ProfileId);
        Assert.Equal(profile, list.SetSeparateProfile(id, false));
        Assert.Null(list.Items[0].ProfileId);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("..\\..\\windows", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    public void ProfileIds_AreOnlyTheFormWeMake(string? id, bool valid) => Assert.Equal(valid, TabRules.IsValidProfileId(id));
}
