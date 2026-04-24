using System.Diagnostics;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private const int SW_RESTORE = 9;

    public static bool FocusTerminalWindow(int pid, string? sessionName = null)
    {
        try
        {
            // Single snapshot for the entire operation — replaces all WMI calls
            var parentMap = BuildParentMap();

            var (windowHandle, wtPid, shellPid) = FindHostingWindow(pid, parentMap);
            if (windowHandle == IntPtr.Zero)
                return false;

            BringWindowToFront(windowHandle);

            if (wtPid > 0)
            {
                TrySwitchToTab(windowHandle, sessionName, wtPid, shellPid, parentMap);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if the terminal window hosting <paramref name="pid"/> is currently
    /// the foreground window. For Windows Terminal, also verifies the matching tab is
    /// the selected one.
    /// </summary>
    public static bool IsTerminalWindowFocused(int pid, string? sessionName = null)
    {
        try
        {
            var parentMap = BuildParentMap();
            var (windowHandle, wtPid, shellPid) = FindHostingWindow(pid, parentMap);
            if (windowHandle == IntPtr.Zero)
                return false;

            if (GetForegroundWindow() != windowHandle)
                return false;

            // Non-WT host: foreground window is enough.
            if (wtPid <= 0)
                return true;

            // Windows Terminal: check that the matching tab is the active one.
            try
            {
                var wtElement = AutomationElement.FromHandle(windowHandle);
                if (wtElement == null) return true;

                var tabItems = wtElement.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

                // Single tab => hosting window focus implies session focus.
                if (tabItems.Count <= 1)
                    return true;

                var match = FindMatchingTab(tabItems, sessionName, wtPid, shellPid, parentMap);
                if (match == null)
                {
                    // Couldn't identify the session's tab; fall back to window focus.
                    return true;
                }

                var sel = match.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
                return sel?.Current.IsSelected ?? true;
            }
            catch
            {
                return true;
            }
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
    private static (IntPtr handle, int wtPid, int shellPid) FindHostingWindow(int startPid, Dictionary<int, int> parentMap)
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
                currentPid = GetParentPid(currentPid, parentMap);
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
    private static void TrySwitchToTab(IntPtr wtHandle, string? sessionName, int wtPid, int shellPid, Dictionary<int, int> parentMap)
    {
        try
        {
            var wtElement = AutomationElement.FromHandle(wtHandle);
            if (wtElement == null) return;

            var tabItems = wtElement.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

            if (tabItems.Count <= 1)
                return;

            var match = FindMatchingTab(tabItems, sessionName, wtPid, shellPid, parentMap);
            if (match != null)
                SelectTab(match);
        }
        catch { }
    }

    /// <summary>
    /// Identifies which Windows Terminal tab corresponds to a given session, either by
    /// matching session name against tab name or by walking the process tree.
    /// </summary>
    private static AutomationElement? FindMatchingTab(
        AutomationElementCollection tabItems, string? sessionName, int wtPid, int shellPid,
        Dictionary<int, int> parentMap)
    {
        // Strategy 1: Match by tab name containing session name
        if (!string.IsNullOrWhiteSpace(sessionName))
        {
            foreach (AutomationElement tab in tabItems)
            {
                var tabName = tab.Current.Name ?? "";
                if (tabName.Contains(sessionName, StringComparison.OrdinalIgnoreCase)
                    || sessionName.Contains(tabName, StringComparison.OrdinalIgnoreCase))
                {
                    return tab;
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
                    return tab;
                }
            }
        }

        // Strategy 2: Match via process tree — find which shell child of WT
        // is an ancestor of our target process
        if (shellPid > 0)
        {
            var shellPids = GetDirectChildPids(wtPid, parentMap)
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
                if (shellPids[i] == shellPid || IsDescendantOf(shellPid, shellPids[i], parentMap))
                {
                    return tabItems[i];
                }
            }
        }

        return null;
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

    private static bool IsDescendantOf(int targetPid, int rootPid, Dictionary<int, int> parentMap)
    {
        int current = targetPid;
        for (int i = 0; i < 20; i++)
        {
            int parent = GetParentPid(current, parentMap);
            if (parent <= 0) return false;
            if (parent == rootPid) return true;
            current = parent;
        }
        return false;
    }

    /// <summary>
    /// Takes a single process snapshot and builds a parent-pid lookup dictionary.
    /// This replaces per-call WMI queries and is near-instant.
    /// </summary>
    private static Dictionary<int, int> BuildParentMap()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
            return map;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref entry))
            {
                do
                {
                    map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                } while (Process32Next(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return map;
    }

    private static int GetParentPid(int pid, Dictionary<int, int> parentMap)
    {
        return parentMap.TryGetValue(pid, out int parent) ? parent : -1;
    }

    private static List<int> GetDirectChildPids(int parentPid, Dictionary<int, int> parentMap)
    {
        return parentMap
            .Where(kvp => kvp.Value == parentPid)
            .Select(kvp => kvp.Key)
            .ToList();
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
