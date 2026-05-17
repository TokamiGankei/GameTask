using System;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    public class NotificationManager
    {
        private readonly IPlayniteAPI api;

        private readonly Action callback;

        private readonly string pendingTasksFile;

        public NotificationManager(
            IPlayniteAPI api,
            Action callback,
            string pluginDataPath)
        {
            this.api = api;

            this.callback = callback;

            string cacheFolder =
                Path.Combine(
                    pluginDataPath,
                    "Cache");

            Directory.CreateDirectory(cacheFolder);

            pendingTasksFile =
                Path.Combine(
                    cacheFolder,
                    "PendingTasks.txt");
        }

        // =====================================================
        // PENDING TASKS NOTIFICATION
        // =====================================================

        public void ShowPendingNotification()
        {
            if (!File.Exists(pendingTasksFile))
                return;

            var lines =
                File.ReadAllLines(pendingTasksFile);

            int count = 0;

            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    count++;
            }

            if (count <= 0)
            {
                api.Notifications.Remove(
                    "GameTaskPending");

                return;
            }

            api.Notifications.Add(
                new NotificationMessage(
                    "GameTaskPending",

                    $"GameTask: {count} game(s) need elevated tasks. Click here.",

                    NotificationType.Info,

                    () => callback()
                )
            );
        }

        // =====================================================
        // EXECUTABLE FIX NOTIFICATION
        // =====================================================

        public void ShowExecutableFixNotification(
            Game game,
            Action fixAction)
        {
            if (game == null)
                return;

            string notificationId =
                $"GameTaskFix_{game.Id}";

            // evita duplicação
            api.Notifications.Remove(
                notificationId);

            api.Notifications.Add(
                new NotificationMessage(
                    notificationId,

                    $"GameTask: '{game.Name}' needs executable path adjustment. Click to fix.",

                    NotificationType.Error,

                    () =>
                    {
                        try
                        {
                            fixAction?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            api.Dialogs.ShowErrorMessage(
                                ex.ToString(),
                                "GameTask");
                        }
                    }
                )
            );
        }

        // =====================================================
        // REMOVE EXECUTABLE FIX NOTIFICATION
        // =====================================================

        public void RemoveExecutableFixNotification(
            Game game)
        {
            if (game == null)
                return;

            string notificationId =
                $"GameTaskFix_{game.Id}";

            api.Notifications.Remove(
                notificationId);
        }

        // =====================================================
        // CLEAR ALL GAMETASK NOTIFICATIONS
        // =====================================================

        public void ClearAllNotifications()
        {
            api.Notifications.Remove(
                "GameTaskPending");
        }

        // =====================================================
        // SHOW SIMPLE INFO
        // =====================================================

        public void ShowInfo(
            string message)
        {
            api.Notifications.Add(
                new NotificationMessage(
                    Guid.NewGuid().ToString(),

                    $"GameTask: {message}",

                    NotificationType.Info
                )
            );
        }

        // =====================================================
        // SHOW SIMPLE ERROR
        // =====================================================

        public void ShowError(
            string message)
        {
            api.Notifications.Add(
                new NotificationMessage(
                    Guid.NewGuid().ToString(),

                    $"GameTask: {message}",

                    NotificationType.Error
                )
            );
        }
    }
}