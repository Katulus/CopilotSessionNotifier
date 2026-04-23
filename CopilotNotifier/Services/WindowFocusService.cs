using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CopilotNotifier.Services;

public static class WindowFocusService
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    public static bool FocusTerminalWindow(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            var hWnd = process.MainWindowHandle;

            if (hWnd == IntPtr.Zero)
            {
                hWnd = FindWindowForProcess(pid);
            }

            if (hWnd == IntPtr.Zero)
                return false;

            if (IsIconic(hWnd))
                ShowWindow(hWnd, SW_RESTORE);

            var foregroundWnd = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foregroundWnd, out _);
            var currentThread = GetCurrentThreadId();

            if (foregroundThread != currentThread)
            {
                AttachThreadInput(currentThread, foregroundThread, true);
                SetForegroundWindow(hWnd);
                AttachThreadInput(currentThread, foregroundThread, false);
            }
            else
            {
                SetForegroundWindow(hWnd);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr FindWindowForProcess(int pid)
    {
        // Walk up the process tree to find a window
        try
        {
            var process = Process.GetProcessById(pid);

            // Check parent processes (terminal hosts like Windows Terminal, cmd, powershell)
            var candidates = Process.GetProcessesByName("WindowsTerminal")
                .Concat(Process.GetProcessesByName("cmd"))
                .Concat(Process.GetProcessesByName("powershell"))
                .Concat(Process.GetProcessesByName("pwsh"))
                .Concat(Process.GetProcessesByName("Code"));

            foreach (var candidate in candidates)
            {
                if (candidate.MainWindowHandle != IntPtr.Zero)
                {
                    // Check if this window's process tree contains our PID
                    // Simple heuristic: just try the first visible terminal window
                    if (IsWindowVisible(candidate.MainWindowHandle))
                        return candidate.MainWindowHandle;
                }
            }
        }
        catch { }

        return IntPtr.Zero;
    }


}
