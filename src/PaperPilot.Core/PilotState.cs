namespace PaperPilot.Core;

/// <summary>The five states from the spec (Section 7.1).</summary>
public enum PilotState
{
    /// <summary>Tool toggled off: pill hidden, mic released.</summary>
    Off,
    /// <summary>Locked on; will scroll.</summary>
    Following,
    /// <summary>Listening but won't move (stories, after manual scroll, searching, just activated).</summary>
    Holding,
    /// <summary>Listening paused by hotkey.</summary>
    Paused,
    /// <summary>Something is broken; scroll manually.</summary>
    Problem,
}
