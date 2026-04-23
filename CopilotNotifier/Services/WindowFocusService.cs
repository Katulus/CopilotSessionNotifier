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

    public static bool FocusTerminalWindow(int pid)
    {
        try
        {
            // First, try to find the hosting Windows Terminal and switch to the correct tab
            if (TryFocusWindowsTerminalTab(pid))
                return true;

            // Fallback: find any window hosting this process
            var hWnd = FindWindowForProcess(pid);
            if (hWnd == IntPtr.Zero)
                return false;

            return BringWindowToFront(hWnd);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFocusWindowsTerminalTab(int targetPid)
    {
        try
        {
            // Find the parent chain from the target PID up to Windows Terminal
            var parentPids = GetAncestorPids(targetPid);

            // Find all Windows Terminal processes
            var wtProcesses = Process.GetProcessesByName("WindowsTerminal");
            if (wtProcesses.Length == 0)
                return false;

            foreach (var wt in wtProcesses)
            {
                if (wt.MainWindowHandle == IntPtr.Zero)
                    continue;

                // Get the automation element for the WT window
                var wtElement = AutomationElement.FromHandle(wt.MainWindowHandle);
                if (wtElement == null)
                    continue;

                // Find the tab control
                var tabControl = wtElement.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
                if (tabControl == null)
                    continue;

                // Get all tab items
                var tabItems = tabControl.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

                // Get all child PIDs of this Windows Terminal, grouped by tab
                // Each tab in WT spawns a shell process tree
                var wtChildPids = GetChildPids(wt.Id);

                // Strategy: try to find which tab owns our target process
                // First, check if any of our ancestor PIDs is a direct child of WT
                foreach (var ancestorPid in parentPids)
                {
                    if (wtChildPids.Contains(ancestorPid))
                    {
                        // We found the WT that hosts our process
                        // Now try to select the right tab
                        if (SelectTabByPid(tabItems, wt.Id, targetPid, parentPids))
                        {
                            BringWindowToFront(wt.MainWindowHandle);
                            return true;
                        }

                        // Fallback: just focus the WT window
                        BringWindowToFront(wt.MainWindowHandle);
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private static bool SelectTabByPid(AutomationElementCollection tabItems, int wtPid,
        int targetPid, HashSet<int> ancestorPids)
    {
        try
        {
            // Get all direct child processes of the WT process
            // Each tab typically has one direct shell child
            var directChildren = GetDirectChildPids(wtPid);

            // For each tab, determine which direct child belongs to it
            // Tabs are ordered, and direct children are spawned in tab order
            // We match by checking which direct child's subtree contains our target
            var childList = directChildren.OrderBy(p => p).ToList();

            for (int i = 0; i < tabItems.Count && i < childList.Count; i++)
            {
                var childPid = childList[i];
                var subtree = GetAllDescendantPids(childPid);
                subtree.Add(childPid);

                if (subtree.Contains(targetPid) || subtree.Overlaps(ancestorPids))
                {
                    // This tab owns our process — select it
                    var tabItem = tabItems[i];
                    var selectionPattern = tabItem.GetCurrentPattern(SelectionItemPattern.Pattern)
                        as SelectionItemPattern;
                    selectionPattern?.Select();
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static HashSet<int> GetAncestorPids(int pid)
    {
        var ancestors = new HashSet<int>();
        try
        {
            var currentPid = pid;
            for (int i = 0; i < 20; i++) // Safety limit
            {
                var parentPid = GetParentPid(currentPid);
                if (parentPid <= 0 || !ancestors.Add(parentPid))
                    break;
                currentPid = parentPid;
            }
        }
        catch { }
        return ancestors;
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

    private static HashSet<int> GetChildPids(int parentPid)
    {
        var children = new HashSet<int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parentPid}");
            foreach (var obj in searcher.Get())
            {
                var childPid = Convert.ToInt32(obj["ProcessId"]);
                children.Add(childPid);
                // Recursively get grandchildren
                foreach (var grandchild in GetChildPids(childPid))
                    children.Add(grandchild);
            }
        }
        catch { }
        return children;
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

    private static HashSet<int> GetAllDescendantPids(int pid)
    {
        var descendants = new HashSet<int>();
        try
        {
            var queue = new Queue<int>();
            queue.Enqueue(pid);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {current}");
                foreach (var obj in searcher.Get())
                {
                    var childPid = Convert.ToInt32(obj["ProcessId"]);
                    if (descendants.Add(childPid))
                        queue.Enqueue(childPid);
                }
            }
        }
        catch { }
        return descendants;
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

    private static IntPtr FindWindowForProcess(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            if (process.MainWindowHandle != IntPtr.Zero)
                return process.MainWindowHandle;

            // Walk up the process tree to find a window
            var ancestors = GetAncestorPids(pid);
            foreach (var ancestorPid in ancestors)
            {
                try
                {
                    var ancestor = Process.GetProcessById(ancestorPid);
                    if (ancestor.MainWindowHandle != IntPtr.Zero && IsWindowVisible(ancestor.MainWindowHandle))
                        return ancestor.MainWindowHandle;
                }
                catch { }
            }
        }
        catch { }

        return IntPtr.Zero;
    }
}
