using System.Diagnostics;
using PerformanceOverlay.Models;

namespace PerformanceOverlay.Services;

public sealed class TelemetryPipeline : IDisposable
{
    private readonly HardwareMonitorService _hwMonitor;
    private readonly FrameCaptureService _frameCapture;
    private readonly ForegroundTracker _fgTracker;

    private TelemetrySnapshot _latestSnapshot;
    private readonly object _snapshotLock = new();

    public DriverInfo DriverInfo { get; private set; } = new();
    public float RamFrequencyMhz { get; private set; }

    private Timer? _sensorTimer;
    private readonly OverlayConfig _config;
    private volatile bool _disposed;

    public event Action<TelemetrySnapshot>? TelemetryUpdated;

    // Frametime history (thread-safe)
    private readonly float[] _frametimeHistory = new float[120];
    private int _frametimeHistoryIndex;
    private readonly object _historyLock = new();

    public (float[] Data, int Index) GetFrametimeHistorySnapshot()
    {
        lock (_historyLock)
        {
            var copy = new float[_frametimeHistory.Length];
            Array.Copy(_frametimeHistory, copy, _frametimeHistory.Length);
            return (copy, _frametimeHistoryIndex);
        }
    }
    public int FrametimeHistoryIndex { get { lock (_historyLock) { return _frametimeHistoryIndex; } } }

    // EMA
    private const float AlphaFps = 0.60f, AlphaUsage = 0.40f, AlphaFt = 0.60f;
    private const float OmAlphaFps = 1f - AlphaFps, OmAlphaUsage = 1f - AlphaUsage, OmAlphaFt = 1f - AlphaFt;
    private bool _emaInit;
    private float _sFps, _sFt, _sFps1, _sCpu, _sGpu, _sRam, _sVram;

    // UI throttle
    private long _lastUiTicks;
    private static readonly long UiMinTicks = Stopwatch.Frequency / 60;

    // Current window title (updated by ForegroundTracker)
    private string _currentWindowTitle = "";
    private string _currentProcessName = "";

    public ForegroundTracker ForegroundTracker => _fgTracker;

    public TelemetryPipeline(OverlayConfig config)
    {
        _config = config;
        _hwMonitor = new HardwareMonitorService();
        _frameCapture = new FrameCaptureService(config.FpsWindowMs);
        _fgTracker = new ForegroundTracker();
    }

    public async Task InitializeAsync()
    {
        var sw = Stopwatch.StartNew();
        await Task.Run(() => _hwMonitor.Initialize());

        var driverTask = Task.Run(() => DriverInfoService.GetDriverInfo());
        var ramFreqTask = Task.Run(() => DriverInfoService.GetRamFrequencyMhz());
        DriverInfo = await driverTask;
        RamFrequencyMhz = await ramFreqTask;

        _frameCapture.Start();
        _fgTracker.ForegroundChanged += OnForegroundChanged;

        if (!string.IsNullOrEmpty(_config.ManualProcessName))
            _fgTracker.SetManualTarget(_config.ManualProcessName);

        _fgTracker.Start();

        _sensorTimer = new Timer(PollSensors, null,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(_config.SensorIntervalMs));

        sw.Stop();
        Trace.WriteLine($"[Pipeline] Init {sw.ElapsedMilliseconds}ms");
    }

    public void SetManualProcess(string processName)
    {
        _fgTracker.SetManualTarget(processName);
    }

    private void OnForegroundChanged(int pid, string processName, string windowTitle, GraphicsApi api)
    {
        _currentProcessName = processName;
        _currentWindowTitle = windowTitle;
        _frameCapture.SetTargetProcess(pid, processName);
        _emaInit = false;

        // Reset graph on process change
        lock (_historyLock)
        {
            Array.Clear(_frametimeHistory);
            _frametimeHistoryIndex = 0;
        }
    }

    private void PollSensors(object? state)
    {
        if (_disposed) return;
        try
        {
            var (cpu, gpu, ram) = _hwMonitor.Poll();
            ram.FrequencyMhz = RamFrequencyMhz;

            var frame = _frameCapture.CurrentTelemetry;

            if (_fgTracker.CurrentApi != GraphicsApi.Unknown)
                frame.DetectedApi = GraphicsApiDetector.Refine(_fgTracker.CurrentApi, frame.DetectedApi);

            // Window title: prefer tracker's current (updates on focus change)
            frame.WindowTitle = _currentWindowTitle;
            frame.TargetProcessName = _currentProcessName;

            // Guardar frametime BRUTO no histórico (antes do EMA).
            // O gráfico precisa mostrar stutters instantâneos sem suavização.
            // O número digital do FPS usa EMA (abaixo), mas o gráfico é cru.
            float rawFrametimeMs = frame.FrametimeMs;

            // EMA
            if (!_emaInit)
            {
                _sFps = frame.Fps; _sFt = frame.FrametimeMs; _sFps1 = frame.Fps1PercentLow;
                _sCpu = cpu.UsagePercent; _sGpu = gpu.UsagePercent;
                _sRam = ram.UsagePercent; _sVram = gpu.VramUsagePercent;
                _emaInit = true;
            }
            else
            {
                _sFps = AlphaFps * frame.Fps + OmAlphaFps * _sFps;
                _sFt = AlphaFt * frame.FrametimeMs + OmAlphaFt * _sFt;
                _sFps1 = AlphaFps * frame.Fps1PercentLow + OmAlphaFps * _sFps1;
                _sCpu = AlphaUsage * cpu.UsagePercent + OmAlphaUsage * _sCpu;
                _sGpu = AlphaUsage * gpu.UsagePercent + OmAlphaUsage * _sGpu;
                _sRam = AlphaUsage * ram.UsagePercent + OmAlphaUsage * _sRam;
                _sVram = AlphaUsage * gpu.VramUsagePercent + OmAlphaUsage * _sVram;
            }

            frame.Fps = _sFps; frame.FrametimeMs = _sFt; frame.Fps1PercentLow = _sFps1;
            cpu.UsagePercent = _sCpu; gpu.UsagePercent = _sGpu;
            ram.UsagePercent = _sRam; gpu.VramUsagePercent = _sVram;

            // Histórico usa valor BRUTO (não suavizado)
            if (rawFrametimeMs > 0)
            {
                lock (_historyLock)
                {
                    _frametimeHistory[_frametimeHistoryIndex] = rawFrametimeMs;
                    _frametimeHistoryIndex = (_frametimeHistoryIndex + 1) % _frametimeHistory.Length;
                }
            }

            var snapshot = new TelemetrySnapshot
            {
                TimestampTicks = Stopwatch.GetTimestamp(),
                Cpu = cpu, Gpu = gpu, Ram = ram, Frame = frame
            };

            lock (_snapshotLock) { _latestSnapshot = snapshot; }

            long now = Stopwatch.GetTimestamp();
            if (now - _lastUiTicks >= UiMinTicks)
            {
                _lastUiTicks = now;
                TelemetryUpdated?.Invoke(snapshot);
            }
        }
        catch (Exception ex) { Trace.WriteLine($"[Pipeline] Poll: {ex.Message}"); }
    }

    public TelemetrySnapshot GetLatestSnapshot()
    {
        lock (_snapshotLock) { return _latestSnapshot; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sensorTimer?.Dispose();
        _fgTracker.ForegroundChanged -= OnForegroundChanged;
        _fgTracker.Dispose();
        _frameCapture.Dispose();
        _hwMonitor.Dispose();
    }
}
