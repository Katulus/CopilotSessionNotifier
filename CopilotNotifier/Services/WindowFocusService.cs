using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Automation;

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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const int SW_RESTORE = 9;

    public static bool FocusTerminalWindow(int pid, string? sessionName = null)
    {
        try
        {
            // Walk up the process tree to find the nearest ancestor with a window
            var (windowHandle, wtPid, shellPid) = FindHostingWindow(pid);
            if (windowHandle == IntPtr.Zero)
                return false;

            // Bring the window to front first (this always works)
            BringWindowToFront(windowHandle);

            // If we found a Windows Terminal, try to switch to the correct tab
            if (wtPid > 0)
            {
                TrySwitchToTab(windowHandle, sessionName, wtPid, shellPid);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Walks up from pid to find the first ancestor with a visible window.
    /// Returns (windowHandle, wtProcessId if WT, directShellPid under WT).
    /// </summary>
    private static (IntPtr handle, int wtPid, int shellPid) FindHostingWindow(int startPid)
    {
        int previousPid = startPid;
        int currentPid = startPid;

        for (int i = 0; i < 20; i++)
        {
            try
            {
                var proc = Process.GetProcessById(currentPid);
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    bool isWt = proc.ProcessName.Equals("WindowsTerminal",
                        StringComparison.OrdinalIgnoreCase);
                    return (proc.MainWindowHandle, isWt ? currentPid : 0, previousPid);
                }

                // Move up
                previousPid = currentPid;
                currentPid = GetParentPid(currentPid);
                if (currentPid <= 0)
                    break;
            }
            catch
            {
                break;
            }
        }

        return (IntPtr.Zero, 0, 0);
    }

    /// <summary>
    /// Attempts to switch to the correct tab in Windows Terminal.
    /// Primary strategy: match tab name against session name.
    /// Fallback: match via process tree ancestry.
    /// </summary>
    private static void TrySwitchToTab(IntPtr wtHandle, string? sessionName, int wtPid, int shellPid)
    {
        try
        {
            var wtElement = AutomationElement.FromHandle(wtHandle);
            if (wtElement == null) return;

            var tabItems = wtElement.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

            if (tabItems.Count <= 1)
                return;

            // Strategy 1: Match by tab name containing session name
            if (!string.IsNullOrWhiteSpace(sessionName))
            {
                foreach (AutomationElement tab in tabItems)
                {
                    var tabName = tab.Current.Name ?? "";
                    if (tabName.Contains(sessionName, StringComparison.OrdinalIgnoreCase)
                        || sessionName.Contains(tabName, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectTab(tab);
                        return;
                    }
                }

                // Try partial match: first 40 chars of either
                var shortSession = sessionName.Length > 40 ? sessionName[..40] : sessionName;
                foreach (AutomationElement tab in tabItems)
                {
                    var tabName = tab.Current.Name ?? "";
                    var shortTab = tabName.Length > 40 ? tabName[..40] : tabName;
                    if (shortTab.Contains(shortSession, StringComparison.OrdinalIgnoreCase)
                        || shortSession.Contains(shortTab, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectTab(tab);
                        return;
                    }
                }
            }

            // Strategy 2: Match via process tree — find which shell child of WT
            // is an ancestor of our target process
            if (shellPid > 0)
            {
                var shellPids = GetDirectChildPids(wtPid)
                    .Where(pid =>
                    {
                        try
                        {
                            var p = Process.GetProcessById(pid);
                            var name = p.ProcessName.ToLowerInvariant();
                            return name is "pwsh" or "powershell" or "cmd" or "bash" or "wsl" or "zsh";
                        }
                        catch { return false; }
                    })
                    .ToList();

                for (int i = 0; i < shellPids.Count && i < tabItems.Count; i++)
                {
                    if (shellPids[i] == shellPid || IsDescendantOf(shellPid, shellPids[i]))
                    {
                        SelectTab(tabItems[i]);
                        return;
                    }
                }
            }
        }
        catch { }
    }

    private static void SelectTab(AutomationElement tab)
    {
        try
        {
            var pattern = tab.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
            pattern?.Select();
        }
        catch { }
    }

    private static bool IsDescendantOf(int targetPid, int rootPid)
    {
        int current = targetPid;
        for (int i = 0; i < 20; i++)
        {
            int parent = GetParentPid(current);
            if (parent <= 0) return false;
            if (parent == rootPid) return true;
            current = parent;
        }
        return false;
    }

    private static int GetParentPid(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var obj in searcher.Get())
            {
                return Convert.ToInt32(obj["ParentProcessId"]);
            }
        }
        catch { }
        return -1;
    }

    private static List<int> GetDirectChildPids(int parentPid)
    {
        var children = new List<int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parentPid}");
            foreach (var obj in searcher.Get())
            {
                children.Add(Convert.ToInt32(obj["ProcessId"]));
            }
        }
        catch { }
        return children;
    }

    private static bool BringWindowToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

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
}
