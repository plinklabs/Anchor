using System.Diagnostics;
using System.Runtime.Versioning;
using FocusAgent.Core.Tamper;
using FocusAgent.Native;
using Microsoft.Win32;

namespace FocusAgent.Native.Tests;

/// <summary>
/// #526 (test phase only, #515): reading Edge's address bar through UI Automation.
/// Against the Edge installed on the machine, in a profile of its own and in the
/// background: the address bar is read from the window, whichever app is in
/// front, so this catches an Edge update that changes it without depending on
/// the foreground. The agent reading the foreground window in a session is the
/// integration suite's <c>AddressBarWitnessTests</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EdgeAddressBarReaderTests
{
    [Fact]
    public void A_window_without_an_address_bar_reads_as_nothing()
    {
        using var window = TestWindow.Open("Anchor address bar test");
        using var reader = new EdgeAddressBarReader(new AppIdentifier());

        Assert.Null(reader.Read(window.Handle));
        Assert.Null(reader.Read(IntPtr.Zero));
        Assert.Equal(IntPtr.Zero, reader.ForegroundWindowOf("no-such-process-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task Reads_what_Edge_s_address_bar_shows_for_a_web_page_and_for_the_block_page()
    {
        await using var edge = await IsolatedEdge.StartAsync("http://offlist.test:9/secret/path?q=private");
        using var reader = new EdgeAddressBarReader(new AppIdentifier());

        // An http page: Edge leaves out the scheme (the page itself doesn't load).
        var page = await edge.ReadUntilAsync(reader, r => r.Text.StartsWith("offlist.test", StringComparison.Ordinal));
        Assert.Equal("offlist.test:9/secret/path?q=private", page.Text);
        Assert.False(page.HasFocus);
        Assert.Equal("offlist.test", AddressBarHost.Of(page.Text));

        // The block page, as the extension shows it: its own scheme, no web page.
        await edge.OpenAsync("chrome-extension://dnkimhodjfogjibnbbfdjdapgmmiojio/block-page.html?url=http%3A%2F%2Fofflist.test%2F");
        var blocked = await edge.ReadUntilAsync(reader, r => r.Text.StartsWith("extension://", StringComparison.Ordinal));
        Assert.Null(AddressBarHost.Of(blocked.Text));
    }

    /// <summary>The installed Edge, in a profile of its own under the temp folder.</summary>
    private sealed class IsolatedEdge : IAsyncDisposable
    {
        private readonly string _exe;
        private readonly string _root;
        private readonly string _appId;
        private Process? _launcher;

        private IsolatedEdge(string exe, string root)
        {
            _exe = exe;
            _root = root;
            // Edge names the Default profile's windows after its user data folder.
            _appId = "MSEdge." + Path.GetFileName(UserData).Replace(" ", "", StringComparison.Ordinal) + ".Default";
        }

        private string UserData => Path.Combine(_root, "AnchorTest User Data");

        public static async Task<IsolatedEdge> StartAsync(string url)
        {
            var exe = FindEdge();
            Assert.True(exe is not null, "Microsoft Edge is not installed; this test reads its address bar.");
            var edge = new IsolatedEdge(exe!, Path.Combine(Path.GetTempPath(), "anchor-native-edge-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(edge.UserData);
            edge._launcher = edge.Start(url);
            await Task.CompletedTask;
            return edge;
        }

        public async Task OpenAsync(string url)
        {
            using var opener = Start(url);
            await opener.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }

        /// <summary>Reads the window's address bar until <paramref name="predicate"/> holds, or fails.</summary>
        public async Task<AddressBarReading> ReadUntilAsync(EdgeAddressBarReader reader, Func<AddressBarReading, bool> predicate)
        {
            AddressBarReading? last = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var window = FindWindow();
                if (window != IntPtr.Zero && reader.Read(window) is { } reading)
                {
                    last = reading;
                    if (predicate(reading)) return reading;
                }
                await Task.Delay(250);
            }
            Assert.Fail($"Edge's address bar never showed what was expected (last read: '{last?.Text ?? "<nothing>"}').");
            return null!;
        }

        private IntPtr FindWindow()
        {
            var found = IntPtr.Zero;
            Win32.NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!Win32.NativeMethods.IsWindowVisible(hwnd)) return true;
                if (!string.Equals(BrowserWindowAppIds.ReadAppId(hwnd), _appId, StringComparison.OrdinalIgnoreCase)) return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private Process Start(string url)
        {
            var psi = new ProcessStartInfo(_exe) { UseShellExecute = false };
            psi.ArgumentList.Add($"--user-data-dir={UserData}");
            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add("--no-default-browser-check");
            psi.ArgumentList.Add("--disable-sync");
            psi.ArgumentList.Add("--lang=en-US");
            psi.ArgumentList.Add("--host-resolver-rules=MAP offlist.test 127.0.0.1");
            psi.ArgumentList.Add(url);
            // An Anchor extension an Edge policy installs here reaches no agent.
            psi.Environment["ANCHOR_WITNESS_PIPE"] = "anchor-witness-native-test-" + Guid.NewGuid().ToString("N");
            return Process.Start(psi) ?? throw new InvalidOperationException("Edge did not start.");
        }

        private static string? FindEdge()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
            if (key?.GetValue(null) is string registered && File.Exists(registered)) return registered;
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     })
            {
                var path = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            // Edge may run on in a process of its own (it relaunches itself from
            // some parents): stop whatever shows this profile's windows too.
            var window = FindWindow();
            var browser = window == IntPtr.Zero ? null : TryGetProcess(window);
            foreach (var process in new[] { browser, _launcher })
            {
                if (process is null) continue;
                try { process.Kill(entireProcessTree: true); } catch { /* gone already */ }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* best-effort */ }
                process.Dispose();
            }
            for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
            {
                try { Directory.Delete(_root, recursive: true); }
                catch { await Task.Delay(500); }
            }
        }

        private static Process? TryGetProcess(IntPtr window)
        {
            Win32.NativeMethods.GetWindowThreadProcessId(window, out var pid);
            try { return Process.GetProcessById((int)pid); } catch { return null; }
        }
    }
}
