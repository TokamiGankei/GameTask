using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    public class LauncherManager
    {
        private readonly Logger logger;
        private readonly string launchersFolder;
        private readonly SettingsManager settings;

        public LauncherManager(Logger logger, string pluginDataPath, SettingsManager settings)
        {
            this.logger   = logger;
            this.settings = settings;

            launchersFolder = Path.Combine(pluginDataPath, "Launchers");
            Directory.CreateDirectory(launchersFolder);
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
                $"shell.Run \"schtasks /run /tn \"\"\\GameTask\\{taskName}\"\"\", 0, False\r\n" +
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
        // Polls for the game process by PID (more reliable than
        // name-based AppActivate, which can match the wrong window
        // when multiple processes share the same exe name).
        // Falls back to name-based if PID resolution is unavailable.
        // =====================================================

        private string BuildFocusBlock(Game game)
        {
            string exeName = GetExeName(game);
            if (string.IsNullOrWhiteSpace(exeName))
                return string.Empty;

            // The VBS script:
            //  1. Waits up to 30 s for a process whose name matches the exe.
            //  2. Among all matching processes, picks the one started most
            //     recently (closest to "now"), which is almost certainly the
            //     one we just launched via the scheduled task.
            //  3. Calls AppActivate with that PID for precise targeting.
            return
                "\r\n" +
                "Dim i, oWMI, oProcs, oProc\r\n" +
                "Dim latestPID, latestDate, procDate\r\n" +
                "latestPID  = 0\r\n" +
                "latestDate = \"\"\r\n" +
                $"Dim targetName : targetName = \"{exeName}.exe\"\r\n" +
                "\r\n" +
                "Set oWMI = GetObject(\"winmgmts:{impersonationLevel=impersonate}!\\\\.\\root\\cimv2\")\r\n" +
                "\r\n" +
                "For i = 1 To 60\r\n" +
                "    WScript.Sleep 500\r\n" +
                "    Set oProcs = oWMI.ExecQuery(\"SELECT ProcessId, CreationDate FROM Win32_Process WHERE Name = '\" & targetName & \"'\")\r\n" +
                "    For Each oProc In oProcs\r\n" +
                "        procDate = oProc.CreationDate\r\n" +
                "        If procDate > latestDate Then\r\n" +
                "            latestDate = procDate\r\n" +
                "            latestPID  = oProc.ProcessId\r\n" +
                "        End If\r\n" +
                "    Next\r\n" +
                "    If latestPID > 0 Then Exit For\r\n" +
                "Next\r\n" +
                "\r\n" +
                "If latestPID > 0 Then\r\n" +
                "    On Error Resume Next\r\n" +
                "    shell.AppActivate latestPID\r\n" +
                "    On Error GoTo 0\r\n" +
                "End If\r\n";
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
