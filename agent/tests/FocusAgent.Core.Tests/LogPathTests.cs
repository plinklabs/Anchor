using FocusAgent.Core.Logging;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #596: the log upload writes the student's own folders as their variables
/// wherever they stand in it, not only in the paths a log line passes through
/// <see cref="LogPath.Redact"/>: in an exception's message, at the end of a
/// line, followed by a /, in another case, or quoted the way Velopack's log
/// quotes them. A folder that only starts with the same letters is left alone.
/// </summary>
public sealed class LogPathTests
{
    // Jan's folders, %USERPROFILE% first: the longest root has to win anyway.
    private static readonly (string Name, string Folder)[] Jan =
    [
        ("%USERPROFILE%", @"C:\Users\jan"),
        ("%APPDATA%", @"C:\Users\jan\AppData\Roaming"),
        ("%LOCALAPPDATA%", @"C:\Users\jan\AppData\Local\"),
    ];

    [Theory]
    // In the middle of a line, as an exception's message carries it.
    [InlineData(
        @"Witness host registration (startup) failed: Access to the path 'C:\Users\jan\AppData\Local\Anchor.Agent\witness\manifest.json' is denied.",
        @"Witness host registration (startup) failed: Access to the path '%LOCALAPPDATA%\Anchor.Agent\witness\manifest.json' is denied.")]
    // At the end of a line, with and without a separator after it.
    [InlineData(@"Profile folder: C:\Users\jan", @"Profile folder: %USERPROFILE%")]
    [InlineData(@"Roaming folder: C:\Users\jan\AppData\Roaming\", @"Roaming folder: %APPDATA%\")]
    // Followed by a /, and written with / throughout.
    [InlineData(
        @"Searching for shortcuts in: ShortcutLocationFlags(STARTUP) (C:\Users\jan\AppData\Roaming/*.lnk)",
        @"Searching for shortcuts in: ShortcutLocationFlags(STARTUP) (%APPDATA%/*.lnk)")]
    [InlineData(@"Opened file:///C:/Users/jan/AppData/Local/Anchor/page.html", @"Opened file:///%LOCALAPPDATA%/Anchor/page.html")]
    // Quoted the way Velopack's log quotes a path: every separator doubled.
    [InlineData(
        @"[update:3328] [09:47:47] [INFO]     Location: ""C:\\Users\\jan\\AppData\\Local\\Anchor.Agent\\Update.exe""",
        @"[update:3328] [09:47:47] [INFO]     Location: ""%LOCALAPPDATA%\\Anchor.Agent\\Update.exe""")]
    // In another case.
    [InlineData(@"c:\users\JAN\appdata\LOCAL\Anchor\FocusAgent\logs", @"%LOCALAPPDATA%\Anchor\FocusAgent\logs")]
    // A full stop after it ends the path.
    [InlineData(@"Could not find C:\Users\jan.", @"Could not find %USERPROFILE%.")]
    // A space is part of a folder name after the root.
    [InlineData(
        @"(C:\Users\jan\OneDrive - VZW Arcadia\Bureaublad/*.lnk)",
        @"(%USERPROFILE%\OneDrive - VZW Arcadia\Bureaublad/*.lnk)")]
    // Every occurrence, on every line.
    [InlineData(
        "IOException: C:\\Users\\jan\\AppData\\Local\\a is in use\n   at Copy(C:\\Users\\jan\\AppData\\Roaming\\b, C:\\Users\\jan\\c)",
        "IOException: %LOCALAPPDATA%\\a is in use\n   at Copy(%APPDATA%\\b, %USERPROFILE%\\c)")]
    // LocalLow is not Local: only the profile is Jan's there.
    [InlineData(@"C:\Users\jan\AppData\LocalLow\x", @"%USERPROFILE%\AppData\LocalLow\x")]
    public void Writes_the_students_folders_as_their_variables_wherever_they_stand(string text, string expected)
    {
        Assert.Equal(expected, LogPath.RedactText(text, Jan));
    }

    [Theory]
    // Someone else's folder that only starts with the same letters.
    [InlineData(@"C:\Users\janssens\AppData\Local\Anchor")]
    [InlineData(@"C:\Users\jan.peeters\Documents")]
    [InlineData(@"C:\Users\jan-2\Documents")]
    [InlineData(@"C:\Users\jan_old")]
    // Not under Jan's folders at all.
    [InlineData(@"C:\Users\Public\Desktop\Anchor.lnk")]
    [InlineData(@"C:\Program Files\Anchor\anchor-witness-host.exe")]
    [InlineData(@"HKCU\Software\Microsoft\Edge\NativeMessagingHosts\net.anchor.witness")]
    public void Leaves_alone_what_is_not_the_students_folder(string text)
    {
        Assert.Equal(text, LogPath.RedactText(text, Jan));
    }

    [Fact]
    public void A_user_name_is_matched_as_written_not_as_a_pattern()
    {
        (string, string)[] roots = [("%USERPROFILE%", @"C:\Users\j.peeters (2)")];

        Assert.Equal(@"%USERPROFILE%\Documents", LogPath.RedactText(@"C:\Users\j.peeters (2)\Documents", roots));
        Assert.Equal(@"C:\Users\jxpeeters (2)\Documents", LogPath.RedactText(@"C:\Users\jxpeeters (2)\Documents", roots));
    }

    [Fact]
    public void A_folder_Windows_has_no_path_for_is_skipped()
    {
        (string, string)[] roots = [("%LOCALAPPDATA%", ""), ("%USERPROFILE%", @"C:\Users\jan")];

        Assert.Equal(@"%USERPROFILE%\x", LogPath.RedactText(@"C:\Users\jan\x", roots));
    }
}
