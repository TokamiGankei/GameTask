using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Playnite.SDK;
using Playnite.SDK.Plugins;

namespace GameTaskPlugin
{
    // =========================================================
    // MODEL
    // =========================================================
    public class PluginSettings : ObservableObject
    {
        private bool bringWindowToForeground = true;
        private bool detectOrphanTasks       = true;
        private bool detectCorruptedTasks    = true;
        private bool lowPerformanceMode      = false;
        private int  guardSeconds            = 20;

        public bool BringWindowToForeground
        {
            get => bringWindowToForeground;
            set => SetValue(ref bringWindowToForeground, value);
        }

        public bool DetectOrphanTasks
        {
            get => detectOrphanTasks;
            set => SetValue(ref detectOrphanTasks, value);
        }

        public bool DetectCorruptedTasks
        {
            get => detectCorruptedTasks;
            set => SetValue(ref detectCorruptedTasks, value);
        }

        public bool LowPerformanceMode
        {
            get => lowPerformanceMode;
            set => SetValue(ref lowPerformanceMode, value);
        }

        /// <summary>How long FocusGuard keeps the game in the foreground after launch.</summary>
        public int GuardSeconds
        {
            get => guardSeconds;
            set => SetValue(ref guardSeconds, Math.Max(5, Math.Min(120, value)));
        }

        // =========================================================
        // FocusGuard parameters — derived from LowPerformanceMode
        // =========================================================

        /// <summary>Max ms to wait for the game process to appear.</summary>
        public int FocusProcessTimeoutMs  => LowPerformanceMode ? 120_000 : 60_000;

        /// <summary>Max ms to wait for the game window handle to appear.</summary>
        public int FocusWindowTimeoutMs   => LowPerformanceMode ? 60_000  : 30_000;

        /// <summary>Number of aggressive foreground pushes right after window appears.</summary>
        public int FocusEarlyPushCount    => LowPerformanceMode ? 8       : 4;

        /// <summary>Interval in ms between early pushes.</summary>
        public int FocusEarlyPushInterval => LowPerformanceMode ? 250     : 300;
    }

    // =========================================================
    // SETTINGS PROVIDER
    // =========================================================
    public class GameTaskSettings : ISettings
    {
        private readonly GameTaskPlugin  plugin;
        private readonly SettingsManager settingsManager;

        public PluginSettings Settings => settingsManager.Current;

        private PluginSettings snapshot;

        public GameTaskSettings(GameTaskPlugin plugin, SettingsManager settingsManager)
        {
            this.plugin          = plugin;
            this.settingsManager = settingsManager;
        }

        public void BeginEdit()
        {
            snapshot = new PluginSettings
            {
                BringWindowToForeground = Settings.BringWindowToForeground,
                DetectOrphanTasks       = Settings.DetectOrphanTasks,
                DetectCorruptedTasks    = Settings.DetectCorruptedTasks,
                LowPerformanceMode      = Settings.LowPerformanceMode,
                GuardSeconds            = Settings.GuardSeconds
            };
        }

        public void CancelEdit()
        {
            Settings.BringWindowToForeground = snapshot.BringWindowToForeground;
            Settings.DetectOrphanTasks       = snapshot.DetectOrphanTasks;
            Settings.DetectCorruptedTasks    = snapshot.DetectCorruptedTasks;
            Settings.LowPerformanceMode      = snapshot.LowPerformanceMode;
            Settings.GuardSeconds            = snapshot.GuardSeconds;
        }

        public void EndEdit() => settingsManager.Save();

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }
    }

    // =========================================================
    // PERSISTENCE — simple key=value format, no external deps
    // =========================================================
    public class SettingsManager
    {
        private readonly Logger logger;
        private readonly string settingsFile;

        private PluginSettings current;
        public PluginSettings Current => current;

        public SettingsManager(Logger logger, string pluginDataPath)
        {
            this.logger = logger;

            string configFolder = Path.Combine(pluginDataPath, "Config");
            Directory.CreateDirectory(configFolder);

            settingsFile = Path.Combine(configFolder, "Settings.ini");
            Load();
        }

        private void Load()
        {
            current = new PluginSettings();

            try
            {
                if (!File.Exists(settingsFile))
                {
                    Save();
                    return;
                }

                foreach (var line in File.ReadAllLines(settingsFile, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                    var parts = line.Split('=');
                    if (parts.Length != 2) continue;

                    string key   = parts[0].Trim();
                    string value = parts[1].Trim();

                    switch (key)
                    {
                        case "BringWindowToForeground":
                            current.BringWindowToForeground = value == "true"; break;
                        case "DetectOrphanTasks":
                            current.DetectOrphanTasks = value == "true"; break;
                        case "DetectCorruptedTasks":
                            current.DetectCorruptedTasks = value == "true"; break;
                        case "LowPerformanceMode":
                            current.LowPerformanceMode = value == "true"; break;
                        case "GuardSeconds":
                            if (int.TryParse(value, out int gs)) current.GuardSeconds = gs; break;
                    }
                }

                logger.Log("Settings loaded.");
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR loading settings: {ex.Message}");
                current = new PluginSettings();
            }
        }

        public void Save()
        {
            try
            {
                var lines = new[]
                {
                    "# GameTask Settings",
                    $"BringWindowToForeground={BoolToStr(current.BringWindowToForeground)}",
                    $"DetectOrphanTasks={BoolToStr(current.DetectOrphanTasks)}",
                    $"DetectCorruptedTasks={BoolToStr(current.DetectCorruptedTasks)}",
                    $"LowPerformanceMode={BoolToStr(current.LowPerformanceMode)}",
                    $"GuardSeconds={current.GuardSeconds}"
                };

                File.WriteAllLines(settingsFile, lines, Encoding.UTF8);
                logger.Log("Settings saved.");
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR saving settings: {ex.Message}");
            }
        }

        private static string BoolToStr(bool value) => value ? "true" : "false";
    }
}
