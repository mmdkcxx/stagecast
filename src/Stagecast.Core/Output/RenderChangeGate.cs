using Stagecast.Core.Rendering;

namespace Stagecast.Core.Output;

/// <summary>A change to the render size and/or rate. A null part is left as it is.</summary>
public sealed record RenderChange(Resolution? Resolution, int? Fps);

public enum ChangeDecision
{
    /// <summary>The change would not alter anything.</summary>
    Nothing,

    /// <summary>No output is on: the caller applies it now.</summary>
    ApplyNow,

    /// <summary>An output is on: it waits until <see cref="RenderChangeGate.Confirm"/> or <see cref="RenderChangeGate.Cancel"/>.</summary>
    Wait,
}

/// <summary>
/// Changing the resolution or frame rate while an output is on can cut that output for a few seconds in front of the audience,
/// so then the change only waits until the operator confirms. With every output off it applies at once. Asking again while a
/// change waits replaces the parts it names and keeps the others.
/// </summary>
public sealed class RenderChangeGate
{
    public RenderChange? Pending { get; private set; }

    /// <param name="currentResolution">What is rendered now.</param>
    /// <param name="currentFps">The rate used now.</param>
    /// <param name="outputsOn">True when the screen, NDI or OMT output is on.</param>
    public ChangeDecision Request(RenderChange change, Resolution currentResolution, int currentFps, bool outputsOn)
    {
        var effective = new RenderChange(
            change.Resolution is { } r && r != currentResolution ? r : null,
            change.Fps is { } f && f != currentFps ? f : null);

        if (!outputsOn)
        {
            Pending = null;
            return effective.Resolution is null && effective.Fps is null ? ChangeDecision.Nothing : ChangeDecision.ApplyNow;
        }

        // Output is on. Keep what already waits, replace the parts that were asked again. Asking for the current value withdraws it.
        var merged = new RenderChange(
            change.Resolution is null ? Pending?.Resolution : effective.Resolution,
            change.Fps is null ? Pending?.Fps : effective.Fps);
        Pending = merged.Resolution is null && merged.Fps is null ? null : merged;
        return Pending is null ? ChangeDecision.Nothing : ChangeDecision.Wait;
    }

    /// <summary>Returns the waiting change for the caller to apply, and clears it.</summary>
    public RenderChange? Confirm()
    {
        var change = Pending;
        Pending = null;
        return change;
    }

    public void Cancel() => Pending = null;
}
