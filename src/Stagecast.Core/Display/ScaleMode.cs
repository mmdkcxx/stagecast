namespace Stagecast.Core.Display;

/// <summary>How the render surface is mapped onto the output monitor. Applies to the screen output only; NDI and OMT always send the native render size.</summary>
public enum ScaleMode
{
    /// <summary>Anamorphic: the whole render rectangle is stretched onto the monitor, then undone downstream (Resolume/vMix).</summary>
    Squeeze,

    /// <summary>Uniform scale that keeps the aspect ratio; unused monitor area stays black.</summary>
    Fit,

    /// <summary>One render pixel per monitor pixel; anything beyond the monitor is cropped.</summary>
    OneToOne,
}
