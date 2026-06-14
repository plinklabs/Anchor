<#
.SYNOPSIS
    Launches the agent's `--show-test-traymenu` self-test, screenshots the
    brand-styled tray context menu (AA4, #176) via BitBlt+CAPTUREBLT, and prints
    the captured rect.

.DESCRIPTION
    Used to visually verify the redesigned tray menu (#176) without needing a real
    backend, session, or a tray click. A tray MenuFlyout is a popup, not a window,
    so a headless run can't open it by clicking the tray — the self-test builds the
    very same menu the TrayIconHost ships (via the shared TrayMenu factory) and
    shows it open over a small ink host window, then keeps the process alive so the
    open menu can be captured.

    Same capture path as verify-overlay.ps1 / verify-toast.ps1 — BitBlt +
    CAPTUREBLT off the screen DC (so WinUI 3's DirectComposition surfaces fold in)
    on a per-monitor DPI-aware process. We capture the ink HOST window's rect; the
    flyout popup (a separate PopupWindowSiteBridge) opens within it, so the host
    rect contains the open menu.

    Saves the screenshot under
    `agent/src/FocusAgent.App/bin/x64/Debug/verify-traymenu.png`.
#>

param(
    [switch]$SkipBuild,
    [string]$OutPath = "$PSScriptRoot\..\..\agent\src\FocusAgent.App\bin\x64\Debug\verify-traymenu.png",
    [int]$WaitForHwndMs = 8000,
    [int]$CaptureAfterMs = 2000
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path "$PSScriptRoot\..\.."
$slnPath  = Join-Path $repoRoot 'agent\FocusAgent.sln'
$exePath  = Join-Path $repoRoot 'agent\src\FocusAgent.App\bin\x64\Debug\net10.0-windows10.0.19041.0\FocusAgent.App.exe'

if (-not $SkipBuild) {
    Write-Host "Building $slnPath ..."
    & dotnet build $slnPath -p:Platform=x64 --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }
}

if (-not (Test-Path $exePath)) { throw "Agent exe not found at: $exePath" }

# Kill any stray agent process so the menu we capture is ours.
Get-Process -Name 'FocusAgent.App' -ErrorAction SilentlyContinue | Stop-Process -Force

Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class TrayMenuCapture
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                     IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    public const uint SRCCOPY    = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")]
    public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // Match the WinUI 3 desktop window class and pick the largest one for this PID
    // — same approach as verify-overlay.ps1. The ink host window is the only
    // sizable WinUIDesktop window in --show-test-traymenu mode; the flyout popup
    // opens within it (a separate, non-WinUIDesktop PopupWindowSiteBridge).
    public static IntPtr FindHostHwnd(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        int largestArea = 0;
        EnumWindows((hWnd, _) =>
        {
            uint windowPid;
            GetWindowThreadProcessId(hWnd, out windowPid);
            if (windowPid != pid) return true;

            var className = new StringBuilder(256);
            GetClassNameW(hWnd, className, className.Capacity);
            if (!className.ToString().StartsWith("WinUIDesktop", StringComparison.OrdinalIgnoreCase))
                return true;

            RECT r;
            if (!GetWindowRect(hWnd, out r)) return true;
            int area = (r.Right - r.Left) * (r.Bottom - r.Top);
            if (area <= 0) return true;
            if (area > largestArea)
            {
                largestArea = area;
                found = hWnd;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string ListProcessWindows(uint pid)
    {
        var sb = new StringBuilder();
        EnumWindows((hWnd, _) =>
        {
            uint windowPid;
            GetWindowThreadProcessId(hWnd, out windowPid);
            if (windowPid != pid) return true;
            int len = GetWindowTextLengthW(hWnd);
            var title = new StringBuilder(len + 1);
            if (len > 0) GetWindowTextW(hWnd, title, title.Capacity);
            var cls = new StringBuilder(256);
            GetClassNameW(hWnd, cls, cls.Capacity);
            RECT r; GetWindowRect(hWnd, out r);
            sb.Append("  hwnd=0x").Append(hWnd.ToInt64().ToString("X"))
              .Append(" rect=").Append(r.Left).Append(",").Append(r.Top)
              .Append(" ").Append(r.Right - r.Left).Append("x").Append(r.Bottom - r.Top)
              .Append(" class=\"").Append(cls).Append("\"")
              .Append(" title=\"").Append(title).Append("\"\n");
            return true;
        }, IntPtr.Zero);
        return sb.ToString();
    }

    public static void CapturePng(IntPtr hwnd, string outPath, out int w, out int h)
    {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) throw new InvalidOperationException("GetWindowRect failed");
        w = r.Right - r.Left;
        h = r.Bottom - r.Top;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr destDc = g.GetHdc();
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                if (!BitBlt(destDc, 0, 0, w, h, screenDc, r.Left, r.Top, SRCCOPY | CAPTUREBLT))
                    throw new InvalidOperationException("BitBlt failed");
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                g.ReleaseHdc(destDc);
            }
            bmp.Save(outPath, ImageFormat.Png);
        }
    }
}
'@ -ReferencedAssemblies 'System.Drawing'

# Per-monitor DPI awareness before any window/screen WinAPI — see
# verify-toast.ps1 for the rationale.
[void][TrayMenuCapture]::SetProcessDpiAwarenessContext([TrayMenuCapture]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

Write-Host "Launching agent: $exePath --show-test-traymenu"
$proc = Start-Process -FilePath $exePath -ArgumentList '--show-test-traymenu' -PassThru
try {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $hwnd = [IntPtr]::Zero
    while ($sw.ElapsedMilliseconds -lt $WaitForHwndMs) {
        $hwnd = [TrayMenuCapture]::FindHostHwnd([uint32]$proc.Id)
        if ($hwnd -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 50
    }
    if ($hwnd -eq [IntPtr]::Zero) {
        Write-Host "Windows owned by pid $($proc.Id):"
        Write-Host ([TrayMenuCapture]::ListProcessWindows([uint32]$proc.Id))
        throw "Tray-menu host HWND never appeared within $WaitForHwndMs ms"
    }

    # Let the flyout open over the host (it shows ~400ms after the window appears).
    Start-Sleep -Milliseconds $CaptureAfterMs

    $outDir = Split-Path -Parent $OutPath
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

    [int]$w = 0; [int]$h = 0
    [TrayMenuCapture]::CapturePng($hwnd, $OutPath, [ref]$w, [ref]$h)

    Write-Host ""
    Write-Host "TRAY MENU CAPTURED"
    Write-Host "  hwnd  : 0x$($hwnd.ToInt64().ToString('X'))"
    Write-Host "  outer : ${w} x ${h} px (physical)"
    Write-Host "  saved : $OutPath"
}
finally {
    if (-not $proc.HasExited) {
        try { $proc.Kill() } catch { }
    }
}
