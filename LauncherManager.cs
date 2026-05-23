using System.IO;
using System.Text;
using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    public class LauncherManager
    {
        private readonly Logger        logger;
        private readonly string        launchersFolder;
        private readonly string        focusGuardExePath;
        private readonly SettingsManager settings;

        public LauncherManager(Logger logger, string pluginDataPath, SettingsManager settings)
        {
            this.logger   = logger;
            this.settings = settings;

            launchersFolder = Path.Combine(pluginDataPath, "Launchers");
            Directory.CreateDirectory(launchersFolder);

            // FocusGuard.exe lives next to the plugin DLL
            string pluginInstallDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            focusGuardExePath = Path.Combine(pluginInstallDir, "GameTask.FocusGuard.exe");
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
            string exeName      = GetExeName(game);

            // 1. Fire the scheduled task (hidden, no wait)
            string content =
                "Set shell = CreateObject(\"WScript.Shell\")\r\n" +
                $"shell.Run \"schtasks /run /tn \" & Chr(34) & \"\\GameTask\\{taskName}\" & Chr(34), 0, False\r\n";

            // 2. Immediately start FocusGuard.exe in background if enabled
            if (settings.Current.BringWindowToForeground &&
                !string.IsNullOrWhiteSpace(exeName) &&
                File.Exists(focusGuardExePath))
            {
                content +=
                    $"shell.Run Chr(34) & \"{focusGuardExePath}\" & Chr(34) & " +
                    $"\" {exeName}.exe 20\", 0, False\r\n";
            }

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

        private static string GetExeName(Game game)
        {
            if (game.GameActions == null) return string.Empty;

            foreach (var action in game.GameActions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.Path)) continue;
                if (action.Name == "Play Without UAC") continue;

                string fileName = Path.GetFileNameWithoutExtension(action.Path);
                if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
            }

            return string.Empty;
        }
    }
}
