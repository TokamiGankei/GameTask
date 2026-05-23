using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// GameTask.FocusGuard.exe <ExeName.exe> [guardSeconds]
///
/// Waits for the given process to appear, then aggressively keeps
/// its window in the foreground for guardSeconds (default 20).
/// Called directly by the game launcher .vbs right after schtasks /run.
/// Runs as a hidden WinExe — no console window.
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
    const uint WM_USER_STOP            = 0x0400 + 1;

    #endregion

    static IntPtr gameHwnd = IntPtr.Zero;
    static int    gamePid  = 0;

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 0) return;

        string exeName     = System.IO.Path.GetFileNameWithoutExtension(args[0]);
        int    guardSecs   = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 20;

        // Step 1 — wait up to 60 s for the process
        Process proc = WaitForProcess(exeName, 60_000);
        if (proc == null) return;

        gamePid = proc.Id;

        // Step 2 — wait up to 30 s for a window handle
        gameHwnd = WaitForWindow(proc, 30_000);
        if (gameHwnd == IntPtr.Zero) return;

        // Step 3 — install hook + message pump
        uint myTid = GetCurrentThreadId();

        WinEventProc hookDelegate = (hk, evt, hwnd, obj, child, thr, time) =>
        {
            if (hwnd == gameHwnd) return;
            ForceForeground();
        };

        IntPtr hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

        // Initial push
        ForceForeground();

        // Stop after guardSecs
        var timer = new System.Threading.Timer(_ =>
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
    }

    static void ForceForeground()
    {
        var hwnd = gameHwnd;
        if (hwnd == IntPtr.Zero) return;

        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);

        AllowSetForegroundWindow(gamePid);

        var    fg     = GetForegroundWindow();
        uint   dummy  = 0;
        var    fgTid  = GetWindowThreadProcessId(fg, out dummy);
        var    myTid  = GetCurrentThreadId();

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
    }

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

    static IntPtr WaitForWindow(Process proc, int timeoutMs)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            Thread.Sleep(500);
            elapsed += 500;
            try { proc.Refresh(); } catch { return IntPtr.Zero; }
            if (proc.MainWindowHandle != IntPtr.Zero)
                return proc.MainWindowHandle;
        }
        return IntPtr.Zero;
    }
}
