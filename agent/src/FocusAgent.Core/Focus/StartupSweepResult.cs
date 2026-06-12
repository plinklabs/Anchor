namespace FocusAgent.Core.Focus;

/// <summary>
/// Outcome of the session-start window sweep (#104): how many top-level windows
/// were examined and the process names of the off-list ones that were minimized.
/// Surfaced via the dev <c>/status</c> endpoint so the headless e2e can assert
/// the sweep actually ran and brought down the right windows, rather than
/// guessing from screenshots.
/// </summary>
public sealed record StartupSweepResult(int WindowsExamined, IReadOnlyList<string> MinimizedProcesses);
