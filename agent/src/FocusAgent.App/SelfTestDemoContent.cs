namespace FocusAgent.App;

/// <summary>
/// Fixed, presentable demo content shared by the agent's <c>--show-test-*</c>
/// self-test surfaces (#251). The self-tests double as the source for the
/// student-facing website screenshots, so the strings they render must be clean
/// and recognisable — a real-looking teacher name and class code, never a debug
/// string like "Self-Test Teacher" or "CONNECTED — SELF-TEST".
///
/// The values deliberately match the teacher dashboard's demo data
/// (<c>dashboard/lib/demo/demo_data.dart</c> — "Ms Rivera", class "3B",
/// <c>PLINK-3B</c>) so the agent and dashboard shots on the website tell one
/// coherent story: the same teacher, the same class, seen from both sides.
/// Centralised here so every surface stays in step and a future copy change is a
/// single edit.
/// </summary>
internal static class SelfTestDemoContent
{
    /// <summary>The teacher who started the focus session (join toast).</summary>
    public const string TeacherName = "Ms Rivera";

    /// <summary>The class join code (join toast / join-by-code).</summary>
    public const string JoinCode = "PLINK-3B";

    /// <summary>The signed-in student shown on the agent main window / tray.</summary>
    public const string StudentName = "Ada Lovelace";

    /// <summary>The tray status eyebrow's representative "connected" text.</summary>
    public const string TrayStatus = "CONNECTED";

    /// <summary>The off-list app whose foreground triggers the block overlay.</summary>
    public const string BlockedAppName = "notepad";
}
