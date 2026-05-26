using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

using Playnite.SDK;
using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    // =========================================================
    // Row model
    // =========================================================
    public class GameDiagnosticRow
    {
        public string Name        { get; set; }
        public string TaskStatus  { get; set; }
        public string ExeName     { get; set; }
        public string FocusStatus { get; set; }
        public string CustomPath  { get; set; }
        public bool   HasIssue    { get; set; }
    }

    // =========================================================
    // ViewModel — implements INotifyPropertyChanged directly
    // to avoid dependency on ObservableObject from PlayniteSDK
    // =========================================================
    public class DiagnosticsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private readonly IPlayniteAPI   api;
        private readonly GameTaskPlugin plugin;
        private readonly PathManager    pathManager;
        private readonly TaskManager    taskManager;
        private readonly string         actionName;

        private ObservableCollection<GameDiagnosticRow> games;
        public ObservableCollection<GameDiagnosticRow> Games
        {
            get => games;
            set { games = value; OnPropertyChanged(); }
        }

        private string summary;
        public string Summary
        {
            get => summary;
            set { summary = value; OnPropertyChanged(); }
        }

        public ICommand FixAllCommand    { get; }
        public ICommand RepairAllCommand { get; }
        public ICommand RefreshCommand   { get; }

        public DiagnosticsViewModel(
            IPlayniteAPI   api,
            GameTaskPlugin plugin,
            PathManager    pathManager,
            TaskManager    taskManager,
            string         actionName)
        {
            this.api         = api;
            this.plugin      = plugin;
            this.pathManager = pathManager;
            this.taskManager = taskManager;
            this.actionName  = actionName;

            FixAllCommand    = new RelayCommand(_ => plugin.InvokeFixAllUnknownExecutables());
            RepairAllCommand = new RelayCommand(_ => { plugin.InvokeRepairAll(); Refresh(); });
            RefreshCommand   = new RelayCommand(_ => Refresh());

            Refresh();
        }

        public void Refresh()
        {
            var rows = new ObservableCollection<GameDiagnosticRow>();

            foreach (var game in api.Database.Games
                .Where(plugin.HasGameTaskTagPublic)
                .OrderBy(g => g.Name))
            {
                string customExe   = pathManager.GetCustomPath(game);
                string resolvedExe = plugin.ResolveExePathPublic(game);
                string exeFileName = string.IsNullOrWhiteSpace(resolvedExe)
                    ? "❌ Unknown"
                    : Path.GetFileName(resolvedExe);

                bool taskExists  = TaskExists(game);
                bool hasExe      = !string.IsNullOrWhiteSpace(resolvedExe);
                bool hasIssue    = !taskExists || !hasExe;

                rows.Add(new GameDiagnosticRow
                {
                    Name        = game.Name,
                    TaskStatus  = taskExists ? "✅ Created"  : "❌ Missing",
                    ExeName     = exeFileName,
                    FocusStatus = hasExe     ? "✅ Active"   : "❌ Inactive",
                    CustomPath  = string.IsNullOrWhiteSpace(customExe) ? "" : customExe,
                    HasIssue    = hasIssue
                });
            }

            Games   = rows;
            int total  = rows.Count;
            int issues = rows.Count(r => r.HasIssue);
            Summary = issues == 0
                ? $"{total} game(s) — all OK"
                : $"{total} game(s) — {issues} with issues (shown in red)";
        }

        private bool TaskExists(Game game)
        {
            string taskName = TaskManager.GetTaskName(game);
            try
            {
                using var proc = new Process();
                proc.StartInfo.FileName        = "schtasks.exe";
                proc.StartInfo.Arguments       = $"/query /tn \"\\GameTask\\{taskName}\"";
                proc.StartInfo.CreateNoWindow  = true;
                proc.StartInfo.UseShellExecute = false;
                proc.Start();
                proc.WaitForExit();
                return proc.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    // =========================================================
    // Simple RelayCommand
    // =========================================================
    public class RelayCommand : ICommand
    {
        private readonly Action<object>      execute;
        private readonly Func<object, bool>  canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            this.execute    = execute;
            this.canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add    { System.Windows.Input.CommandManager.RequerySuggested += value; }
            remove { System.Windows.Input.CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => canExecute?.Invoke(parameter) ?? true;
        public void Execute(object parameter)    => execute(parameter);
    }
}
