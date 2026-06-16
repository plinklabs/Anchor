using FocusAgent.Core.Diagnostics;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Unit coverage for the #248 crash-dialog text builder. These lock the curation
/// decisions the issue calls for — the default detail field is friendly-but-useful
/// (type + message + inner-exception <em>message</em> chain, version, log path) and
/// stays free of stack frames, while the full stack is available separately for the
/// expander. The dialog's rendering is covered by the visual e2e
/// (CrashDialogVisualTests); this covers the text those surfaces show.
/// </summary>
public class CrashDiagnosticsTests
{
    private static Exception ThrownWithStack(Exception ex)
    {
        try { throw ex; }
        catch (Exception caught) { return caught; }
    }

    [Fact]
    public void BuildDetail_IncludesTypeMessageVersionAndLogPath()
    {
        var ex = new InvalidOperationException("Backend:BaseUrl is empty.");

        var detail = CrashDiagnostics.BuildDetail(ex, "1.4.2", @"C:\logs\startup-error.log");

        Assert.Contains("Anchor agent v1.4.2", detail);
        Assert.Contains("System.InvalidOperationException: Backend:BaseUrl is empty.", detail);
        Assert.Contains(@"Log file: C:\logs\startup-error.log", detail);
    }

    [Fact]
    public void BuildDetail_WalksTheInnerExceptionMessageChain()
    {
        var ex = new InvalidOperationException(
            "Could not build the hub connection.",
            new UriFormatException("Invalid URI: The URI is empty."));

        var detail = CrashDiagnostics.BuildDetail(ex, "1.0.0", "log.txt");

        Assert.Contains("System.InvalidOperationException: Could not build the hub connection.", detail);
        // The inner exception is shown, indented under its outer cause.
        Assert.Contains("-> System.UriFormatException: Invalid URI: The URI is empty.", detail);
    }

    [Fact]
    public void BuildDetail_DoesNotIncludeStackFrames()
    {
        // A real, thrown exception carries an "at ..." stack; the curated default
        // field must not show it (that lives behind the expander via BuildFullStack).
        var ex = ThrownWithStack(new InvalidOperationException("boom"));

        var detail = CrashDiagnostics.BuildDetail(ex, "1.0.0", "log.txt");

        Assert.DoesNotContain("   at ", detail);
    }

    [Fact]
    public void BuildDetail_FlattensMultiLineMessagesToOneLinePerCause()
    {
        var ex = new InvalidOperationException("line one\r\nline two\nline three");

        var detail = CrashDiagnostics.BuildDetail(ex, null, "log.txt");

        Assert.Contains("line one line two line three", detail);
        // The flattened message stays on a single line.
        Assert.DoesNotContain("line one\r\nline two", detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildDetail_OmitsVersionWhenUnavailable(string? version)
    {
        var detail = CrashDiagnostics.BuildDetail(
            new InvalidOperationException("x"), version, "log.txt");

        Assert.StartsWith("Anchor agent" + Environment.NewLine, detail);
        Assert.DoesNotContain("Anchor agent v", detail);
    }

    [Fact]
    public void BuildDetail_FallsBackWhenLogPathMissing()
    {
        var detail = CrashDiagnostics.BuildDetail(
            new InvalidOperationException("x"), "1.0.0", null);

        Assert.Contains("Log file: (unavailable)", detail);
    }

    [Fact]
    public void BuildFullStack_IncludesTheFullExceptionText()
    {
        var ex = ThrownWithStack(new InvalidOperationException(
            "outer", new UriFormatException("inner")));

        var stack = CrashDiagnostics.BuildFullStack(ex);

        Assert.Contains("System.InvalidOperationException: outer", stack);
        // The full text carries the inner exception and the captured stack frames
        // the curated detail deliberately omits.
        Assert.Contains("System.UriFormatException: inner", stack);
        Assert.Contains("   at ", stack);
    }
}
