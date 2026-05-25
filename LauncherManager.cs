using System.IO;
using System.Text;
using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    public class LauncherManager
    {
        private readonly Logger          logger;
        private readonly string          launchersFolder;
        private readonly string          focusGuardExePath;
        private readonly string          focusGuardLogPath;
        private readonly SettingsManager settings;

        public LauncherManager(Logger logger, string pluginDataPath, SettingsManager settings)
        {
            this.logger   = logger;
            this.settings = settings;

            launchersFolder   = Path.Combine(pluginDataPath, "Launchers");
            focusGuardLogPath = Path.Combine(pluginDataPath, "Logs", "FocusGuard.log");

            Directory.CreateDirectory(launchersFolder);
            Directory.CreateDirectory(Path.Combine(pluginDataPath, "Logs"));

            string pluginInstallDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            focusGuardExePath = Path.Combine(pluginInstallDir, "GameTask.FocusGuard.exe");
        }

        public string GetLauncherPath(Game game)
        {
            string safeName = Utils.MakeSafeFileName(game.Name);
            return Path.Combine(launchersFolder, $"{safeName}.vbs");
        }

        /// <param name="resolvedExePath">
        /// The fully resolved path to the game's actual .exe (from custom path or action).
        /// Used to tell FocusGuard which process to watch.
        /// If null or empty, FocusGuard will not be launched.
        /// </param>
        public void CreateOrUpdateLauncher(Game game, string resolvedExePath = null)
        {
            string launcherPath = GetLauncherPath(game);
            string taskSafeName = Utils.MakeSafeTaskName(game.Name);
            string taskName     = $"GameTask_v1_{taskSafeName}";

            // 1. Fire the scheduled task (hidden, no wait)
            string content =
                "Set shell = CreateObject(\"WScript.Shell\")\r\n" +
                $"shell.Run \"schtasks /run /tn \" & Chr(34) & \"\\GameTask\\{taskName}\" & Chr(34), 0, False\r\n";

            // 2. Immediately start FocusGuard.exe using the real game exe name
            string exeFileName = string.IsNullOrWhiteSpace(resolvedExePath)
                ? null
                : Path.GetFileName(resolvedExePath);

            if (settings.Current.BringWindowToForeground &&
                !string.IsNullOrWhiteSpace(exeFileName) &&
                File.Exists(focusGuardExePath))
            {
                var s = settings.Current;
                content +=
                    $"shell.Run Chr(34) & \"{focusGuardExePath}\" & Chr(34) & " +
                    $"\" \" & Chr(34) & \"{exeFileName}\" & Chr(34) & " +
                    $"\" {s.GuardSeconds} \" & Chr(34) & \"{focusGuardLogPath}\" & Chr(34) & " +
                    $"\" {s.FocusProcessTimeoutMs} {s.FocusWindowTimeoutMs} {s.FocusEarlyPushCount} {s.FocusEarlyPushInterval}\", 0, False\r\n";
            }

            File.WriteAllText(launcherPath, content, Encoding.ASCII);
            logger.Log($"Launcher verified: {game.Name} (exe: {exeFileName ?? "unknown"})");
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
    }
}
