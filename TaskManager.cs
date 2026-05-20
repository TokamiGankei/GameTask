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

            if (string.IsNullOrWhiteSpace(resolvedExe))
            {
                var action = GetValidAction(game, ignoredActionName);
                if (action == null)
                {
                    logger.Log($"ERROR: No valid action found for: {game.Name}");
                    return;
                }

                resolvedExe = ResolveExecutable(game, action);
                logger.Log($"Original Action.Path: {action.Path}");
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

            string entry = $"{game.Name}|{resolvedExe}";

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

        public List<(string GameName, string ExePath)> GetPendingTasks()
        {
            var result = new List<(string, string)>();
            if (!File.Exists(pendingFile)) return result;

            foreach (var line in File.ReadAllLines(pendingFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('|');
                if (parts.Length < 2) continue;
                result.Add((parts[0], parts[1]));
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

        public string ResolveExecutable(Game game, GameAction action)
        {
            if (action == null) return null;

            string path = action.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path)) return null;

            path = path.Trim('"');

            if (path.Contains("{InstallDir}") && !string.IsNullOrWhiteSpace(game.InstallDirectory))
                path = path.Replace("{InstallDir}", game.InstallDirectory);

            if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(game.InstallDirectory))
                path = Path.Combine(game.InstallDirectory, path);

            return path;
        }
    }
}
