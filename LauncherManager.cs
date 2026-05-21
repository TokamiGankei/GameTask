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
using System.Runtime.InteropServices;
public class WinApi {
    [DllImport(""user32.dll"")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport(""user32.dll"")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport(""user32.dll"")]
    public static extern bool IsIconic(IntPtr hWnd);
    [DllImport(""user32.dll"")]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport(""user32.dll"")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport(""user32.dll"")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport(""kernel32.dll"")]
    public static extern uint GetCurrentThreadId();
    [DllImport(""user32.dll"")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
'@

$target = [System.IO.Path]::GetFileNameWithoutExtension($ExeName)

# Wait up to 60 s for the process to appear (some games are slow to start)
$proc = $null
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Milliseconds 500
    $candidates = Get-Process -Name $target -ErrorAction SilentlyContinue
    if ($candidates) {
        # Pick the most recently started instance
        $proc = $candidates | Sort-Object StartTime -Descending | Select-Object -First 1
        break
    }
}

if ($proc -eq $null) { exit }

# Wait up to 15 more seconds for the main window handle to appear
# Some games show a splash screen process first — keep waiting
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 500
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
}

if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { exit }

# Extra delay — let the game finish rendering its first frame
# before we try to bring it forward (avoids focus being returned to Playnite)
Start-Sleep -Milliseconds 1500

$proc.Refresh()
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { exit }

# Restore if minimized
if ([WinApi]::IsIconic($hwnd)) {
    [WinApi]::ShowWindow($hwnd, 9) | Out-Null  # SW_RESTORE
    Start-Sleep -Milliseconds 500
}

# Windows blocks SetForegroundWindow when another window owns the foreground lock.
# Workaround: attach our thread input to the foreground window's thread,
# call SetForegroundWindow, then detach. This bypasses the lock reliably.
$fgHwnd      = [WinApi]::GetForegroundWindow()
$fgThreadId  = 0
$fgProcId    = 0
$dummy       = 0
$fgThreadId  = [WinApi]::GetWindowThreadProcessId($fgHwnd, [ref]$dummy)
$myThreadId  = [WinApi]::GetCurrentThreadId()

[WinApi]::AllowSetForegroundWindow($proc.Id) | Out-Null

if ($fgThreadId -ne 0 -and $fgThreadId -ne $myThreadId) {
    [WinApi]::AttachThreadInput($myThreadId, $fgThreadId, $true) | Out-Null
    [WinApi]::SetForegroundWindow($hwnd) | Out-Null
    [WinApi]::ShowWindow($hwnd, 9) | Out-Null
    [WinApi]::AttachThreadInput($myThreadId, $fgThreadId, $false) | Out-Null
} else {
    [WinApi]::SetForegroundWindow($hwnd) | Out-Null
}
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
