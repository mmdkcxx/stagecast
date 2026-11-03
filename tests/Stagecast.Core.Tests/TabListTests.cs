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
    public void MayHaveNoTab_ThenTheFirstOneAddedIsActive()
    {
        var list = new TabList([], 5);
        Assert.Empty(list.Items);
        Assert.Null(list.Active);
        Assert.Null(list.Program);
        Assert.False(list.CanClose);
        var first = list.Add();
        Assert.Equal(first, list.Active);
        Assert.Equal(0, list.ActiveIndex);
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
        var active = list.Active!;
        list.Close(list.Items[0].Id);
        Assert.Equal(active, list.Active);
        list.Close(list.Items[^1].Id);
        Assert.Equal(active, list.Active);
    }

    [Fact]
    public void Close_AllowsTheLastTab_AndLeavesNoActiveTab()
    {
        var list = WithTabs(1);
        Assert.True(list.CanCloseTab(list.Items[0].Id));
        Assert.Null(list.Close(list.Items[0].Id));
        Assert.Empty(list.Items);
        Assert.Null(list.Active);
        Assert.Null(list.Close(Guid.NewGuid())); // an unknown tab changes nothing
    }

    [Fact]
    public void Move_KeepsTheActiveTabActive_AndStopsAtTheEnds()
    {
        var list = WithTabs(3, active: 1);
        var active = list.Active!;
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
        var active = list.Active!;
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

public class TabPreviewTakeTests
{
    private static TabList WithTabs(int count, int active = 0)
    {
        var list = new TabList([new TabInfo(Guid.NewGuid(), "Tab 1", TabRules.BlankUrl)], 0);
        for (int i = 1; i < count; i++) list.Add();
        list.Select(list.Items[active].Id);
        return list;
    }

    [Fact]
    public void DirectMode_IsTheDefault_AndTheActiveTabIsOnAir()
    {
        var list = WithTabs(3, active: 1);
        Assert.False(list.PreviewMode);
        Assert.Null(list.ProgramId);
        Assert.Equal(list.Active, list.Program);
        Assert.Equal(1, list.LiveIndex);
    }

    [Fact]
    public void EnterPreviewMode_KeepsTheActiveTabOnAir_AndPreviewsTheNextOne()
    {
        var list = WithTabs(3, active: 1);
        var onAir = list.Active!;
        Assert.True(list.EnterPreviewMode());
        Assert.Equal(onAir, list.Program);
        Assert.Equal(2, list.ActiveIndex);
        Assert.Equal(1, list.LiveIndex);
    }

    [Fact]
    public void EnterPreviewMode_WrapsAroundFromTheLastTab()
    {
        var list = WithTabs(3, active: 2);
        list.EnterPreviewMode();
        Assert.Equal(0, list.ActiveIndex);
    }

    [Fact]
    public void EnterPreviewMode_NeedsTwoTabs()
    {
        var list = WithTabs(1);
        Assert.False(list.CanEnterPreviewMode);
        Assert.False(list.EnterPreviewMode());
        Assert.False(list.PreviewMode);
    }

    [Fact]
    public void Select_CannotPickTheTabOnAir_ButCanPickOthers()
    {
        var list = WithTabs(3);
        list.EnterPreviewMode();
        var onAir = list.Program!;
        Assert.False(list.Select(onAir.Id));
        Assert.Equal(1, list.ActiveIndex);
        Assert.True(list.Select(list.Items[2].Id));
        Assert.Equal(onAir, list.Program);
    }

    [Fact]
    public void Take_SwapsPreviewAndProgram()
    {
        var list = WithTabs(3);
        list.EnterPreviewMode();
        var wasProgram = list.Program!;
        var wasPreview = list.Active!;
        Assert.True(list.Take());
        Assert.Equal(wasPreview, list.Program);
        Assert.Equal(wasProgram, list.Active);
        Assert.True(list.Take()); // and back
        Assert.Equal(wasProgram, list.Program);
    }

    [Fact]
    public void Take_DoesNothingInDirectMode()
    {
        var list = WithTabs(2);
        Assert.False(list.Take());
    }

    [Fact]
    public void ExitPreviewMode_LeavesTheTabOnAirOnAir()
    {
        var list = WithTabs(3);
        list.EnterPreviewMode();
        list.Select(list.Items[2].Id);
        var onAir = list.Program!;
        Assert.True(list.ExitPreviewMode());
        Assert.Equal(onAir, list.Active);
        Assert.False(list.PreviewMode);
    }

    [Fact]
    public void ClosingTheTabOnAir_EndsPreviewMode_AndKeepsThePreviewSelected()
    {
        var list = WithTabs(3);
        list.EnterPreviewMode();
        var preview = list.Active!;
        Assert.True(list.CanCloseTab(list.Program!.Id));
        Assert.Equal(preview, list.Close(list.Program!.Id));
        Assert.Equal(2, list.Items.Count);
        Assert.False(list.PreviewMode);
        Assert.Equal(preview, list.Active);
    }

    [Fact]
    public void ClosingThePreview_NeverMakesTheProgramThePreview()
    {
        var list = WithTabs(3, active: 0);
        list.EnterPreviewMode();            // program Tab 1, preview Tab 2
        var program = list.Program!;
        list.Close(list.Active!.Id);         // closes Tab 2; the neighbour is Tab 3
        Assert.Equal(program, list.Program);
        Assert.NotEqual(list.Program!.Id, list.Active!.Id);
        Assert.True(list.PreviewMode);

        list.Close(list.Active!.Id);         // only the program is left
        Assert.False(list.PreviewMode);
        Assert.Equal(program, list.Active);
    }

    [Fact]
    public void ClosingAtTheEnd_PicksTheTabBeforeTheProgram()
    {
        var list = WithTabs(3, active: 1);
        list.EnterPreviewMode();            // program Tab 2, preview Tab 3
        list.Close(list.Active!.Id);         // Tab 3 closed: the only other tab is Tab 1
        Assert.Equal("Tab 1", list.Active!.Name);
        Assert.Equal("Tab 2", list.Program!.Name);
    }

    [Fact]
    public void MovingTabs_KeepsProgramAndPreview()
    {
        var list = WithTabs(3);
        list.EnterPreviewMode();
        var (program, preview) = (list.Program!, list.Active!);
        list.Move(program.Id, +1);
        list.Move(preview.Id, -1);
        Assert.Equal(program, list.Program);
        Assert.Equal(preview, list.Active);
    }

    [Fact]
    public void AddDocument_KeepsKindAndSource_AndCountsTowardsTheLimit()
    {
        var list = WithTabs(1);
        var pdf = list.AddDocument(TabKind.Pdf, "  D:\\Acara\\Gala.pdf ");
        Assert.NotNull(pdf);
        Assert.Equal(TabKind.Pdf, pdf.Kind);
        Assert.Equal("D:\\Acara\\Gala.pdf", pdf.Source);
        Assert.Equal(TabRules.BlankUrl, pdf.Url);
        Assert.Equal("Tab 2", pdf.Name);
        for (int i = list.Items.Count; i < TabRules.MaxTabs; i++) Assert.NotNull(list.AddDocument(TabKind.PowerPoint, null));
        Assert.Null(list.AddDocument(TabKind.Video, null));
        Assert.Null(list.Add());
    }

    [Fact]
    public void AddDocument_RefusesAWebTab_AndKeepsAnEmptyPathAsNull()
    {
        var list = WithTabs(1);
        Assert.Null(list.AddDocument(TabKind.Web, "x"));
        Assert.Null(list.AddDocument(TabKind.Pdf, "   ")!.Source);
        Assert.Null(list.AddDocument(TabKind.Pdf, new string('x', TabRules.MaxSourceLength + 1))!.Source);
    }

    [Fact]
    public void FileTabs_HaveNoAddressAndNoSeparateProfile()
    {
        var list = WithTabs(1);
        var pdf = list.AddDocument(TabKind.Pdf, "a.pdf")!;
        list.SetUrl(pdf.Id, "https://example.com");
        Assert.Equal(TabRules.BlankUrl, list.Items[1].Url);
        Assert.Null(list.SetSeparateProfile(pdf.Id, separate: true));
        Assert.Null(list.Items[1].ProfileId);
    }

    [Fact]
    public void SetSource_ChangesFileTabsOnly()
    {
        var list = WithTabs(1);
        var pdf = list.AddDocument(TabKind.Pdf, "a.pdf")!;
        list.SetSource(pdf.Id, "b.pdf");
        Assert.Equal("b.pdf", list.Items[1].Source);
        list.SetSource(list.Items[0].Id, "c.pdf");
        Assert.Null(list.Items[0].Source);
    }

    [Fact]
    public void TabKinds_NamesRoundTrip_AndUnknownNamesAreReported()
    {
        foreach (var kind in Enum.GetValues<TabKind>())
        {
            Assert.True(TabKinds.TryParse(TabKinds.ConfigName(kind), out var back));
            Assert.Equal(kind, back);
        }
        Assert.True(TabKinds.TryParse(null, out var none));
        Assert.Equal(TabKind.Web, none);
        Assert.True(TabKinds.TryParse(" PDF ", out var pdf));
        Assert.Equal(TabKind.Pdf, pdf);
        Assert.False(TabKinds.TryParse("hologram", out var unknown));
        Assert.Equal(TabKind.Web, unknown);
    }
}
