using System.IO;
using System.Text;
using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    public class LauncherManager
    {
        private readonly Logger logger;
        private readonly string launchersFolder;

        public LauncherManager(Logger logger, string pluginDataPath)
        {
            this.logger = logger;

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
            string taskName = $"GameTask_v1_{taskSafeName}";

            string content =
$@"Set shell = CreateObject(""WScript.Shell"")
shell.Run ""schtasks /run /tn """"\GameTask\{taskName}"""" "", 0, False
";

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
    }
}