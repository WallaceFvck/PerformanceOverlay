// ═══════════════════════════════════════════════════════════════════════════════
// ForegroundTracker.cs — Detecção de processo + título da janela
// ═══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PerformanceOverlay.Models;

namespace PerformanceOverlay.Services;

public sealed class ForegroundTracker : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    private int _currentPid;
    private string _currentProcessName = "";
    private string _currentWindowTitle = "";
    private GraphicsApi _currentApi = GraphicsApi.Unknown;
    private Timer? _pollTimer;
    private volatile bool _disposed;
    private volatile string _manualTarget = "";

    public event Action<int, string, string, GraphicsApi>? ForegroundChanged;

    public int CurrentPid => _currentPid;
    public string CurrentProcessName => _currentProcessName;
    public string CurrentWindowTitle => _currentWindowTitle;
    public GraphicsApi CurrentApi => _currentApi;

    public void SetManualTarget(string processNameWithoutExe)
    {
        string newTarget = processNameWithoutExe?.Replace(".exe", "").Trim() ?? "";
        string oldTarget = _manualTarget;
        _manualTarget = newTarget;
        if (newTarget != oldTarget)
        {
            _currentPid = 0;
            _currentProcessName = "";
            _currentWindowTitle = "";
            _currentApi = GraphicsApi.Unknown;
            if (!string.IsNullOrEmpty(newTarget)) TryFindManualProcess();
        }
    }

    public void Start(int pollIntervalMs = 1000)
    {
        _pollTimer = new Timer(PollForeground, null, 0, pollIntervalMs);
    }

    private void PollForeground(object? state)
    {
        if (_disposed) return;
        try
        {
            string manual = _manualTarget;
            if (!string.IsNullOrEmpty(manual))
            {
                TryFindManualProcess();
                return;
            }

            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid <= 0) return;

            // Mesmo PID — refresh título (pode mudar: loading → in-game)
            if (pid == _currentPid)
            {
                RefreshWindowTitle(hwnd);
                return;
            }

            string processName;
            try
            {
                using var proc = Process.GetProcessById(pid);
                processName = proc.ProcessName;
                if (IgnoredProcesses.Contains(processName)) return;
            }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }

            string windowTitle = GetWindowTitle(hwnd);
            var api = GraphicsApiDetector.Detect(pid);
            UpdateTarget(pid, processName, windowTitle, api);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FGTracker] Error: {ex.Message}");
        }
    }

    private void TryFindManualProcess()
    {
        string target = _manualTarget;
        if (string.IsNullOrEmpty(target)) return;
        Process[]? procs = null;
        try
        {
            procs = Process.GetProcessesByName(target);
            if (procs.Length == 0)
            {
                if (_currentPid != 0)
                {
                    _currentPid = 0; _currentProcessName = ""; _currentWindowTitle = "";
                    _currentApi = GraphicsApi.Unknown;
                    ForegroundChanged?.Invoke(0, "", "", GraphicsApi.Unknown);
                }
                return;
            }

            var proc = procs[0];
            int pid = proc.Id;
            string name = proc.ProcessName;

            if (pid == _currentPid)
            {
                try
                {
                    string t = proc.MainWindowTitle;
                    if (!string.IsNullOrWhiteSpace(t) && t != _currentWindowTitle)
                    {
                        _currentWindowTitle = t;
                        ForegroundChanged?.Invoke(pid, name, t, _currentApi);
                    }
                }
                catch { }
                return;
            }

            string windowTitle = "";
            try { windowTitle = proc.MainWindowTitle; } catch { }

            var api = GraphicsApiDetector.Detect(pid);
            UpdateTarget(pid, name, windowTitle, api);
        }
        catch (Exception ex) { Trace.WriteLine($"[FGTracker] Manual: {ex.Message}"); }
        finally
        {
            if (procs != null) foreach (var p in procs) p.Dispose();
        }
    }

    private void RefreshWindowTitle(IntPtr hwnd)
    {
        string newTitle = GetWindowTitle(hwnd);
        if (!string.IsNullOrWhiteSpace(newTitle) && newTitle != _currentWindowTitle)
        {
            _currentWindowTitle = newTitle;
            ForegroundChanged?.Invoke(_currentPid, _currentProcessName, newTitle, _currentApi);
        }
    }

    private void UpdateTarget(int pid, string name, string windowTitle, GraphicsApi api)
    {
        _currentPid = pid;
        _currentProcessName = name;
        _currentWindowTitle = windowTitle;
        _currentApi = api;
        string display = !string.IsNullOrWhiteSpace(windowTitle) ? windowTitle : name;
        Trace.WriteLine($"[FGTracker] Target: {display} (PID:{pid}, API:{api})");
        ForegroundChanged?.Invoke(pid, name, windowTitle, api);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "taskmgr", "taskhostw",
        "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost",
        "TextInputHost", "ApplicationFrameHost", "SystemSettings",
        "RuntimeBroker", "sihost", "ctfmon", "dllhost", "conhost",
        "svchost", "SecurityHealthSystray", "WidgetService", "Widgets",
        "PerformanceOverlay",
        "chrome", "firefox", "msedge", "opera", "brave", "vivaldi",
        "steam", "steamwebhelper", "GameOverlayUI",
        "EpicGamesLauncher", "EpicWebHelper",
        "GalaxyClient", "Battle.net", "Origin", "EADesktop",
        "Discord", "Slack", "Teams", "Zoom", "Telegram",
        "MSIAfterburner", "RTSS", "RivaTunerStatisticsServer",
        "WindowsTerminal", "cmd", "powershell", "pwsh", "Code", "devenv",
    };

    public void Dispose() { _disposed = true; _pollTimer?.Dispose(); }
}
