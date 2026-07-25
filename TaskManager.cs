using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    public class TaskManager
    {
        private readonly Logger logger;

        private readonly string cacheFolder;
        private readonly string pendingFile;
        private readonly string deleteFile;
        private readonly string knownTasksFile;

        public TaskManager(Logger logger, string pluginDataPath)
        {
            this.logger = logger;

            cacheFolder = Path.Combine(pluginDataPath, "Cache");
            Directory.CreateDirectory(cacheFolder);

            pendingFile    = Path.Combine(cacheFolder, "PendingTasks.txt");
            deleteFile     = Path.Combine(cacheFolder, "DeleteTasks.txt");
            knownTasksFile = Path.Combine(cacheFolder, "KnownTasks.txt");

            if (!File.Exists(pendingFile))    File.WriteAllText(pendingFile,    string.Empty);
            if (!File.Exists(deleteFile))     File.WriteAllText(deleteFile,     string.Empty);
            if (!File.Exists(knownTasksFile)) File.WriteAllText(knownTasksFile, string.Empty);
        }

        // =========================================================
        // Pending Tasks
        // =========================================================

        public void ResetPendingFile()
        {
            File.WriteAllText(pendingFile, string.Empty);
            logger.Log("PendingTasks reset.");
        }

        public void AddPendingTask(Game game, string ignoredActionName, string resolvedExeOverride = null)
        {
            if (game == null) return;

            string resolvedExe = resolvedExeOverride;
            GameAction action;

            if (string.IsNullOrWhiteSpace(resolvedExe))
            {
                action = GetValidAction(game, ignoredActionName);
                if (action == null)
                {
                    logger.Log($"ERROR: No valid action found for: {game.Name}");
                    return;
                }

                resolvedExe = ResolveExecutable(game, action);
                logger.Log($"Original Action.Path: {action.Path}");
            }
            else
            {
                // A custom/override exe was supplied (e.g. manually selected via
                // "Fix Executable"). Only inherit a WorkingDir from a GameAction
                // whose own resolved Path points at this exact same exe — grabbing
                // "the first" action here would risk pulling the WorkingDir from an
                // unrelated/outdated action and pointing the task at the wrong folder.
                action = FindActionMatchingExe(game, ignoredActionName, resolvedExe);
            }

            logger.Log($"Resolved Path: {resolvedExe}");

            if (string.IsNullOrWhiteSpace(resolvedExe))
            {
                logger.Log($"ERROR: Resolved EXE empty: {game.Name}");
                return;
            }

            if (!File.Exists(resolvedExe))
            {
                logger.Log($"SKIP exe not found: {game.Name} -> {resolvedExe}");
                return;
            }

            string workingDir = ResolveWorkingDirectory(game, action, resolvedExe);
            logger.Log($"Resolved WorkingDir: {workingDir}");

            string entry = $"{game.Name}|{resolvedExe}|{workingDir}";

            var existing = File.ReadAllLines(pendingFile)
                .Where(l => !l.StartsWith(game.Name + "|", StringComparison.OrdinalIgnoreCase))
                .ToList();

            existing.Add(entry);
            File.WriteAllLines(pendingFile, existing);
            logger.Log($"Pending task added: {game.Name}");
        }

        public void RemovePendingEntry(Game game)
        {
            if (game == null || !File.Exists(pendingFile)) return;

            var lines = File.ReadAllLines(pendingFile)
                .Where(l => !l.StartsWith(game.Name + "|", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            File.WriteAllLines(pendingFile, lines);
            logger.Log($"Pending entry removed: {game.Name}");
        }

        public List<(string GameName, string ExePath, string WorkingDir)> GetPendingTasks()
        {
            var result = new List<(string, string, string)>();
            if (!File.Exists(pendingFile)) return result;

            foreach (var line in File.ReadAllLines(pendingFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('|');
                if (parts.Length < 2) continue;

                // Backward compatible: older entries (pre WorkingDir support) only
                // have 2 columns. Fall back to the exe's own folder in that case.
                string workingDir = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2])
                    ? parts[2]
                    : Path.GetDirectoryName(parts[1]);

                result.Add((parts[0], parts[1], workingDir));
            }

            return result;
        }

        // =========================================================
        // Delete Tasks
        // =========================================================

        public void ResetDeleteFile()
        {
            File.WriteAllText(deleteFile, string.Empty);
            logger.Log("DeleteTasks reset.");
        }

        public void AddDeleteTask(Game game)
        {
            if (game == null) return;

            string taskName = GetTaskName(game);
            var existing = File.ReadAllLines(deleteFile);
            if (!existing.Any(l => l.Equals(taskName, StringComparison.OrdinalIgnoreCase)))
            {
                File.AppendAllLines(deleteFile, new[] { taskName });
                logger.Log($"Delete task added: {taskName}");
            }
        }

        public List<string> GetDeleteTasks()
        {
            return File.Exists(deleteFile)
                ? File.ReadAllLines(deleteFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
                : new List<string>();
        }

        // =========================================================
        // Known Tasks  (used for orphan detection)
        // Writes the full list of task names the plugin currently
        // manages so the PowerShell orphan-cleaner can compare
        // against what's actually in the Task Scheduler.
        // =========================================================

        public void WriteKnownTasks(IEnumerable<string> taskNames)
        {
            File.WriteAllLines(knownTasksFile, taskNames);
            logger.Log($"KnownTasks written: {taskNames.Count()} entries.");
        }

        // =========================================================
        // Helpers
        // =========================================================

        public static string GetTaskName(Game game)
            => $"GameTask_v1_{Utils.MakeSafeTaskName(game.Name)}";

        private GameAction GetValidAction(Game game, string ignoredActionName)
        {
            if (game.GameActions == null) return null;
            return game.GameActions.FirstOrDefault(
                a => a != null && a.Name != ignoredActionName && !string.IsNullOrWhiteSpace(a.Path));
        }

        /// <summary>
        /// Finds the GameAction (if any) whose resolved executable path matches the
        /// given exe exactly. Used when a custom/override exe is supplied, so we only
        /// ever inherit a WorkingDir from the action that actually points at that exe
        /// — never from an unrelated action that merely happens to be first in the list.
        /// </summary>
        private GameAction FindActionMatchingExe(Game game, string ignoredActionName, string resolvedExe)
        {
            if (game.GameActions == null || string.IsNullOrWhiteSpace(resolvedExe)) return null;

            foreach (var a in game.GameActions)
            {
                if (a == null || a.Name == ignoredActionName || string.IsNullOrWhiteSpace(a.Path)) continue;

                string candidate = ResolveExecutable(game, a);
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                if (string.Equals(
                        candidate.TrimEnd('\\'),
                        resolvedExe.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }

            return null;
        }

        public string ResolveExecutable(Game game, GameAction action)
        {
            if (action == null) return null;

            try
            {
                string path = action.Path?.Trim();
                if (string.IsNullOrWhiteSpace(path)) return null;

                path = path.Trim('"');

                if (path.Contains("{InstallDir}") && !string.IsNullOrWhiteSpace(game.InstallDirectory))
                    path = path.Replace("{InstallDir}", game.InstallDirectory);

                if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(game.InstallDirectory))
                    path = Path.Combine(game.InstallDirectory, path);

                return path;
            }
            catch (Exception ex)
            {
                // A malformed action.Path (e.g. stray quotes/arguments pasted from a
                // .bat file, invalid characters) must never bubble up and abort the
                // whole library scan — just treat this action as unusable.
                logger.Log($"WARNING: could not resolve action path for {game?.Name}: {action.Path} -> {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolves the working directory to use when creating the scheduled task.
        /// Priority:
        ///   1. The GameAction's own WorkingDir, exactly as configured in Playnite
        ///      (this is what the user sees/edits in the game's Edit > Actions screen).
        ///   2. Fallback: the resolved executable's own parent folder (previous
        ///      GameTask behavior), used when no WorkingDir is configured or when
        ///      it doesn't resolve to an existing folder.
        /// </summary>
        public string ResolveWorkingDirectory(Game game, GameAction action, string resolvedExe)
        {
            try
            {
                string workingDir = action?.WorkingDir?.Trim();

                if (!string.IsNullOrWhiteSpace(workingDir))
                {
                    workingDir = workingDir.Trim('"');

                    if (workingDir.Contains("{InstallDir}") && !string.IsNullOrWhiteSpace(game?.InstallDirectory))
                        workingDir = workingDir.Replace("{InstallDir}", game.InstallDirectory);

                    if (!Path.IsPathRooted(workingDir) && !string.IsNullOrWhiteSpace(game?.InstallDirectory))
                        workingDir = Path.Combine(game.InstallDirectory, workingDir);

                    if (Directory.Exists(workingDir))
                        return workingDir;

                    logger.Log($"WARNING: configured WorkingDir not found on disk, falling back to exe folder: {workingDir}");
                }
            }
            catch (Exception ex)
            {
                // Same reasoning as ResolveExecutable: a malformed WorkingDir string
                // must never abort the whole library scan.
                logger.Log($"WARNING: could not resolve WorkingDir for {game?.Name}: {action?.WorkingDir} -> {ex.Message}");
            }

            try
            {
                return Path.GetDirectoryName(resolvedExe);
            }
            catch (Exception ex)
            {
                logger.Log($"WARNING: could not derive folder from resolved exe for {game?.Name}: {resolvedExe} -> {ex.Message}");
                return null;
            }
        }
    }
}
