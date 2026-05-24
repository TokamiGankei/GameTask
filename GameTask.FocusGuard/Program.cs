using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// GameTask.FocusGuard.exe <ExeName.exe> [guardSeconds] [logPath]
/// 
/// Waits for the given process to appear, then aggressively keeps
/// its window in the foreground for guardSeconds (default 20).
/// Also monitors child processes in case the initial process spawns
/// the real game (e.g. launchers, anti-cheat wrappers).
/// Called directly by the game launcher .vbs right after schtasks /run.
/// </summary>
class Program
{
    #region Win32

    delegate void WinEventProc(IntPtr hHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hHook);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool fAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX, ptY;
    }

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    const uint WM_USER_STOP            = 0x0401;

    #endregion

    static IntPtr gameHwnd = IntPtr.Zero;
    static int    gamePid  = 0;
    static string logFile;

    static void Log(string msg)
    {
        try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n"); } catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 0) return;

        string exeName   = Path.GetFileNameWithoutExtension(args[0]);
        int    guardSecs = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 20;

        // Log path — 3rd argument points to plugin data Logs folder
        if (args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]))
        {
            logFile = args[2];
            try { Directory.CreateDirectory(Path.GetDirectoryName(logFile)); } catch { }
        }
        else
        {
            string dir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            logFile = Path.Combine(dir, "FocusGuard.log");
        }

        Log($"=== Started. target={exeName} guard={guardSecs}s ===");

        // Step 1 — wait up to 60 s for the target process OR any child of it
        Process proc = WaitForProcess(exeName, 60_000);
        if (proc == null) { Log("Process not found within timeout."); return; }

        gamePid = proc.Id;
        Log($"Process found. name={proc.ProcessName} PID={gamePid}");

        // Step 2 — wait up to 30 s for a window handle.
        // Also watch child processes — some games spawn a child that owns the actual window
        // (e.g. anti-cheat wrappers, GOG Galaxy launcher, etc.)
        gameHwnd = WaitForWindowWithChildren(proc, 30_000);
        if (gameHwnd == IntPtr.Zero) { Log("Window handle not found within timeout."); return; }

        Log($"Window found. HWND=0x{gameHwnd:X} PID={gamePid}");

        // Step 3 — install foreground event hook + message pump
        uint myTid = GetCurrentThreadId();

        WinEventProc hookDelegate = (hk, evt, hwnd, obj, child, thr, time) =>
        {
            if (hwnd == gameHwnd) return;
            Log($"Foreground stolen by HWND=0x{hwnd:X} — reclaiming...");
            ForceForeground();
        };

        IntPtr hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

        Log($"Hook installed. Guarding for {guardSecs}s...");

        // Initial push
        ForceForeground();

        // Stop after guardSecs
        var timer = new Timer(_ =>
            PostThreadMessage(myTid, WM_USER_STOP, IntPtr.Zero, IntPtr.Zero),
            null, guardSecs * 1000, Timeout.Infinite);

        // Message pump — required for hook to fire
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == WM_USER_STOP) break;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        timer.Dispose();
        UnhookWinEvent(hook);
        Log("=== Guard finished. ===");
    }

    static void ForceForeground()
    {
        var hwnd = gameHwnd;
        if (hwnd == IntPtr.Zero) return;

        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);

        AllowSetForegroundWindow(gamePid);

        var    fg    = GetForegroundWindow();
        uint   dummy = 0;
        var    fgTid = GetWindowThreadProcessId(fg, out dummy);
        var    myTid = GetCurrentThreadId();

        if (fgTid != 0 && fgTid != myTid)
        {
            AttachThreadInput(myTid, fgTid, true);
            bool r = SetForegroundWindow(hwnd);
            ShowWindow(hwnd, 9);
            AttachThreadInput(myTid, fgTid, false);
            Log($"SetForegroundWindow (attached) result={r}");
        }
        else
        {
            bool r = SetForegroundWindow(hwnd);
            Log($"SetForegroundWindow result={r}");
        }
    }

    // =========================================================
    // Wait for the target process by name
    // =========================================================
    static Process WaitForProcess(string name, int timeoutMs)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            Thread.Sleep(500);
            elapsed += 500;

            var list = Process.GetProcessesByName(name);
            if (list.Length > 0)
            {
                Array.Sort(list, (a, b) => b.StartTime.CompareTo(a.StartTime));
                return list[0];
            }
        }
        return null;
    }

    // =========================================================
    // Wait for a window handle on the process OR any child process.
    // This handles games that spawn a child process which owns
    // the actual game window (anti-cheat, GOG/Steam helpers, etc.).
    // Note: we do NOT kill or interfere with child processes —
    // we only observe them. Anti-cheat safe.
    // =========================================================
    static IntPtr WaitForWindowWithChildren(Process root, int timeoutMs)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            Thread.Sleep(500);
            elapsed += 500;

            // Check the root process first
            try
            {
                root.Refresh();
                if (root.MainWindowHandle != IntPtr.Zero)
                {
                    Log($"Window on root process after {elapsed}ms");
                    return root.MainWindowHandle;
                }
            }
            catch { return IntPtr.Zero; }

            // Check child processes (read-only, no interference)
            try
            {
                var children = GetChildProcesses(root.Id);
                foreach (var child in children)
                {
                    try
                    {
                        child.Refresh();
                        if (child.MainWindowHandle != IntPtr.Zero)
                        {
                            // Switch target to the child that has the window
                            gamePid  = child.Id;
                            Log($"Window on child process '{child.ProcessName}' PID={gamePid} after {elapsed}ms");
                            return child.MainWindowHandle;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    static Process[] GetChildProcesses(int parentPid)
    {
        var children = new System.Collections.Generic.List<Process>();
        try
        {
            // Use WMI to find child processes — read-only, no interference with anti-cheat
            var query = $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parentPid}";
            using (var searcher = new System.Management.ManagementObjectSearcher(query))
            {
                foreach (var obj in searcher.Get())
                {
                    int pid = Convert.ToInt32(obj["ProcessId"]);
                    try { children.Add(Process.GetProcessById(pid)); } catch { }
                }
            }
        }
        catch { }
        return children.ToArray();
    }
}
