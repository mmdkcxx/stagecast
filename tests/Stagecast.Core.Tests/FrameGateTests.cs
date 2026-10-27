using Stagecast.Core.Output;

namespace Stagecast.Core.Tests;

public class FrameGateTests
{
    private static readonly string Black = "black";

    [Fact]
    public void Live_PassesEveryFrameThrough()
    {
        var gate = new FrameGate<string>(Black);
        Assert.Equal("f1", gate.Select("f1"));
        Assert.Equal("f2", gate.Select("f2"));
    }

    [Fact]
    public void Freeze_RepeatsTheLastLiveFrame()
    {
        var gate = new FrameGate<string>(Black);
        gate.Select("f1");
        gate.Mode = GateMode.Freeze;
        Assert.Equal("f1", gate.Select("f2"));
        Assert.Equal("f1", gate.Select("f3"));
    }

    [Fact]
    public void ReleasingFreeze_ShowsTheNewestFrameImmediately()
    {
        var gate = new FrameGate<string>(Black);
        gate.Select("f1");
        gate.Mode = GateMode.Freeze;
        gate.Select("f2");
        gate.Mode = GateMode.Live;
        Assert.Equal("f3", gate.Select("f3"));
    }

    [Fact]
    public void Freeze_BeforeAnyLiveFrame_HasNothingToShow()
    {
        var gate = new FrameGate<string>(Black) { Mode = GateMode.Freeze };
        Assert.Null(gate.Select("f1"));
    }

    [Fact]
    public void Blackout_SendsBlackAndDoesNotReplaceTheHeldFrame()
    {
        var gate = new FrameGate<string>(Black);
        gate.Select("f1");
        gate.Mode = GateMode.Blackout;
        Assert.Equal(Black, gate.Select("f2"));
        gate.Mode = GateMode.Freeze;
        Assert.Equal("f1", gate.Select("f3"));
    }
}
