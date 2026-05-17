namespace GameTaskPlugin
{
    /// <summary>
    /// TrackerManager — Reserved for future advanced tracking support.
    ///
    /// CURRENT STATE:
    ///   GameTask currently relies entirely on Playnite's official built-in
    ///   tracking system (TrackingMode.ProcessName), configured in ActionManager.
    ///   This means playtime is recorded automatically as long as the game's
    ///   main executable is correctly set via ActionManager or PathManager.
    ///
    /// WHY THIS CLASS EXISTS:
    ///   Some games launch through intermediate launchers or splash screens,
    ///   making process-name tracking unreliable. This module is reserved as
    ///   the integration point for more advanced strategies in the future.
    ///
    /// FOR FUTURE CONTRIBUTORS — possible tracking approaches to explore:
    ///
    ///   1. Child process detection:
    ///      Monitor child processes spawned by the main executable.
    ///      Useful when a launcher starts the real game as a subprocess.
    ///      See: System.Diagnostics.Process.GetProcessById / ManagementObjectSearcher (WMI).
    ///
    ///   2. Window title tracking:
    ///      Poll for a window with a known title instead of a process name.
    ///      Useful when the process name is generic (e.g. "GameService.exe").
    ///      See: Win32 API FindWindow / EnumWindows via P/Invoke.
    ///
    ///   3. File access monitoring:
    ///      Detect when a game-specific save file or config is being written,
    ///      as a proxy signal that the game is running.
    ///
    ///   Integration point:
    ///      When implemented, this class should expose Start(Game) and Stop(Game)
    ///      methods, called from GameTaskPlugin.OnGameStarted / OnGameStopped.
    ///      Playnite SDK events: OnGameStarted, OnGameStopped, OnGameStarting.
    /// </summary>
    public class TrackerManager
    {
        private readonly Logger logger;

        public TrackerManager(Logger logger)
        {
            this.logger = logger;
            logger.Log("TrackerManager loaded. Using official Playnite tracking only.");
        }
    }
}
