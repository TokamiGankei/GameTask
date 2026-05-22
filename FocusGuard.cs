using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using Playnite.SDK.Models;

namespace GameTaskPlugin
{
    /// <summary>
    /// Monitors foreground window changes via SetWinEventHook and immediately
    /// reclaims focus for the game window whenever anything steals it.
    /// Runs on a dedicated STA thread (required for Win32 message pump + hooks).
    /// Active for a configurable number of seconds after the game window appears.
    /// </summary>
    public class FocusGuard : IDisposable
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
        static extern bool GetMessage(out MSG msg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

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
        const uint WM_QUIT                 = 0x0012;
        const uint WM_USER_STOP            = 0x0400 + 1;

        #endregion

        private readonly Logger logger;
        private readonly int    guardSeconds;

        private Thread    staThread;
        private uint      staThreadId;
        private IntPtr    hookHandle = IntPtr.Zero;
        private IntPtr    gameHwnd   = IntPtr.Zero;
        private int       gamePid    = 0;
        private bool      disposed   = false;

        // Keep delegate alive — GC must not collect it while hook is active
        private WinEventProc hookDelegate;

        public FocusGuard(Logger logger, int guardSeconds = 20)
        {
            this.logger       = logger;
            this.guardSeconds = guardSeconds;
        }

        /// <summary>
        /// Starts watching for the game process and guarding its focus.
        /// </summary>
        public void StartAsync(string exeName)
        {
            if (staThread != null && staThread.IsAlive)
                Stop();

            staThread = new Thread(() => RunLoop(exeName));
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Name         = "GameTask.FocusGuard";
            staThread.Start();
        }

        public void Stop()
        {
            if (staThreadId != 0)
                PostThreadMessage(staThreadId, WM_USER_STOP, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            disposed = true;
            Stop();
        }

        // =====================================================================
        // STA thread — message pump + hook
        // =====================================================================

        private void RunLoop(string exeName)
        {
            staThreadId = GetCurrentThreadId();
            logger.Log($"FocusGuard started for: {exeName}");

            // Step 1 — wait up to 60 s for the process
            Process proc = WaitForProcess(exeName, timeoutMs: 60_000);
            if (proc == null)
            {
                logger.Log("FocusGuard: process not found within timeout.");
                return;
            }

            gamePid = proc.Id;
            logger.Log($"FocusGuard: process found PID={gamePid}");

            // Step 2 — wait up to 30 s for a window handle
            gameHwnd = WaitForWindow(proc, timeoutMs: 30_000);
            if (gameHwnd == IntPtr.Zero)
            {
                logger.Log("FocusGuard: window handle not found within timeout.");
                return;
            }

            logger.Log($"FocusGuard: window found HWND={gameHwnd}");

            // Step 3 — install hook and run message pump
            hookDelegate = OnForegroundChanged;
            hookHandle   = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

            // Initial push
            ForceForeground();

            // Schedule stop after guardSeconds
            var timer = new System.Threading.Timer(_ =>
            {
                if (staThreadId != 0)
                    PostThreadMessage(staThreadId, WM_USER_STOP, IntPtr.Zero, IntPtr.Zero);
            }, null, guardSeconds * 1000, Timeout.Infinite);

            // Message pump — required for WINEVENT_OUTOFCONTEXT hooks to fire
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0))
            {
                if (msg.message == WM_USER_STOP) break;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            timer.Dispose();

            if (hookHandle != IntPtr.Zero)
            {
                UnhookWinEvent(hookHandle);
                hookHandle = IntPtr.Zero;
            }

            logger.Log("FocusGuard: stopped.");
        }

        private void OnForegroundChanged(IntPtr hHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint eventThread, uint eventTime)
        {
            if (hwnd == gameHwnd) return; // game already in foreground
            ForceForeground();
        }

        private void ForceForeground()
        {
            var hwnd = gameHwnd;
            if (hwnd == IntPtr.Zero) return;

            if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE

            AllowSetForegroundWindow(gamePid);

            var fg      = GetForegroundWindow();
            uint dummy  = 0;
            var fgTid   = GetWindowThreadProcessId(fg, out dummy);
            var myTid   = GetCurrentThreadId();

            if (fgTid != 0 && fgTid != myTid)
            {
                AttachThreadInput(myTid, fgTid, true);
                SetForegroundWindow(hwnd);
                ShowWindow(hwnd, 9);
                AttachThreadInput(myTid, fgTid, false);
            }
            else
            {
                SetForegroundWindow(hwnd);
            }

            logger.Log("FocusGuard: foreground reclaimed.");
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static Process WaitForProcess(string exeName, int timeoutMs)
        {
            string target   = System.IO.Path.GetFileNameWithoutExtension(exeName);
            int    elapsed  = 0;
            const int delay = 500;

            while (elapsed < timeoutMs)
            {
                Thread.Sleep(delay);
                elapsed += delay;

                var candidates = Process.GetProcessesByName(target);
                if (candidates.Length > 0)
                {
                    Array.Sort(candidates, (a, b) => b.StartTime.CompareTo(a.StartTime));
                    return candidates[0];
                }
            }

            return null;
        }

        private static IntPtr WaitForWindow(Process proc, int timeoutMs)
        {
            int   elapsed = 0;
            const int delay = 500;

            while (elapsed < timeoutMs)
            {
                Thread.Sleep(delay);
                elapsed += delay;

                try { proc.Refresh(); } catch { return IntPtr.Zero; }

                if (proc.MainWindowHandle != IntPtr.Zero)
                    return proc.MainWindowHandle;
            }

            return IntPtr.Zero;
        }
    }
}
