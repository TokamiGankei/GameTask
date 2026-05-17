using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Playnite;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    public class ActionManager
    {
        private readonly Logger logger;
        private readonly LauncherManager launcherManager;

        private const string ActionName = "Play Without UAC";

        public ActionManager(Logger logger, LauncherManager launcherManager)
        {
            this.logger = logger;
            this.launcherManager = launcherManager;
        }

        public void CreateOrUpdatePlayAction(Game game, IPlayniteAPI api)
        {
            if (game.GameActions == null)
            {
                game.GameActions = new ObservableCollection<GameAction>();
            }

            string launcherPath = launcherManager.GetLauncherPath(game);

            var action = game.GameActions.FirstOrDefault(a => a.Name == ActionName);

            if (action == null)
            {
                action = new GameAction
                {
                    Name = ActionName
                };

                game.GameActions.Add(action);
                logger.Log($"Action created: {game.Name}");
            }
            else
            {
                logger.Log($"Action verified: {game.Name}");
            }

            action.Type = GameActionType.File;
            action.Path = "wscript.exe";
            action.Arguments = $"\"{launcherPath}\"";
            action.IsPlayAction = true;

            ConfigureOfficialPlayniteTracking(game, action);

            api.Database.Games.Update(game);
        }

        public void RemovePlayAction(Game game, IPlayniteAPI api)
        {
            if (game.GameActions == null)
                return;

            var actions = game.GameActions
                .Where(a => a.Name == ActionName)
                .ToList();

            foreach (var action in actions)
            {
                game.GameActions.Remove(action);
            }

            api.Database.Games.Update(game);

            logger.Log($"Action removed: {game.Name}");
        }

        private void ConfigureOfficialPlayniteTracking(Game game, GameAction action)
        {
            string exePath = Utils.ResolveExecutablePath(game, ActionName);

            if (string.IsNullOrWhiteSpace(exePath))
            {
                logger.Log($"Tracking not configured, EXE not found: {game.Name}");
                return;
            }

            string processName = Path.GetFileNameWithoutExtension(exePath);

            if (string.IsNullOrWhiteSpace(processName))
            {
                logger.Log($"Tracking not configured, invalid process: {game.Name}");
                return;
            }

            action.TrackingMode = TrackingMode.ProcessName;
            action.TrackingPath = processName;
            action.InitialTrackingDelay = 3000;
            action.TrackingFrequency = 2000;

            logger.Log($"Official Playnite tracking configured: {game.Name} -> {processName}");
        }
    }
}