using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

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

        private readonly Logger                logger;
        private readonly SettingsManager       settingsManager;
        private readonly GameTaskSettings      gameTaskSettings;
        private readonly TaskManager           taskManager;
        private readonly LauncherManager       launcherManager;
        private readonly ActionManager         actionManager;
        private readonly HiddenLauncherManager hiddenLauncherManager;
        private readonly NotificationManager   notificationManager;
        private readonly TrackerManager        trackerManager;
        private readonly PathManager           pathManager;

        // Cooldown: tracks last launch time to prevent double-launch
        private DateTime lastLaunchTime = DateTime.MinValue;
        private const int LaunchCooldownMs = 3000;

        public GameTaskPlugin(IPlayniteAPI api) : base(api)
        {
            this.api = api;

            Properties = new GenericPluginProperties { HasSettings = true };

            pluginDataPath = GetPluginUserDataPath();
            Directory.CreateDirectory(pluginDataPath);

            logger                = new Logger(pluginDataPath);
            settingsManager       = new SettingsManager(logger, pluginDataPath);
            gameTaskSettings      = new GameTaskSettings(this, settingsManager);
            taskManager           = new TaskManager(logger, pluginDataPath);
            launcherManager       = new LauncherManager(logger, pluginDataPath, settingsManager);
            actionManager         = new ActionManager(logger, launcherManager);
            hiddenLauncherManager = new HiddenLauncherManager(logger, pluginDataPath);
            trackerManager        = new TrackerManager(logger);
            pathManager           = new PathManager(logger, pluginDataPath, taskManager);
            notificationManager   = new NotificationManager(api, RunPendingTasks, pluginDataPath);

            logger.Log("GameTask plugin started.");
        }

        // =====================================================
        // SETTINGS PAGE
        // =====================================================

        public override ISettings GetSettings(bool firstRunSettings) => gameTaskSettings;

        public override UserControl GetSettingsView(bool firstRunSettings)
            => new SettingsView { DataContext = gameTaskSettings };

        // =====================================================
        // STARTUP
        // =====================================================

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);

            // Run startup tasks in background to avoid blocking Playnite UI
            // and prevent the "serious error" crash on slow PCs with many tagged games
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    ScanLibrary();

                    if (settingsManager.Current.DetectOrphanTasks)
                        CheckForOrphanTasks();

                    if (settingsManager.Current.DetectCorruptedTasks)
                        CheckForCorruptedTasks();

                    // ShowPendingNotification must run on UI thread
                    api.MainView.UIDispatcher.Invoke(() =>
                        notificationManager.ShowPendingNotification());
                }
                catch (Exception ex)
                {
                    logger.Log($"ERROR in background startup: {ex.Message}");
                }
            });
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
        // =====================================================

        private void CheckForOrphanTasks()
        {
            try
            {
                var knownNames = api.Database.Games
                    .Where(HasGameTaskTag)
                    .Select(g => TaskManager.GetTaskName(g))
                    .ToList();

                taskManager.WriteKnownTasks(knownNames);

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
                        string cell = line.Split(',')[0].Trim('"');
                        string name = Path.GetFileName(cell);

                        if (!string.IsNullOrWhiteSpace(name) &&
                            !knownNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                            orphans.Add(name);
                    }
                }

                if (orphans.Count == 0) { logger.Log("Orphan check: none found."); return; }

                logger.Log($"Orphan check: {orphans.Count} orphan(s) found.");
                api.Notifications.Add(new NotificationMessage(
                    "GameTaskOrphans",
                    $"GameTask: {orphans.Count} orphan task(s) found in Task Scheduler. Click to clean up.",
                    NotificationType.Info,
                    () => CleanOrphanTasks()));
            }
            catch (Exception ex) { logger.Log($"ERROR in orphan check: {ex.Message}"); }
        }

        // =====================================================
        // CORRUPTED TASK DETECTION
        // =====================================================

        private void CheckForCorruptedTasks()
        {
            try
            {
                var taggedGames = api.Database.Games.Where(HasGameTaskTag).ToList();
                var corrupted   = new List<Game>();

                foreach (var game in taggedGames)
                {
                    string customExe = pathManager.GetCustomPath(game);
                    if (!string.IsNullOrWhiteSpace(customExe))
                    {
                        if (!File.Exists(customExe)) corrupted.Add(game);
                        continue;
                    }

                    var action = game.GameActions?.FirstOrDefault(a =>
                        a != null && a.Name != ActionName && !string.IsNullOrWhiteSpace(a.Path));

                    if (action == null) continue;

                    string exePath = taskManager.ResolveExecutable(game, action);
                    if (!string.IsNullOrWhiteSpace(exePath) && !File.Exists(exePath))
                        corrupted.Add(game);
                }

                if (corrupted.Count == 0) { logger.Log("Corrupted task check: none found."); return; }

                logger.Log($"Corrupted task check: {corrupted.Count} found.");
                foreach (var game in corrupted)
                    notificationManager.ShowExecutableFixNotification(game, () => FixExecutablePath(game));
            }
            catch (Exception ex) { logger.Log($"ERROR in corrupted task check: {ex.Message}"); }
        }

        // =====================================================
        // UNKNOWN EXE DETECTION
        // Games tagged with GameTask but whose exe can't be
        // resolved — FocusGuard won't work for these games.
        // =====================================================

        private void CheckForUnknownExecutables()
        {
            var unknown = api.Database.Games
                .Where(HasGameTaskTag)
                .Where(g => string.IsNullOrWhiteSpace(ResolveExePathForGame(g)))
                .ToList();

            if (unknown.Count == 0)
            {
                api.Dialogs.ShowMessage("All tagged games have a detected executable.", "GameTask");
                return;
            }

            logger.Log($"Unknown exe check: {unknown.Count} game(s) need attention.");

            api.Notifications.Add(new NotificationMessage(
                "GameTaskUnknownExe",
                $"GameTask: {unknown.Count} game(s) have no detected executable. Click to fix them.",
                NotificationType.Info,
                () => FixAllUnknownExecutables()));
        }

        // =====================================================
        // FIX ALL UNKNOWN EXECUTABLES
        // =====================================================

        private void FixAllUnknownExecutables()
        {
            var unknown = api.Database.Games
                .Where(HasGameTaskTag)
                .Where(g => string.IsNullOrWhiteSpace(ResolveExePathForGame(g)))
                .ToList();

            if (unknown.Count == 0)
            {
                api.Dialogs.ShowMessage("All tagged games have a detected executable.", "GameTask");
                return;
            }

            int fixed_count = 0;

            foreach (var game in unknown)
            {
                var result = api.Dialogs.ShowMessage(
                    $"Game \"{game.Name}\" has no detected executable.\n\nDo you want to select it now?",
                    "GameTask — Fix Executable",
                    System.Windows.MessageBoxButton.YesNoCancel);

                if (result == System.Windows.MessageBoxResult.Cancel) break;
                if (result == System.Windows.MessageBoxResult.No) continue;

                bool fixed_path = pathManager.PromptForExecutable(game);
                if (!fixed_path) continue;

                fixed_count++;
                string customExe = pathManager.GetCustomPath(game);

                taskManager.RemovePendingEntry(game);
                taskManager.AddPendingTask(game, ActionName, customExe);
                launcherManager.CreateOrUpdateLauncher(game, customExe);
                actionManager.CreateOrUpdatePlayAction(game, api);
                notificationManager.RemoveExecutableFixNotification(game);

                logger.Log($"Executable fixed via Fix All: {game.Name} -> {customExe}");
            }

            api.Notifications.Remove("GameTaskUnknownExe");

            if (fixed_count > 0)
            {
                notificationManager.ShowPendingNotification();
                api.Dialogs.ShowMessage(
                    $"{fixed_count} executable(s) configured.\n\nClick the notification or use \"Create Pending Tasks\" to register the Windows tasks.",
                    "GameTask");
            }
        }

        // =====================================================
        // CLEAN ORPHAN TASKS
        // =====================================================

        private void CleanOrphanTasks()
        {
            try
            {
                var knownNames = api.Database.Games
                    .Where(HasGameTaskTag)
                    .Select(g => TaskManager.GetTaskName(g))
                    .ToList();

                taskManager.WriteKnownTasks(knownNames);
                logger.Log("Requesting elevated orphan cleanup...");

                Process.Start(new ProcessStartInfo
                {
                    FileName        = "wscript.exe",
                    Arguments       = $"\"{hiddenLauncherManager.GetCleanOrphansLauncherPath()}\"",
                    Verb            = "runas",
                    UseShellExecute = true
                });

                api.Notifications.Remove("GameTaskOrphans");
            }
            catch (Exception ex) { logger.Log($"ERROR running orphan cleanup: {ex.Message}"); }
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
        // REPAIR (single game)
        // =====================================================

        private void RepairGameTask(Game game)
        {
            string resolvedExe = ResolveExePathForGame(game);
            launcherManager.CreateOrUpdateLauncher(game, resolvedExe);
            actionManager.CreateOrUpdatePlayAction(game, api);

            if (!TaskExists(game))
                taskManager.AddPendingTask(game, ActionName, resolvedExe);

            ValidateExecutable(game);
        }

        // =====================================================
        // RESOLVE EXE PATH
        // =====================================================

        private string ResolveExePathForGame(Game game)
        {
            string customExe = pathManager.GetCustomPath(game);
            if (!string.IsNullOrWhiteSpace(customExe) && File.Exists(customExe))
                return customExe;

            var action = game.GameActions?.FirstOrDefault(a =>
                a != null && a.Name != ActionName && !string.IsNullOrWhiteSpace(a.Path));

            if (action == null) return null;

            string resolved = taskManager.ResolveExecutable(game, action);
            return File.Exists(resolved) ? resolved : null;
        }

        private string ResolveExeNameForGame(Game game)
        {
            string path = ResolveExePathForGame(game);
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path);
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
                logger.Log($"EXE not found: {game.Name}");
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

            launcherManager.CreateOrUpdateLauncher(game, customExe);
            actionManager.CreateOrUpdatePlayAction(game, api);

            notificationManager.ShowPendingNotification();
            logger.Log($"Pending task queued after fix: {game.Name}");

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

                string resolvedExe = ResolveExePathForGame(game);
                launcherManager.CreateOrUpdateLauncher(game, resolvedExe);
                actionManager.CreateOrUpdatePlayAction(game, api);
                taskManager.AddPendingTask(game, ActionName, resolvedExe);

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
                process.StartInfo.FileName        = "schtasks.exe";
                process.StartInfo.Arguments       = $"/query /tn \"\\GameTask\\{taskName}\"";
                process.StartInfo.CreateNoWindow  = true;
                process.StartInfo.UseShellExecute = false;

                process.Start();
                process.WaitForExit();

                return process.ExitCode == 0;
            }
            catch { return false; }
        }

        // =====================================================
        // RUN PENDING TASKS
        // =====================================================

        private void RunPendingTasks()
        {
            // Cooldown — prevent double-launch within 3 seconds
            var now = DateTime.UtcNow;
            if ((now - lastLaunchTime).TotalMilliseconds < LaunchCooldownMs)
            {
                logger.Log("RunPendingTasks skipped — cooldown active.");
                return;
            }
            lastLaunchTime = now;

            // If PendingTasks.txt is empty, rescan all tagged games for missing tasks
            // This handles the case where tasks were manually deleted from Task Scheduler
            var pending = taskManager.GetPendingTasks();
            if (pending.Count == 0)
            {
                logger.Log("PendingTasks.txt is empty — rescanning for missing tasks...");
                foreach (var game in api.Database.Games.Where(HasGameTaskTag))
                {
                    if (!TaskExists(game))
                    {
                        string resolvedExe = ResolveExePathForGame(game);
                        if (!string.IsNullOrWhiteSpace(resolvedExe))
                        {
                            taskManager.AddPendingTask(game, ActionName, resolvedExe);
                            logger.Log($"Re-queued missing task: {game.Name}");
                        }
                    }
                }

                pending = taskManager.GetPendingTasks();
                if (pending.Count == 0)
                {
                    logger.Log("No missing tasks found after rescan.");
                    api.Notifications.Add(new NotificationMessage(
                        "GameTaskNoPending",
                        "GameTask: No pending tasks found. All tasks are already created.",
                        NotificationType.Info,
                        null));
                    return;
                }
            }

            try
            {
                string resultFile = hiddenLauncherManager.ResultFile;
                if (File.Exists(resultFile)) File.Delete(resultFile);

                logger.Log("Requesting elevated task creation...");

                Process.Start(new ProcessStartInfo
                {
                    FileName        = "wscript.exe",
                    Arguments       = $"\"{hiddenLauncherManager.GetCreateLauncherPath()}\"",
                    Verb            = "runas",
                    UseShellExecute = true
                });

                // Poll for result file in background (up to 60 s)
                Task.Run(() =>
                {
                    for (int i = 0; i < 120; i++)
                    {
                        Thread.Sleep(500);
                        if (!File.Exists(resultFile)) continue;

                        try
                        {
                            string content = File.ReadAllText(resultFile).Trim();
                            int created = 0, failed = 0;
                            foreach (var part in content.Split('|'))
                            {
                                var kv = part.Split('=');
                                if (kv.Length != 2) continue;
                                if (kv[0] == "created") int.TryParse(kv[1], out created);
                                if (kv[0] == "failed")  int.TryParse(kv[1], out failed);
                            }

                            logger.Log($"Task creation result: created={created} failed={failed}");

                            if (failed == 0)
                                notificationManager.ShowInfo($"{created} task(s) created successfully.");
                            else
                                notificationManager.ShowError($"{created} task(s) created, {failed} failed. Check Logs\\PS1.log for details.");

                            api.Notifications.Remove("GameTaskPending");
                        }
                        catch (Exception ex) { logger.Log($"ERROR reading result file: {ex.Message}"); }

                        break;
                    }
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
        // PUBLIC METHODS FOR DIAGNOSTICS VIEW
        // =====================================================

        public bool HasGameTaskTagPublic(Game game) => HasGameTaskTag(game);

        public string ResolveExePathPublic(Game game) => ResolveExePathForGame(game);

        public bool HasNoGameAction(Game game)
        {
            if (game.GameActions == null || !game.GameActions.Any(a =>
                a != null && a.Name != ActionName && !string.IsNullOrWhiteSpace(a.Path)))
                return true;
            return false;
        }

        public void InvokeFixAllUnknownExecutables() => FixAllUnknownExecutables();

        public void InvokeRepairAll() => RepairAll();

        public void InvokeRepairGame(Game game)
        {
            RepairGameTask(game);
            notificationManager.ShowPendingNotification();
        }

        public void InvokeFixExecutablePath(Game game) => FixExecutablePath(game);

        public void InvokeDisableGame(Game game) => DisableGameTask(new[] { game });

        // =====================================================
        // OPEN DIAGNOSTICS
        // =====================================================

        private void OpenDiagnostics()
        {
            var vm   = new DiagnosticsViewModel(api, this, pathManager, taskManager, ActionName);
            var view = new DiagnosticsView { DataContext = vm };
            view.ShowDialog();
        }

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
            int taggedCount  = api.Database.Games.Count(HasGameTaskTag);
            int unknownCount = api.Database.Games.Count(g => HasGameTaskTag(g) &&
                string.IsNullOrWhiteSpace(ResolveExePathForGame(g)));

            yield return new MainMenuItem
            {
                MenuSection = "@GameTask",
                Description = $"Repair All Tagged Games ({taggedCount})",
                Action      = _ => RepairAll()
            };

            yield return new MainMenuItem
            {
                MenuSection = "@GameTask",
                Description = unknownCount > 0
                    ? $"Fix All Unknown Executables ({unknownCount})"
                    : "Fix All Unknown Executables",
                Action = _ => FixAllUnknownExecutables()
            };

            yield return new MainMenuItem
            {
                MenuSection = "@GameTask",
                Description = "Clean Orphan Tasks",
                Action      = _ => CleanOrphanTasks()
            };

            yield return new MainMenuItem
            {
                MenuSection = "@GameTask",
                Description = "Open Data Folder",
                Action      = _ => OpenDataFolder()
            };

            yield return new MainMenuItem
            {
                MenuSection = "@GameTask",
                Description = "Diagnostics",
                Action      = _ => OpenDiagnostics()
            };

            // Quick-access settings toggles
            var s = settingsManager.Current;

            yield return new MainMenuItem
            {
                MenuSection = "@GameTask|Settings",
                Description = $"Bring Game to Foreground: {(s.BringWindowToForeground ? "ON" : "OFF")}",
                Action      = _ =>
                {
                    s.BringWindowToForeground = !s.BringWindowToForeground;
                    settingsManager.Save();
                    api.Dialogs.ShowMessage(
                        $"\"Bring game window to foreground\" is now {(s.BringWindowToForeground ? "ON" : "OFF")}.\n\nRun \"Repair All Tagged Games\" so the launchers are regenerated.",
                        "GameTask – Settings");
                }
            };

            yield return new MainMenuItem
            {
                MenuSection = "@GameTask|Settings",
                Description = $"Detect Orphan Tasks on Startup: {(s.DetectOrphanTasks ? "ON" : "OFF")}",
                Action      = _ =>
                {
                    s.DetectOrphanTasks = !s.DetectOrphanTasks;
                    settingsManager.Save();
                    api.Dialogs.ShowMessage(
                        $"\"Detect orphan tasks on startup\" is now {(s.DetectOrphanTasks ? "ON" : "OFF")}.",
                        "GameTask – Settings");
                }
            };

            yield return new MainMenuItem
            {
                MenuSection = "@GameTask|Settings",
                Description = $"Detect Corrupted Tasks on Startup: {(s.DetectCorruptedTasks ? "ON" : "OFF")}",
                Action      = _ =>
                {
                    s.DetectCorruptedTasks = !s.DetectCorruptedTasks;
                    settingsManager.Save();
                    api.Dialogs.ShowMessage(
                        $"\"Detect corrupted tasks on startup\" is now {(s.DetectCorruptedTasks ? "ON" : "OFF")}.",
                        "GameTask – Settings");
                }
            };
        }
    }
}
