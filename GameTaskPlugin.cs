using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace GameTaskPlugin
{
    public class GameTaskPlugin : GenericPlugin
    {
        public override Guid Id { get; } =
            Guid.Parse("d6d798db-6a1f-4c6e-9b1d-000000000001");

        private const string TagName = "[GT] UAC-skip";
        private const string ActionName = "Play Without UAC";

        private readonly IPlayniteAPI api;
        private readonly string pluginDataPath;

        private readonly Logger logger;
        private readonly TaskManager taskManager;
        private readonly LauncherManager launcherManager;
        private readonly ActionManager actionManager;
        private readonly HiddenLauncherManager hiddenLauncherManager;
        private readonly NotificationManager notificationManager;
        private readonly TrackerManager trackerManager;
        private readonly PathManager pathManager;

        public GameTaskPlugin(IPlayniteAPI api) : base(api)
        {
            this.api = api;

            Properties = new GenericPluginProperties
            {
                HasSettings = false
            };

            pluginDataPath = GetPluginUserDataPath();
            Directory.CreateDirectory(pluginDataPath);

            logger = new Logger(pluginDataPath);
            taskManager = new TaskManager(logger, pluginDataPath);
            launcherManager = new LauncherManager(logger, pluginDataPath);
            actionManager = new ActionManager(logger, launcherManager);
            hiddenLauncherManager = new HiddenLauncherManager(logger, pluginDataPath);
            trackerManager = new TrackerManager(logger);
            pathManager = new PathManager(logger, pluginDataPath, taskManager);
            notificationManager = new NotificationManager(api, RunPendingTasks, pluginDataPath);

            logger.Log("GameTask plugin started.");
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);

            ScanLibrary();
            notificationManager.ShowPendingNotification();
        }

        // =====================================================
        // LIBRARY SCAN
        // =====================================================

        private void ScanLibrary()
        {
            taskManager.ResetPendingFile();

            foreach (var game in api.Database.Games)
            {
                if (!HasGameTaskTag(game))
                    continue;

                RepairGameTask(game);
            }
        }

        // =====================================================
        // REPAIR
        // =====================================================

        private void RepairGameTask(Game game)
        {
            launcherManager.CreateOrUpdateLauncher(game);
            actionManager.CreateOrUpdatePlayAction(game, api);

            if (!TaskExists(game))
                taskManager.AddPendingTask(game, ActionName);

            ValidateExecutable(game);
        }

        // =====================================================
        // VALIDATE EXECUTABLE
        // Checks both the custom path (PathManager) and the
        // Playnite action path. If neither resolves to a valid
        // file, shows an error notification so the user can fix it.
        // =====================================================

        private void ValidateExecutable(Game game)
        {
            // First try the custom path saved by the user
            string customExe = pathManager.GetCustomPath(game);
            if (!string.IsNullOrWhiteSpace(customExe) && File.Exists(customExe))
            {
                notificationManager.RemoveExecutableFixNotification(game);
                return;
            }

            // Fall back to resolving from the Playnite action
            var action =
                game.GameActions?.FirstOrDefault(a =>
                    a != null &&
                    a.Name != ActionName &&
                    !string.IsNullOrWhiteSpace(a.Path));

            if (action == null)
                return;

            string exePath = pathManager.GetExecutablePath(game, action);

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                logger.Log($"Tracking not configured, EXE not found: {game.Name}");

                notificationManager.ShowExecutableFixNotification(
                    game,
                    () => FixExecutablePath(game));
            }
            else
            {
                notificationManager.RemoveExecutableFixNotification(game);
            }
        }

        // =====================================================
        // FIX EXECUTABLE PATH
        // Opens a file dialog for the user to select the .exe,
        // saves it via PathManager, forces the pending task to
        // be re-queued with the correct path, and shows a
        // confirmation message.
        // =====================================================

        private void FixExecutablePath(Game game)
        {
            bool fixedPath = pathManager.PromptForExecutable(game);
            if (!fixedPath) return;

            logger.Log($"Executable manually fixed: {game.Name}");
            notificationManager.RemoveExecutableFixNotification(game);

            string customExe = pathManager.GetCustomPath(game);

            // Force re-queue with the correct exe, bypassing TaskExists()
            taskManager.RemovePendingEntry(game);
            taskManager.AddPendingTask(game, ActionName, customExe);

            launcherManager.CreateOrUpdateLauncher(game);
            actionManager.CreateOrUpdatePlayAction(game, api);

            notificationManager.ShowPendingNotification();
            logger.Log($"Pending task queued after executable fix: {game.Name}");

            api.Dialogs.ShowMessage(
                $"Executable configured for \"{game.Name}\".\n\nClick \"Create Pending Tasks\" in the GameTask menu (or the notification) to register the Windows task with elevated rights.",
                "GameTask");
        }

        // =====================================================
        // REMOVE CUSTOM PATH
        // Clears the manually saved exe override for a game.
        // After clearing, ValidateExecutable runs again so the
        // notification reappears if the automatic path is also missing.
        // =====================================================

        private void RemoveCustomPath(Game game)
        {
            pathManager.RemoveCustomPath(game);
            logger.Log($"Custom path removed: {game.Name}");

            ValidateExecutable(game);

            api.Dialogs.ShowMessage(
                $"Custom executable path removed for \"{game.Name}\".\n\nGameTask will now try to detect the executable automatically.",
                "GameTask");
        }

        // =====================================================
        // TAG HELPERS
        // =====================================================

        private bool HasGameTaskTag(Game game)
        {
            if (game.Tags != null && game.Tags.Any(t => t != null && t.Name == TagName))
                return true;

            if (game.TagIds == null)
                return false;

            foreach (var tagId in game.TagIds)
            {
                var tag = api.Database.Tags.Get(tagId);
                if (tag != null && tag.Name == TagName)
                    return true;
            }

            return false;
        }

        private Tag GetOrCreateGameTaskTag()
        {
            var existing = api.Database.Tags.FirstOrDefault(t => t.Name == TagName);
            if (existing != null) return existing;

            var tag = new Tag(TagName);
            api.Database.Tags.Add(tag);

            logger.Log("Tag created: " + TagName);
            return tag;
        }

        // =====================================================
        // ENABLE / DISABLE
        // =====================================================

        private void EnableGameTask(IEnumerable<Game> games)
        {
            var tag = GetOrCreateGameTaskTag();

            foreach (var game in games)
            {
                if (game.TagIds == null)
                    game.TagIds = new List<Guid>();

                if (!game.TagIds.Contains(tag.Id))
                {
                    game.TagIds.Add(tag.Id);
                    api.Database.Games.Update(game);
                }

                RepairGameTask(game);
                logger.Log($"GameTask enabled: {game.Name}");
            }

            notificationManager.ShowPendingNotification();
        }

        private void DisableGameTask(IEnumerable<Game> games)
        {
            taskManager.ResetDeleteFile();

            foreach (var game in games)
            {
                var tag = api.Database.Tags.FirstOrDefault(t => t.Name == TagName);
                if (tag != null && game.TagIds != null && game.TagIds.Contains(tag.Id))
                    game.TagIds.Remove(tag.Id);

                actionManager.RemovePlayAction(game, api);
                launcherManager.RemoveLauncher(game);
                taskManager.RemovePendingEntry(game);
                taskManager.AddDeleteTask(game);
                notificationManager.RemoveExecutableFixNotification(game);

                api.Database.Games.Update(game);
                logger.Log($"GameTask disabled: {game.Name}");
            }

            RunDeleteTasks();
        }

        // =====================================================
        // REBUILD / REPAIR
        // =====================================================

        private void RebuildSelected(IEnumerable<Game> games)
        {
            taskManager.ResetDeleteFile();

            foreach (var game in games)
            {
                actionManager.RemovePlayAction(game, api);
                launcherManager.RemoveLauncher(game);
                taskManager.RemovePendingEntry(game);
                taskManager.AddDeleteTask(game);

                launcherManager.CreateOrUpdateLauncher(game);
                actionManager.CreateOrUpdatePlayAction(game, api);
                taskManager.AddPendingTask(game, ActionName);

                ValidateExecutable(game);
                logger.Log($"GameTask rebuilt: {game.Name}");
            }

            RunDeleteTasks();
            notificationManager.ShowPendingNotification();
        }

        private void RepairSelected(IEnumerable<Game> games)
        {
            foreach (var game in games)
            {
                RepairGameTask(game);
                logger.Log($"GameTask repaired: {game.Name}");
            }

            notificationManager.ShowPendingNotification();
        }

        // =====================================================
        // TASK EXISTS CHECK
        // =====================================================

        private bool TaskExists(Game game)
        {
            string taskName = $"GameTask_v1_{Utils.MakeSafeTaskName(game.Name)}";

            try
            {
                using var process = new Process();
                process.StartInfo.FileName = "schtasks.exe";
                process.StartInfo.Arguments = $"/query /tn \"\\GameTask\\{taskName}\"";
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.UseShellExecute = false;

                process.Start();
                process.WaitForExit();

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // =====================================================
        // RUN HELPERS (elevated via wscript runas)
        // =====================================================

        private void RunPendingTasks()
        {
            try
            {
                logger.Log("Requesting elevated task creation...");

                Process.Start(new ProcessStartInfo
                {
                    FileName = "wscript.exe",
                    Arguments = $"\"{hiddenLauncherManager.GetCreateLauncherPath()}\"",
                    Verb = "runas",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR running create helper: {ex.Message}");
            }
        }

        private void RunDeleteTasks()
        {
            try
            {
                logger.Log("Requesting elevated task cleanup...");

                Process.Start(new ProcessStartInfo
                {
                    FileName = "wscript.exe",
                    Arguments = $"\"{hiddenLauncherManager.GetDeleteLauncherPath()}\"",
                    Verb = "runas",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR running delete helper: {ex.Message}");
            }
        }

        // =====================================================
        // UTILITY ACTIONS
        // =====================================================

        private void OpenDataFolder()
        {
            try
            {
                Process.Start(pluginDataPath);
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR opening data folder: {ex.Message}");
            }
        }

        private void OpenTaskScheduler()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "taskschd.msc",
                    UseShellExecute = true
                });

                logger.Log("Task Scheduler opened.");
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR opening Task Scheduler: {ex.Message}");
            }
        }

        // =====================================================
        // GAME MENU ITEMS
        // =====================================================

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            yield return new GameMenuItem
            {
                MenuSection = "GameTask",
                Description = "Enable GameTask",
                Action = menuArgs => EnableGameTask(menuArgs.Games)
            };

            yield return new GameMenuItem
            {
                MenuSection = "GameTask",
                Description = "Disable GameTask",
                Action = menuArgs => DisableGameTask(menuArgs.Games)
            };

            yield return new GameMenuItem
            {
                MenuSection = "GameTask",
                Description = "Create Pending Tasks",
                Action = menuArgs => RunPendingTasks()
            };

            yield return new GameMenuItem
            {
                MenuSection = "GameTask",
                Description = "Rebuild Selected",
                Action = menuArgs => RebuildSelected(menuArgs.Games)
            };

            yield return new GameMenuItem
            {
                MenuSection = "GameTask",
                Description = "Repair Selected",
                Action = menuArgs => RepairSelected(menuArgs.Games)
            };

            yield return new GameMenuItem
            {
                MenuSection = "GameTask",
                Description = "Fix Executable Path",
                Action = menuArgs =>
                {
                    foreach (var game in menuArgs.Games)
                        FixExecutablePath(game);
                }
            };

            yield return new GameMenuItem
            {
                MenuSection = "GameTask",
                Description = "Remove Custom Executable Path",
                Action = menuArgs =>
                {
                    foreach (var game in menuArgs.Games)
                        RemoveCustomPath(game);
                }
            };

            yield return new GameMenuItem
            {
                MenuSection = "GameTask",
                Description = "Open Data Folder",
                Action = menuArgs => OpenDataFolder()
            };

            yield return new GameMenuItem
            {
                MenuSection = "GameTask",
                Description = "Open Task Scheduler",
                Action = menuArgs => OpenTaskScheduler()
            };
        }
    }
}
