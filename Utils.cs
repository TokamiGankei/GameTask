using System.IO;
using System.Text.RegularExpressions;
using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    public static class Utils
    {
        public static string MakeSafeFileName(string text)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                text = text.Replace(c, '_');
            }

            return text;
        }

        public static string MakeSafeTaskName(string text)
        {
            return Regex.Replace(text, @"[^a-zA-Z0-9_\- ]", "_");
        }

        public static string ResolveExecutablePath(Game game, string actionNameToIgnore)
        {
            if (game.GameActions == null)
                return null;

            foreach (var action in game.GameActions)
            {
                if (action.Name == actionNameToIgnore)
                    continue;

                if (string.IsNullOrWhiteSpace(action.Path))
                    continue;

                string path = action.Path;

                if (Path.IsPathRooted(path))
                    return path;

                if (!string.IsNullOrWhiteSpace(game.InstallDirectory))
                    return Path.Combine(game.InstallDirectory, path);

                return path;
            }

            return null;
        }
    }
}