using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Newtonsoft.Json;

using Playnite.SDK;
using Playnite.SDK.Plugins;

namespace GameTaskPlugin
{
    // =========================================================
    // MODEL — serialized to Config/Settings.json
    // =========================================================
    public class PluginSettings : ObservableObject
    {
        private bool bringWindowToForeground = true;
        private bool detectOrphanTasks       = true;
        private bool detectCorruptedTasks    = true;

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
    }

    // =========================================================
    // SETTINGS PROVIDER — implements ISettings for Playnite's
    // native settings page (Settings → Plugins → GameTask)
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
                DetectCorruptedTasks    = Settings.DetectCorruptedTasks
            };
        }

        public void CancelEdit()
        {
            Settings.BringWindowToForeground = snapshot.BringWindowToForeground;
            Settings.DetectOrphanTasks       = snapshot.DetectOrphanTasks;
            Settings.DetectCorruptedTasks    = snapshot.DetectCorruptedTasks;
        }

        public void EndEdit()
        {
            settingsManager.Save();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }
    }

    // =========================================================
    // PERSISTENCE — loads / saves Settings.json
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

            settingsFile = Path.Combine(configFolder, "Settings.json");
            Load();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(settingsFile))
                {
                    string json = File.ReadAllText(settingsFile, Encoding.UTF8);
                    current = JsonConvert.DeserializeObject<PluginSettings>(json)
                              ?? new PluginSettings();
                    logger.Log("Settings loaded.");
                }
                else
                {
                    current = new PluginSettings();
                    Save();
                }
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
                string json = JsonConvert.SerializeObject(current, Formatting.Indented);
                File.WriteAllText(settingsFile, json, Encoding.UTF8);
                logger.Log("Settings saved.");
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR saving settings: {ex.Message}");
            }
        }
    }
}
