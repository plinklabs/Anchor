using System.Text.Json;
using FocusAgent.Core.Focus;

namespace FocusAgent.Core.Tests;

public class ForegroundChangeReportTests
{
    private static readonly ForegroundChange TeamsChat = new(
        new AppInfo(
            "ms-teams",
            @"C:\Users\alice.peeters\AppData\Local\Microsoft\Teams\ms-teams.exe",
            "Microsoft Corporation"),
        WindowTitle: "Chat with Bram De Smet | Microsoft Teams",
        ProcessId: 4242,
        WindowHandle: 0x1234);

    [Fact]
    public void Reports_the_app_its_publisher_and_whether_it_was_blocked()
    {
        using var payload = JsonDocument.Parse(ForegroundChangeReport.ToPayloadJson(TeamsChat, blocked: true));

        Assert.Equal("ms-teams", payload.RootElement.GetProperty("processName").GetString());
        Assert.Equal("Microsoft Corporation", payload.RootElement.GetProperty("publisher").GetString());
        Assert.True(payload.RootElement.GetProperty("blocked").GetBoolean());
    }

    [Fact]
    public void Leaves_out_the_window_title_and_the_executable_path()
    {
        // #345: titles and paths carry personal data (chat partners, document
        // names, the Windows user name) that nothing on the backend uses.
        var json = ForegroundChangeReport.ToPayloadJson(TeamsChat, blocked: false);
        using var payload = JsonDocument.Parse(json);

        Assert.Equal(
            new[] { "processName", "publisher", "blocked" },
            payload.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.DoesNotContain("Bram", json);
        Assert.DoesNotContain("alice.peeters", json);
    }

    [Fact]
    public void A_dialog_another_process_shows_for_an_app_is_reported_as_that_app()
    {
        // #428: the file picker PickerHost opens for an Edge upload is Edge to the
        // teacher, not a Windows process name.
        var picker = new ForegroundChange(
            new AppInfo("PickerHost", @"C:\Windows\System32\PickerHost.exe", "Microsoft Windows"),
            WindowTitle: "Open",
            ProcessId: 7036,
            WindowHandle: 0x700,
            Owner: new ForegroundOwner(
                new AppInfo("msedge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Microsoft Corporation"),
                ProcessId: 5151,
                WindowHandle: 0x300));

        using var payload = JsonDocument.Parse(ForegroundChangeReport.ToPayloadJson(picker, blocked: false));

        Assert.Equal("msedge", payload.RootElement.GetProperty("processName").GetString());
        Assert.Equal("Microsoft Corporation", payload.RootElement.GetProperty("publisher").GetString());
        Assert.False(payload.RootElement.GetProperty("blocked").GetBoolean());
    }
}
