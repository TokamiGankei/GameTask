using System;
using System.IO;
using System.Collections.Generic;
using System.Text;

using Microsoft.Win32;

using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    public class PathManager
    {
        private readonly Logger logger;

        private readonly string customPathsFile;

        private readonly TaskManager taskManager;

        private Dictionary<string, string> customPaths;

        public PathManager(
            Logger logger,
            string pluginDataPath,
            TaskManager taskManager)
        {
            this.logger = logger;
            this.taskManager = taskManager;

            string configFolder =
                Path.Combine(
                    pluginDataPath,
                    "Config");

            Directory.CreateDirectory(configFolder);

            customPathsFile =
                Path.Combine(
                    configFolder,
                    "CustomPaths.txt");

            LoadCustomPaths();
        }

        // =====================================================
        // LOAD
        // =====================================================

        private void LoadCustomPaths()
        {
            try
            {
                customPaths = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

                if (!File.Exists(customPathsFile))
                {
                    SaveCustomPaths();
                    return;
                }

                foreach (var line in File.ReadAllLines(customPathsFile, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    int separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                        continue;

                    string key = line.Substring(0, separatorIndex).Trim();
                    string value = line.Substring(separatorIndex + 1).Trim();

                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                        customPaths[key] = value;
                }

                logger.Log(
                    $"Custom paths loaded: {customPaths.Count}");
            }
            catch (Exception ex)
            {
                logger.Log(
                    $"ERROR loading custom paths: {ex}");

                customPaths =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        // =====================================================
        // SAVE
        // =====================================================

        private void SaveCustomPaths()
        {
            try
            {
                var lines = new List<string>();

                foreach (var entry in customPaths)
                    lines.Add($"{entry.Key}={entry.Value}");

                File.WriteAllLines(
                    customPathsFile,
                    lines,
                    Encoding.UTF8);

                logger.Log(
                    "Custom paths saved.");
            }
            catch (Exception ex)
            {
                logger.Log(
                    $"ERROR saving custom paths: {ex}");
            }
        }

        // =====================================================
        // GET CUSTOM PATH
        // =====================================================

        public string GetCustomPath(Game game)
        {
            if (game == null)
                return null;

            if (customPaths.TryGetValue(
                game.Id.ToString(),
                out string path))
            {
                return path;
            }

            return null;
        }

        // =====================================================
        // GET EXECUTABLE PATH
        // =====================================================

        public string GetExecutablePath(
            Game game,
            GameAction action)
        {
            if (game == null)
                return null;

            // custom override
            if (customPaths.TryGetValue(
                game.Id.ToString(),
                out string customPath))
            {
                if (File.Exists(customPath))
                {
                    logger.Log(
                        $"Using custom executable: {game.Name} -> {customPath}");

                    return customPath;
                }
                else
                {
                    logger.Log(
                        $"Custom executable missing: {game.Name} -> {customPath}");
                }
            }

            // fallback normal
            return taskManager.ResolveExecutable(
                game,
                action);
        }

        // =====================================================
        // PROMPT FOR EXECUTABLE
        // =====================================================

        public bool PromptForExecutable(Game game)
        {
            if (game == null)
                return false;

            try
            {
                var dialog =
                    new OpenFileDialog
                    {
                        Title =
                            $"Select executable for {game.Name}",

                        Filter =
                            "Executable (*.exe)|*.exe",

                        CheckFileExists = true,

                        Multiselect = false
                    };

                if (!string.IsNullOrWhiteSpace(game.InstallDirectory) &&
                    Directory.Exists(game.InstallDirectory))
                {
                    dialog.InitialDirectory =
                        game.InstallDirectory;
                }

                bool? result =
                    dialog.ShowDialog();

                if (result != true)
                {
                    logger.Log(
                        $"Executable selection canceled: {game.Name}");

                    return false;
                }

                string selected = dialog.FileName;

                // Validate that the selected file is actually a .exe
                // The Windows file dialog can show .lnk shortcuts without
                // their extension, which would cause FocusGuard to watch
                // for a process that never starts.
                if (!selected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    logger.Log(
                        $"Selected file is not an executable: {selected}");

                    System.Windows.MessageBox.Show(
                        $"The selected file is not an executable (.exe):\n{selected}\n\nPlease select the actual game executable, not a shortcut.",
                        "GameTask — Invalid Selection",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);

                    return false;
                }

                if (!File.Exists(selected))
                {
                    logger.Log(
                        $"Selected executable does not exist: {selected}");

                    return false;
                }

                customPaths[game.Id.ToString()] =
                    selected;

                SaveCustomPaths();

                logger.Log(
                    $"Custom executable selected: {game.Name} -> {selected}");

                return true;
            }
            catch (Exception ex)
            {
                logger.Log(
                    $"ERROR selecting executable: {ex}");

                return false;
            }
        }

        // =====================================================
        // SET CUSTOM PATH
        // =====================================================

        public void SetCustomPath(
            Game game,
            string exePath)
        {
            if (game == null)
                return;

            if (string.IsNullOrWhiteSpace(exePath))
                return;

            customPaths[game.Id.ToString()] =
                exePath;

            SaveCustomPaths();

            logger.Log(
                $"Custom path set: {game.Name} -> {exePath}");
        }

        // =====================================================
        // REMOVE CUSTOM PATH
        // =====================================================

        public void RemoveCustomPath(Game game)
        {
            if (game == null)
                return;

            string key = game.Id.ToString();

            if (customPaths.ContainsKey(key))
            {
                customPaths.Remove(key);

                SaveCustomPaths();

                logger.Log(
                    $"Removed custom path: {game.Name}");
            }
        }

        // =====================================================
        // CHECK CUSTOM PATH
        // =====================================================

        public bool HasCustomPath(Game game)
        {
            if (game == null)
                return false;

            return customPaths.ContainsKey(game.Id.ToString());
        }
    }
}
