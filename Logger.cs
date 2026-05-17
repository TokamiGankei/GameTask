using System;
using System.IO;

namespace GameTaskPlugin
{
    public class Logger
    {
        private readonly string logPath;

        public Logger(string pluginDataPath)
        {
            string logsFolder = Path.Combine(pluginDataPath, "Logs");
            Directory.CreateDirectory(logsFolder);

            logPath = Path.Combine(logsFolder, "GameTask.log");
        }

        public void Log(string text)
        {
            try
            {
                File.AppendAllText(
                    logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}" + Environment.NewLine);
            }
            catch { }
        }
    }
}