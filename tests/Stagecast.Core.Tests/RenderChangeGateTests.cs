using Stagecast.Core.Output;
using Stagecast.Core.Rendering;

namespace Stagecast.Core.Tests;

public class RenderChangeGateTests
{
    private static readonly Resolution Wide = new(2520, 1080);
    private static readonly Resolution Hd = new(1920, 1080);

    [Fact]
    public void OutputsOff_AppliesAtOnce()
    {
        var gate = new RenderChangeGate();
        Assert.Equal(ChangeDecision.ApplyNow, gate.Request(new(Hd, null), Wide, 60, outputsOn: false));
        Assert.Null(gate.Pending);
    }

    [Fact]
    public void OutputsOn_WaitsForConfirmation()
    {
        var gate = new RenderChangeGate();
        Assert.Equal(ChangeDecision.Wait, gate.Request(new(Hd, null), Wide, 60, outputsOn: true));
        Assert.Equal(new RenderChange(Hd, null), gate.Pending);
        Assert.Equal(new RenderChange(Hd, null), gate.Confirm());
        Assert.Null(gate.Pending);
    }

    [Fact]
    public void SameAsCurrent_ChangesNothing()
    {
        var gate = new RenderChangeGate();
        Assert.Equal(ChangeDecision.Nothing, gate.Request(new(Wide, 60), Wide, 60, outputsOn: true));
        Assert.Equal(ChangeDecision.Nothing, gate.Request(new(Wide, 60), Wide, 60, outputsOn: false));
        Assert.Null(gate.Pending);
    }

    [Fact]
    public void ResolutionAndFpsWaitTogether()
    {
        var gate = new RenderChangeGate();
        gate.Request(new(Hd, null), Wide, 60, outputsOn: true);
        gate.Request(new(null, 30), Wide, 60, outputsOn: true);
        Assert.Equal(new RenderChange(Hd, 30), gate.Pending);
    }

    [Fact]
    public void AskingAgainReplacesThatPart()
    {
        var gate = new RenderChangeGate();
        gate.Request(new(Hd, 30), Wide, 60, outputsOn: true);
        gate.Request(new(new Resolution(3840, 2160), null), Wide, 60, outputsOn: true);
        Assert.Equal(new RenderChange(new Resolution(3840, 2160), 30), gate.Pending);
    }

    [Fact]
    public void AskingForTheCurrentValueWithdrawsThatPart()
    {
        var gate = new RenderChangeGate();
        gate.Request(new(Hd, 30), Wide, 60, outputsOn: true);
        gate.Request(new(Wide, null), Wide, 60, outputsOn: true);
        Assert.Equal(new RenderChange(null, 30), gate.Pending);
        Assert.Equal(ChangeDecision.Nothing, gate.Request(new(null, 60), Wide, 60, outputsOn: true));
        Assert.Null(gate.Pending);
    }

    [Fact]
    public void Cancel_DropsTheWaitingChange()
    {
        var gate = new RenderChangeGate();
        gate.Request(new(Hd, null), Wide, 60, outputsOn: true);
        gate.Cancel();
        Assert.Null(gate.Pending);
        Assert.Null(gate.Confirm());
    }

    [Fact]
    public void OutputsTurnedOffMeanTheNextRequestAppliesAndClearsWhatWaited()
    {
        var gate = new RenderChangeGate();
        gate.Request(new(Hd, null), Wide, 60, outputsOn: true);
        Assert.Equal(ChangeDecision.ApplyNow, gate.Request(new(null, 30), Wide, 60, outputsOn: false));
        Assert.Null(gate.Pending);
    }
}
