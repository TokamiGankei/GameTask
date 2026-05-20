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

        private const string TagName    = "[GT] UAC-skip";
        private const string ActionName = "Play Without UAC";

        private readonly IPlayniteAPI api;
        private readonly string pluginDataPath;

        private readonly Logger               logger;
        private readonly SettingsManager      settingsManager;
        private readonly TaskManager          taskManager;
        private readonly LauncherManager      launcherManager;
        private readonly ActionManager        actionManager;
        private readonly HiddenLauncherManager hiddenLauncherManager;
        private readonly NotificationManager  notificationManager;
        private readonly TrackerManager       trackerManager;
        private readonly PathManager          pathManager;

        public GameTaskPlugin(IPlayniteAPI api) : base(api)
        {
            this.api = api;

            Properties = new GenericPluginProperties { HasSettings = false };

            pluginDataPath = GetPluginUserDataPath();
            Directory.CreateDirectory(pluginDataPath);

            logger              = new Logger(pluginDataPath);
            settingsManager     = new SettingsManager(logger, pluginDataPath);
            taskManager         = new TaskManager(logger, pluginDataPath);
            launcherManager     = new LauncherManager(logger, pluginDataPath, settingsManager);
            actionManager       = new ActionManager(logger, launcherManager);
            hiddenLauncherManager = new HiddenLauncherManager(logger, pluginDataPath);
            trackerManager      = new TrackerManager(logger);
            pathManager         = new PathManager(logger, pluginDataPath, taskManager);
            notificationManager = new NotificationManager(api, RunPendingTasks, pluginDataPath);

            logger.Log("GameTask plugin started.");
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);
            ScanLibrary();

            if (settingsManager.Current.DetectOrphanTasks)
                CheckForOrphanTasks();

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
                if (!HasGameTaskTag(game)) continue;
                RepairGameTask(game);
            }
        }

        // =====================================================
        // ORPHAN TASK DETECTION
        // Collects all task names the plugin knows about and
        // shows a notification if the Task Scheduler has extras.
        // =====================================================

        private void CheckForOrphanTasks()
        {
            try
            {
                // Build the set of task names that should exist
                var knownNames = api.Database.Games
                    .Where(HasGameTaskTag)
                    .Select(g => TaskManager.GetTaskName(g))
                    .ToList();

                taskManager.WriteKnownTasks(knownNames);

                // Query the Task Scheduler for everything under \GameTask\
                var orphans = new List<string>();

                using (var proc = new Process())
                {
                    proc.StartInfo.FileName               = "schtasks.exe";
                    proc.StartInfo.Arguments              = "/query /fo CSV /nh /tn \"\\GameTask\\\"";
                    proc.StartInfo.CreateNoWindow         = true;
                    proc.StartInfo.UseShellExecute        = false;
                    proc.StartInfo.RedirectStandardOutput = true;

                    proc.Start();
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();

                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        // CSV lines: "\\GameTask\\TaskName","Status",...
                        string cell = line.Split(',')[0].Trim('"');
                        string name = System.IO.Path.GetFileName(cell);

                        if (!string.IsNullOrWhiteSpace(name) &&
                            !knownNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        {
                            orphans.Add(name);
                        }
                    }
                }

                if (orphans.Count == 0)
                {
                    logger.Log("Orphan check: no orphans found.");
                    return;
                }

                logger.Log($"Orphan check: {orphans.Count} orphan(s) found.");

                api.Notifications.Add(new NotificationMessage(
                    "GameTaskOrphans",
                    $"GameTask: {orphans.Count} orphan task(s) found in Task Scheduler. Click to clean up.",
                    NotificationType.Info,
                    () => CleanOrphanTasks()
                ));
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR in orphan check: {ex.Message}");
            }
        }

        // =====================================================
        // CLEAN ORPHAN TASKS
        // Writes the known-tasks list and triggers the elevated
        // PowerShell script that removes anything not on that list.
        // =====================================================

        private void CleanOrphanTasks()
        {
            try
            {
                // Refresh the known-tasks file right before elevation
                var knownNames = api.Database.Games
                    .Where(HasGameTaskTag)
                    .Select(g => TaskManager.GetTaskName(g))
                    .ToList();

                taskManager.WriteKnownTasks(knownNames);

                logger.Log("Requesting elevated orphan cleanup...");

                Process.Start(new ProcessStartInfo
                {
                    FileName       = "wscript.exe",
                    Arguments      = $"\"{hiddenLauncherManager.GetCleanOrphansLauncherPath()}\"",
                    Verb           = "runas",
                    UseShellExecute = true
                });

                api.Notifications.Remove("GameTaskOrphans");
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR running orphan cleanup: {ex.Message}");
            }
        }

        // =====================================================
        // REPAIR ALL TAGGED GAMES
        // =====================================================

        private void RepairAll()
        {
            var taggedGames = api.Database.Games.Where(HasGameTaskTag).ToList();

            if (taggedGames.Count == 0)
            {
                api.Dialogs.ShowMessage("No games with the GameTask tag were found.", "GameTask");
                return;
            }

            foreach (var game in taggedGames)
            {
                RepairGameTask(game);
                logger.Log($"GameTask repaired (all): {game.Name}");
            }

            notificationManager.ShowPendingNotification();
            api.Dialogs.ShowMessage(
                $"Repair complete: {taggedGames.Count} game(s) processed.\n\nIf new pending tasks were queued, click the notification or use \"Create Pending Tasks\" to register them.",
                "GameTask");
        }

        // =====================================================
        // TOGGLE SETTINGS
        // =====================================================

        private void ToggleBringToForeground()
        {
            settingsManager.Current.BringWindowToForeground = !settingsManager.Current.BringWindowToForeground;
            settingsManager.Save();

            bool enabled = settingsManager.Current.BringWindowToForeground;
            logger.Log($"BringWindowToForeground set to: {enabled}");

            api.Dialogs.ShowMessage(
                $"\"Bring game window to foreground\" is now {(enabled ? "ON" : "OFF")}.\n\nRun \"Repair All\" so the launchers are regenerated with the new setting.",
                "GameTask – Settings");
        }

        private void ToggleDetectOrphans()
        {
            settingsManager.Current.DetectOrphanTasks = !settingsManager.Current.DetectOrphanTasks;
            settingsManager.Save();

            bool enabled = settingsManager.Current.DetectOrphanTasks;
            logger.Log($"DetectOrphanTasks set to: {enabled}");

            api.Dialogs.ShowMessage(
                $"\"Detect orphan tasks on startup\" is now {(enabled ? "ON" : "OFF")}.",
                "GameTask – Settings");
        }

        // =====================================================
        // REPAIR (single / selected)
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
        // =====================================================

        private void ValidateExecutable(Game game)
        {
            string customExe = pathManager.GetCustomPath(game);
            if (!string.IsNullOrWhiteSpace(customExe) && File.Exists(customExe))
            {
                notificationManager.RemoveExecutableFixNotification(game);
                return;
            }

            var action = game.GameActions?.FirstOrDefault(a =>
                a != null && a.Name != ActionName && !string.IsNullOrWhiteSpace(a.Path));

            if (action == null) return;

            string exePath = pathManager.GetExecutablePath(game, action);

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                logger.Log($"Tracking not configured, EXE not found: {game.Name}");
                notificationManager.ShowExecutableFixNotification(game, () => FixExecutablePath(game));
            }
            else
            {
                notificationManager.RemoveExecutableFixNotification(game);
            }
        }

        // =====================================================
        // FIX / REMOVE EXECUTABLE PATH
        // =====================================================

        private void FixExecutablePath(Game game)
        {
            bool fixedPath = pathManager.PromptForExecutable(game);
            if (!fixedPath) return;

            logger.Log($"Executable manually fixed: {game.Name}");
            notificationManager.RemoveExecutableFixNotification(game);

            string customExe = pathManager.GetCustomPath(game);

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

            if (game.TagIds == null) return false;

            foreach (var tagId in game.TagIds)
            {
                var tag = api.Database.Tags.Get(tagId);
                if (tag != null && tag.Name == TagName) return true;
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
                if (game.TagIds == null) game.TagIds = new List<Guid>();

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
        // REBUILD / REPAIR SELECTED
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
            string taskName = TaskManager.GetTaskName(game);

            try
            {
                using var process = new Process();
                process.StartInfo.FileName       = "schtasks.exe";
                process.StartInfo.Arguments      = $"/query /tn \"\\GameTask\\{taskName}\"";
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.UseShellExecute = false;

                process.Start();
                process.WaitForExit();

                return process.ExitCode == 0;
            }
            catch { return false; }
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
                    FileName        = "wscript.exe",
                    Arguments       = $"\"{hiddenLauncherManager.GetCreateLauncherPath()}\"",
                    Verb            = "runas",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { logger.Log($"ERROR running create helper: {ex.Message}"); }
        }

        private void RunDeleteTasks()
        {
            try
            {
                logger.Log("Requesting elevated task cleanup...");
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "wscript.exe",
                    Arguments       = $"\"{hiddenLauncherManager.GetDeleteLauncherPath()}\"",
                    Verb            = "runas",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { logger.Log($"ERROR running delete helper: {ex.Message}"); }
        }

        // =====================================================
        // UTILITY ACTIONS
        // =====================================================

        private void OpenDataFolder()
        {
            try { Process.Start(pluginDataPath); }
            catch (Exception ex) { logger.Log($"ERROR opening data folder: {ex.Message}"); }
        }

        private void OpenTaskScheduler()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "taskschd.msc", UseShellExecute = true });
                logger.Log("Task Scheduler opened.");
            }
            catch (Exception ex) { logger.Log($"ERROR opening Task Scheduler: {ex.Message}"); }
        }

        // =====================================================
        // MENUS
        // =====================================================

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            yield return new GameMenuItem { MenuSection = "GameTask", Description = "Enable GameTask",
                Action = a => EnableGameTask(a.Games) };

            yield return new GameMenuItem { MenuSection = "GameTask", Description = "Disable GameTask",
                Action = a => DisableGameTask(a.Games) };

            yield return new GameMenuItem { MenuSection = "GameTask", Description = "Create Pending Tasks",
                Action = a => RunPendingTasks() };

            yield return new GameMenuItem { MenuSection = "GameTask", Description = "Rebuild Selected",
                Action = a => RebuildSelected(a.Games) };

            yield return new GameMenuItem { MenuSection = "GameTask", Description = "Repair Selected",
                Action = a => RepairSelected(a.Games) };

            yield return new GameMenuItem { MenuSection = "GameTask", Description = "Fix Executable Path",
                Action = a => { foreach (var g in a.Games) FixExecutablePath(g); } };

            yield return new GameMenuItem { MenuSection = "GameTask", Description = "Remove Custom Executable Path",
                Action = a => { foreach (var g in a.Games) RemoveCustomPath(g); } };

            yield return new GameMenuItem { MenuSection = "GameTask", Description = "Open Data Folder",
                Action = a => OpenDataFolder() };

            yield return new GameMenuItem { MenuSection = "GameTask", Description = "Open Task Scheduler",
                Action = a => OpenTaskScheduler() };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@GameTask",
                Description = "Repair All Tagged Games",
                Action      = _ => RepairAll()
            };

            yield return new MainMenuItem
            {
                MenuSection = "@GameTask",
                Description = "Clean Orphan Tasks",
                Action      = _ => CleanOrphanTasks()
            };

            yield return new MainMenuItem
            {
                MenuSection = "@GameTask|Settings",
                Description = $"Bring Game to Foreground: {(settingsManager.Current.BringWindowToForeground ? "ON" : "OFF")}",
                Action      = _ => ToggleBringToForeground()
            };

            yield return new MainMenuItem
            {
                MenuSection = "@GameTask|Settings",
                Description = $"Detect Orphan Tasks on Startup: {(settingsManager.Current.DetectOrphanTasks ? "ON" : "OFF")}",
                Action      = _ => ToggleDetectOrphans()
            };
        }
    }
}
