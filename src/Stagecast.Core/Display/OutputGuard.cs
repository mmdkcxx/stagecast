namespace Stagecast.Core.Display;

public enum OutputWarning
{
    None,

    /// <summary>Only one monitor exists: an always-on-top output would cover the control window.</summary>
    OnlyOneMonitor,

    /// <summary>The output monitor is the one the control window is on.</summary>
    SameAsControl,
}

public static class OutputGuard
{
    /// <summary>The monitor with this id, or null (it was unplugged, or nothing was chosen yet).</summary>
    public static MonitorInfo? Find(IReadOnlyList<MonitorInfo> monitors, string? id)
        => id is null ? null : monitors.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first monitor that is not the control monitor, else the control monitor itself. Used when nothing valid was saved.</summary>
    public static MonitorInfo? Default(IReadOnlyList<MonitorInfo> monitors, MonitorInfo? control)
        => monitors.FirstOrDefault(m => control is null || m.Id != control.Id) ?? monitors.FirstOrDefault();

    /// <summary>What to warn about before the output is shown on <paramref name="target"/> while the operator works on <paramref name="control"/>.</summary>
    public static OutputWarning Check(IReadOnlyList<MonitorInfo> monitors, MonitorInfo target, MonitorInfo? control)
    {
        if (monitors.Count <= 1) return OutputWarning.OnlyOneMonitor;
        return control is not null && control.Id == target.Id ? OutputWarning.SameAsControl : OutputWarning.None;
    }

    public static string Message(OutputWarning warning) => warning switch
    {
        OutputWarning.OnlyOneMonitor => "Hanya ada satu monitor. Output layar penuh akan menutupi jendela kontrol ini, dan Anda tidak bisa mematikannya dengan mouse. Matikan dengan pintasan Ctrl+Alt+H. Tetap nyalakan?",
        OutputWarning.SameAsControl => "Monitor output sama dengan monitor tempat jendela kontrol ini berada, jadi output akan menutupinya. Matikan dengan pintasan Ctrl+Alt+H. Tetap nyalakan?",
        _ => string.Empty,
    };
}
