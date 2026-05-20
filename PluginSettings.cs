using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace GameTaskPlugin
{
    public class PluginSettings
    {
        public bool BringWindowToForeground { get; set; } = true;
        public bool DetectOrphanTasks       { get; set; } = true;
    }

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
