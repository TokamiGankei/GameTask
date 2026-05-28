using System.IO;
using System.Linq;
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
            string exeFileName  = string.IsNullOrWhiteSpace(resolvedExePath)
                ? null
                : Path.GetFileName(resolvedExePath);

            // Detect Steam games — use steam.exe -applaunch instead of schtasks
            bool isSteamGame = IsSteamGame(game);

            string launchLine;
            if (isSteamGame)
            {
                string steamAppId  = GetSteamAppId(game);
                string steamExe    = resolvedExePath ?? @"C:\Program Files (x86)\Steam\steam.exe";
                launchLine =
                    $"shell.Run Chr(34) & \"{steamExe}\" & Chr(34) & \" -applaunch {steamAppId}\", 0, False\r\n";
                // FocusGuard watches steam.exe which spawns the game
                exeFileName = Path.GetFileName(steamExe);
            }
            else
            {
                launchLine =
                    $"shell.Run \"schtasks /run /tn \" & Chr(34) & \"\\GameTask\\{taskName}\" & Chr(34), 0, False\r\n";
            }

            string content = "Set shell = CreateObject(\"WScript.Shell\")\r\n" + launchLine;

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
            logger.Log($"Launcher verified: {game.Name} (exe: {exeFileName ?? "unknown"}{(isSteamGame ? ", Steam" : "")})");
        }

        private static bool IsSteamGame(Game game)
        {
            if (game.GameActions == null) return false;
            return game.GameActions.Any(a =>
                a != null &&
                !string.IsNullOrWhiteSpace(a.Path) &&
                a.Path.StartsWith("steam://", StringComparison.OrdinalIgnoreCase));
        }

        private static string GetSteamAppId(Game game)
        {
            // steam://run/APPID or steam://rungameid/APPID
            var action = game.GameActions?.FirstOrDefault(a =>
                a != null && a.Path != null &&
                a.Path.StartsWith("steam://", StringComparison.OrdinalIgnoreCase));

            if (action == null) return "0";

            var parts = action.Path.Split('/');
            return parts.Length > 0 ? parts[parts.Length - 1] : "0";
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
