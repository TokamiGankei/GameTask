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
    // Row model — includes per-row action commands
    // =========================================================
    public class GameDiagnosticRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public Game   Game          { get; set; }
        public string Name          { get; set; }
        public string TaskStatus    { get; set; }
        public string ExeName       { get; set; }
        public string FocusStatus   { get; set; }
        public string CustomPath    { get; set; }
        public string IssueHint     { get; set; }
        public bool   HasIssue      { get; set; }
        public bool   HasNoAction   { get; set; }

        // Per-row commands
        public ICommand RepairCommand  { get; set; }
        public ICommand FixExeCommand  { get; set; }
        public ICommand DisableCommand { get; set; }
    }

    // =========================================================
    // ViewModel
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

        private bool isLoading;
        public bool IsLoading
        {
            get => isLoading;
            set { isLoading = value; OnPropertyChanged(); }
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
            Summary   = "Loading...";
            IsLoading = true;
            Games     = new ObservableCollection<GameDiagnosticRow>();

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
                bool hasNoAction = plugin.HasNoGameAction(game);
                bool hasIssue    = !taskExists || !hasExe;

                string issueHint = "";
                if (!taskExists && !hasExe)
                    issueHint = hasNoAction
                        ? "No GameAction configured — add a Play action in Game Details → Actions"
                        : "Task missing and exe unknown — use Fix Executable Path";
                else if (!taskExists)
                    issueHint = "Task missing — use Repair";
                else if (!hasExe)
                    issueHint = hasNoAction
                        ? "No GameAction configured — add a Play action in Game Details → Actions"
                        : "Exe unknown — use Fix Executable Path";

                var row = new GameDiagnosticRow
                {
                    Game        = game,
                    Name        = game.Name,
                    TaskStatus  = taskExists ? "✅ Created"  : "❌ Missing",
                    ExeName     = exeFileName,
                    FocusStatus = hasExe     ? "✅ Active"   : "❌ Inactive",
                    CustomPath  = string.IsNullOrWhiteSpace(customExe) ? "" : customExe,
                    IssueHint   = issueHint,
                    HasIssue    = hasIssue,
                    HasNoAction = hasNoAction
                };

                // Per-row commands
                var capturedGame = game;
                row.RepairCommand  = new RelayCommand(_ => { plugin.InvokeRepairGame(capturedGame); Refresh(); });
                row.FixExeCommand  = new RelayCommand(_ => { plugin.InvokeFixExecutablePath(capturedGame); Refresh(); },
                                                      _ => !hasNoAction);
                row.DisableCommand = new RelayCommand(_ => { plugin.InvokeDisableGame(capturedGame); Refresh(); });

                rows.Add(row);
            }

            Games     = rows;
            IsLoading = false;

            int total  = rows.Count;
            int issues = rows.Count(r => r.HasIssue);
            Summary = issues == 0
                ? $"{total} game(s) — all OK ✅"
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
        private readonly Action<object>     execute;
        private readonly Func<object, bool> canExecute;

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
