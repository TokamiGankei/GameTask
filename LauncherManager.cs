using System;
using System.IO;
using System.Text;
using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    public class LauncherManager
    {
        private readonly Logger logger;
        private readonly string launchersFolder;
        private readonly string focusPs1Path;
        private readonly SettingsManager settings;

        public LauncherManager(Logger logger, string pluginDataPath, SettingsManager settings)
        {
            this.logger   = logger;
            this.settings = settings;

            launchersFolder = Path.Combine(pluginDataPath, "Launchers");
            Directory.CreateDirectory(launchersFolder);

            // Write the reusable focus helper script once
            string cacheFolder = Path.Combine(pluginDataPath, "Cache");
            Directory.CreateDirectory(cacheFolder);
            focusPs1Path = Path.Combine(cacheFolder, "FocusGame.ps1");
            WriteFocusScript();
        }

        // =====================================================
        // FOCUS HELPER SCRIPT
        // A reusable PowerShell script that receives the exe name
        // as a parameter, waits for the process and calls
        // SetForegroundWindow via P/Invoke — works even over
        // Playnite fullscreen, unlike VBScript AppActivate.
        // =====================================================

        private void WriteFocusScript()
        {
            string script = @"
param([string]$ExeName)

Add-Type @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public class FocusGuard {
    delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport(""user32.dll"")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);
    [DllImport(""user32.dll"")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport(""user32.dll"")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport(""user32.dll"")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport(""user32.dll"")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport(""user32.dll"")] static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport(""user32.dll"")] static extern IntPtr GetForegroundWindow();
    [DllImport(""user32.dll"")] static extern bool AttachThreadInput(uint a, uint b, bool c);
    [DllImport(""kernel32.dll"")] static extern uint GetCurrentThreadId();
    [DllImport(""user32.dll"")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport(""user32.dll"")] static extern void GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport(""user32.dll"")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport(""user32.dll"")] static extern IntPtr DispatchMessage(ref MSG msg);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam;
                 public IntPtr lParam; public uint time; public System.Drawing.Point pt; }

    const uint EVENT_SYSTEM_FOREGROUND = 3;
    const uint WINEVENT_OUTOFCONTEXT   = 0;

    static IntPtr  _gameHwnd;
    static int     _gamePid;
    static IntPtr  _hook;
    static bool    _done;

    static void ForceForeground() {
        var hwnd = _gameHwnd;
        if (hwnd == IntPtr.Zero) return;

        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);

        AllowSetForegroundWindow(_gamePid);

        var fg      = GetForegroundWindow();
        uint dummy  = 0;
        var fgTid   = GetWindowThreadProcessId(fg, out dummy);
        var myTid   = GetCurrentThreadId();

        if (fgTid != 0 && fgTid != myTid) {
            AttachThreadInput(myTid, fgTid, true);
            SetForegroundWindow(hwnd);
            ShowWindow(hwnd, 9);
            AttachThreadInput(myTid, fgTid, false);
        } else {
            SetForegroundWindow(hwnd);
        }
    }

    static WinEventDelegate _delegate;

    static void OnForegroundChange(IntPtr hook, uint evt, IntPtr hwnd,
        int obj, int child, uint thread, uint time) {
        if (_done) return;
        if (hwnd == _gameHwnd) return;  // game already in foreground — OK
        // Something else stole focus — take it back immediately
        ForceForeground();
    }

    public static void Run(IntPtr gameHwnd, int gamePid, int guardSeconds) {
        _gameHwnd = gameHwnd;
        _gamePid  = gamePid;
        _done     = false;

        _delegate = new WinEventDelegate(OnForegroundChange);
        _hook     = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                        IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);

        // Initial push
        ForceForeground();

        // Pump messages for guardSeconds — the hook fires during this loop
        var end = DateTime.UtcNow.AddSeconds(guardSeconds);
        MSG msg;
        while (DateTime.UtcNow < end && !_done) {
            Thread.Sleep(100);
        }

        _done = true;
        UnhookWinEvent(_hook);
    }
}
'@ -ReferencedAssemblies 'System.Drawing'

$target = [System.IO.Path]::GetFileNameWithoutExtension($ExeName)

# Step 1 — wait up to 60 s for the game process
$proc = $null
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Milliseconds 500
    $candidates = Get-Process -Name $target -ErrorAction SilentlyContinue
    if ($candidates) {
        $proc = $candidates | Sort-Object StartTime -Descending | Select-Object -First 1
        break
    }
}
if ($proc -eq $null) { exit }

# Step 2 — wait up to 30 s for a window handle
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
        $hwnd = $proc.MainWindowHandle
        break
    }
}
if ($hwnd -eq [IntPtr]::Zero) { exit }

# Step 3 — install foreground event hook and guard for 20 seconds.
# Any window that steals focus triggers an immediate reclaim.
[FocusGuard]::Run($hwnd, $proc.Id, 20)
";
            File.WriteAllText(focusPs1Path, script, Encoding.UTF8);
            logger.Log("FocusGame.ps1 written.");
        }

        public string GetLauncherPath(Game game)
        {
            string safeName = Utils.MakeSafeFileName(game.Name);
            return Path.Combine(launchersFolder, $"{safeName}.vbs");
        }

        public void CreateOrUpdateLauncher(Game game)
        {
            string launcherPath = GetLauncherPath(game);
            string taskSafeName = Utils.MakeSafeTaskName(game.Name);
            string taskName     = $"GameTask_v1_{taskSafeName}";

            string focusBlock = settings.Current.BringWindowToForeground
                ? BuildFocusBlock(game)
                : string.Empty;

            string content =
                "Set shell = CreateObject(\"WScript.Shell\")\r\n" +
                $"shell.Run \"schtasks /run /tn \" & Chr(34) & \"\\GameTask\\{taskName}\" & Chr(34), 0, False\r\n" +
                focusBlock;

            File.WriteAllText(launcherPath, content, Encoding.ASCII);
            logger.Log($"Launcher verified: {game.Name}");
        }

        public void RemoveLauncher(Game game)
        {
            string launcherPath = GetLauncherPath(game);

            if (File.Exists(launcherPath))
            {
                File.Delete(launcherPath);
                logger.Log($"Launcher removed: {game.Name}");
            }
        }

        // =====================================================
        // FOCUS BLOCK
        // The VBS launcher fires the scheduled task, then
        // immediately spawns the PowerShell focus helper in the
        // background. PS uses SetForegroundWindow (P/Invoke)
        // which works over fullscreen windows.
        // =====================================================

        private string BuildFocusBlock(Game game)
        {
            string exeName = GetExeName(game);
            if (string.IsNullOrWhiteSpace(exeName))
                return string.Empty;

            return
                "shell.Run \"powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File \" " +
                $"& Chr(34) & \"{focusPs1Path}\" & Chr(34) & \" -ExeName {exeName}.exe\", 0, False\r\n";
        }

        private static string GetExeName(Game game)
        {
            if (game.GameActions == null)
                return string.Empty;

            foreach (var action in game.GameActions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.Path))
                    continue;

                if (action.Name == "Play Without UAC")
                    continue;

                string fileName = Path.GetFileNameWithoutExtension(action.Path);
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }

            return string.Empty;
        }
    }
}
