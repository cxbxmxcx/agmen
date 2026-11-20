using System.Runtime.InteropServices;
using System.Text;

namespace Agmen.Services;

/// <summary>
/// Finds the top-level window hosting a Claude Code session (the CLI itself is
/// a console process with no window) by walking up the parent-process chain,
/// then brings that window to the foreground.
/// </summary>
public static class WindowFocusService
{
    public static bool FocusSession(int claudePid)
    {
        foreach (var node in ProcessTree.Ancestry(claudePid, maxDepth: 12))
        {
            var hwnd = FindMainWindow(node.Pid);
            if (hwnd != IntPtr.Zero)
                return BringToForeground(hwnd);
        }
        return false;
    }

    private static IntPtr FindMainWindow(int pid)
    {
        IntPtr best = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint windowPid);
            if (windowPid != (uint)pid) return true;
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true; // owned popup

            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0) return true;

            best = hwnd;
            return false; // stop at the first real titled window
        }, IntPtr.Zero);
        return best;
    }

    private static bool BringToForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint ourThread = GetCurrentThreadId();

        // Attach input queues so Windows permits the foreground switch.
        if (foregroundThread != ourThread) AttachThreadInput(ourThread, foregroundThread, true);
        if (targetThread != ourThread) AttachThreadInput(ourThread, targetThread, true);
        try
        {
            BringWindowToTop(hwnd);
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            if (foregroundThread != ourThread) AttachThreadInput(ourThread, foregroundThread, false);
            if (targetThread != ourThread) AttachThreadInput(ourThread, targetThread, false);
        }
    }

    // ------------------------------------------------------------------ P/Invoke

    private const uint GW_OWNER = 4;
    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
}
