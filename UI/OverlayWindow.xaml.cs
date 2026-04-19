using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PerformanceOverlay.Helpers;
using PerformanceOverlay.Localization;
using PerformanceOverlay.Models;
using PerformanceOverlay.Services;

namespace PerformanceOverlay.UI;

public partial class OverlayWindow : Window
{
    private TelemetryPipeline? _pipeline;
    private OverlayConfig _config = new();
    private IntPtr _hwnd;
    private HotkeyService? _hotkeys;

    private int _hotkeyToggleId = -1, _hotkeyPositionId = -1, _hotkeyModeId = -1;
    private bool _overlayVisible = true;
    private int _positionIndex = 0;
    private OverlayMode _currentMode = OverlayMode.Full;

    private readonly Polyline _frametimeLine = new();

    // FPS color state (avoids SetResourceReference spam)
    private enum FpsColorState : byte { Accent, Good, Warning, Critical }
    private FpsColorState _lastFpsColor = FpsColorState.Accent;
    private FpsColorState _lastCompactFpsColor = FpsColorState.Accent;

    // Graph: reuse PointCollection + dynamic Y-axis (FPS scale) with EMA
    private PointCollection? _graphPoints;
    private float _smoothMaxFt = 60f; // EMA do max FPS no eixo Y (começa em 60)
    private const float MaxFtAlpha = 0.15f; // EMA lento para o eixo Y não pular

    // Cached strings
    private const string DashDash = "---";
    private const string DashMs = "--- ms";
    private const string DashTemp = "--°C";
    private const string DashPower = "--W";

    // Background color base (sem alpha) — alpha controlado separadamente
    private byte _bgR = 0x10, _bgG = 0x10, _bgB = 0x14;

    private static readonly SolidColorBrush BrushWarning = Freeze(new(Color.FromRgb(0xFF, 0x98, 0x00)));
    private static readonly SolidColorBrush BrushCritical = Freeze(new(Color.FromRgb(0xFF, 0x17, 0x44)));
    private static readonly SolidColorBrush BrushGood = Freeze(new(Color.FromRgb(0x4C, 0xAF, 0x50)));
    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public OverlayWindow() => InitializeComponent();

    public void Initialize(TelemetryPipeline pipeline, OverlayConfig config)
    {
        _pipeline = pipeline;
        _config = config;
        _currentMode = config.Mode;
        _pipeline.TelemetryUpdated += OnTelemetryUpdated;

        var driver = _pipeline.DriverInfo;
        TxtDriverInfo.Text = driver.Vendor switch
        {
            GpuVendor.AMD => $"Driver AMD Software: {driver.FriendlyVersion}",
            GpuVendor.NVIDIA => $"Driver NVIDIA: {driver.FriendlyVersion}",
            GpuVendor.Intel => $"Driver Intel: {driver.FriendlyVersion}",
            _ => $"Driver: {driver.DriverVersion}"
        };

        SetupFrametimeGraph();
        TxtFps.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        TxtCompactFps.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        ApplyTheme(config.AccentColorHex);
        ApplyBackgroundOpacity(config.Opacity);
        ApplyMode(_currentMode);
    }

    public void ApplyTheme(string accentHex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(accentHex);
            Resources["AccentBrush"] = new SolidColorBrush(color);
            Resources["AccentBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B));
            Resources["BarFillBrush"] = new SolidColorBrush(color);
            Resources["GraphLineBrush"] = new SolidColorBrush(Color.FromArgb(0xCC, color.R, color.G, color.B));
        }
        catch { }
    }

    /// <summary>
    /// Opacidade seletiva: altera APENAS o alpha do fundo.
    /// Texto, gráficos e ícones mantêm-se 100% opacos.
    /// </summary>
    public void ApplyBackgroundOpacity(double opacity)
    {
        opacity = Math.Clamp(opacity, 0.1, 1.0);
        byte alpha = (byte)(opacity * 255);
        var bgBrush = new SolidColorBrush(Color.FromArgb(alpha, _bgR, _bgG, _bgB));
        Resources["PanelBgBrush"] = bgBrush;
        // Window.Opacity fica SEMPRE 1.0 — nunca tocar nela
        this.Opacity = 1.0;
    }

    public void ApplyScaleAndOpacity(double scale, double opacity)
    {
        ApplyBackgroundOpacity(opacity);
        scale = Math.Clamp(scale, 0.5, 2.0);
        if (RootScaleHost != null)
            RootScaleHost.LayoutTransform = new ScaleTransform(scale, scale);

        // Re-clamp: garante que o overlay fica dentro da tela após escalar
        Dispatcher.BeginInvoke(new Action(ClampToScreen), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Verifica se o overlay ultrapassou os limites do ecrã e corrige posição.
    /// Chamado automaticamente após escalar ou reposicionar.
    /// </summary>
    private void ClampToScreen()
    {
        if (_hwnd == IntPtr.Zero) return;

        var mon = NativeInterop.MonitorFromWindow(_hwnd, NativeInterop.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeInterop.MONITORINFO();
        info.cbSize = Marshal.SizeOf<NativeInterop.MONITORINFO>();
        if (!NativeInterop.GetMonitorInfo(mon, ref info)) return;

        var r = info.rcMonitor;

        double w = ActualWidth;
        double h = ActualHeight;

        double newLeft = Left;
        double newTop = Top;

        if (newLeft + w > r.Right) newLeft = r.Right - w;
        if (newTop + h > r.Bottom) newTop = r.Bottom - h;
        if (newLeft < r.Left) newLeft = r.Left;
        if (newTop < r.Top) newTop = r.Top;

        if (newLeft != Left) Left = newLeft;
        if (newTop != Top) Top = newTop;
    }

    public void ApplyMode(OverlayMode mode)
    {
        _currentMode = mode;
        FullPanel.Visibility = mode == OverlayMode.Full ? Visibility.Visible : Visibility.Collapsed;
        CompactPanel.Visibility = mode == OverlayMode.Compact ? Visibility.Visible : Visibility.Collapsed;
        _config.Mode = mode;
        Dispatcher.BeginInvoke(new Action(() => PositionOverlay(_positionIndex)), DispatcherPriority.Loaded);
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.Handle;
        NativeInterop.MakeOverlayWindow(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        _hotkeys = new HotkeyService(_hwnd);
        _hotkeyToggleId = _hotkeys.Register(_config.ToggleVisibilityHotkey, ToggleVisibility);
        _hotkeyPositionId = _hotkeys.Register(_config.CyclePositionHotkey, CyclePosition);
        _hotkeyModeId = _hotkeys.Register(_config.ToggleModeHotkey, ToggleMode);

        PositionOverlay(_positionIndex);
        ApplyScaleAndOpacity(_config.Scale, _config.Opacity);
    }

    public void ReapplyHotkeys(string toggle, string position, string mode)
    {
        if (_hotkeys == null) return;
        if (_hotkeyToggleId >= 0) _hotkeys.Reassign(_hotkeyToggleId, toggle, ToggleVisibility);
        if (_hotkeyPositionId >= 0) _hotkeys.Reassign(_hotkeyPositionId, position, CyclePosition);
        if (_hotkeyModeId >= 0) _hotkeys.Reassign(_hotkeyModeId, mode, ToggleMode);
    }

    private void OnTelemetryUpdated(TelemetrySnapshot snapshot)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (!_overlayVisible) return;
            if (_currentMode == OverlayMode.Full) UpdateFullUi(snapshot);
            else UpdateCompactUi(snapshot);
        });
    }

    // ═══ FPS Color State Machine ═════════════════════════════════════════

    private void SetFpsColor(TextBlock txt, float fps, ref FpsColorState lastState)
    {
        FpsColorState s;
        if (fps >= 60) s = FpsColorState.Good;
        else if (fps >= 30) s = FpsColorState.Warning;
        else if (fps > 0) s = FpsColorState.Critical;
        else s = FpsColorState.Accent;
        if (s == lastState) return;
        lastState = s;
        switch (s)
        {
            case FpsColorState.Good: txt.Foreground = BrushGood; break;
            case FpsColorState.Warning: txt.Foreground = BrushWarning; break;
            case FpsColorState.Critical: txt.Foreground = BrushCritical; break;
            default:
                txt.ClearValue(TextBlock.ForegroundProperty);
                txt.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                break;
        }
    }

    // ═══ Display: Window Title (fallback para ProcessName) ═══════════════

    private static string GetDisplayName(FrameTelemetry frame)
    {
        // Preferir WindowTitle real (ex: "Minecraft 1.12.1 - Singleplayer")
        // Fallback para ProcessName (ex: "javaw") se título vazio
        if (!string.IsNullOrWhiteSpace(frame.WindowTitle))
            return $"▶ {frame.WindowTitle}";
        if (!string.IsNullOrEmpty(frame.TargetProcessName))
            return $"▶ {frame.TargetProcessName}";
        return "";
    }

    private void UpdateFullUi(TelemetrySnapshot snap)
    {
        TxtApi.Text = GraphicsApiDetector.GetApiDisplayName(snap.Frame.DetectedApi);
        TxtTime.Text = DateTime.Now.ToString("HH:mm:ss");

        float fps = snap.Frame.Fps;
        TxtFps.Text = fps > 0 ? $"{fps:F0}" : DashDash;
        SetFpsColor(TxtFps, fps, ref _lastFpsColor);

        TxtFps1Low.Text = snap.Frame.Fps1PercentLow > 0 ? $"{snap.Frame.Fps1PercentLow:F0}" : DashDash;
        TxtFrametime.Text = snap.Frame.FrametimeMs > 0 ? $"{snap.Frame.FrametimeMs:F1} ms" : DashMs;
        TxtProcessName.Text = GetDisplayName(snap.Frame);

        TxtCpuName.Text = snap.Cpu.Name ?? DashDash;
        TxtCpuUsage.Text = $"{snap.Cpu.UsagePercent:F0}%";
        TxtCpuClock.Text = $"{snap.Cpu.ClockMhz:F0} MHz";
        TxtCpuTemp.Text = snap.Cpu.TemperatureC > 0 ? $"{snap.Cpu.TemperatureC:F0}°C" : DashTemp;
        TxtCpuPower.Text = snap.Cpu.PowerW > 0 ? $"{snap.Cpu.PowerW:F0}W" : DashPower;
        BarCpu.Value = snap.Cpu.UsagePercent; BarCpu.Foreground = UsageBrush(snap.Cpu.UsagePercent);

        TxtRamUsage.Text = $"{snap.Ram.UsedMb:F0}/{snap.Ram.TotalMb:F0} MB";
        TxtRamPercent.Text = $"{snap.Ram.UsagePercent:F0}%";
        TxtRamFreq.Text = snap.Ram.FrequencyMhz > 0 ? $"{snap.Ram.FrequencyMhz:F0} MHz" : "";
        BarRam.Value = snap.Ram.UsagePercent; BarRam.Foreground = UsageBrush(snap.Ram.UsagePercent);

        TxtGpuName.Text = snap.Gpu.Name ?? DashDash;
        TxtGpuUsage.Text = $"{snap.Gpu.UsagePercent:F0}%";
        TxtGpuClock.Text = $"{snap.Gpu.ClockMhz:F0} MHz";
        TxtGpuTemp.Text = snap.Gpu.TemperatureC > 0 ? $"{snap.Gpu.TemperatureC:F0}°C" : DashTemp;
        TxtGpuPower.Text = snap.Gpu.PowerW > 0 ? $"{snap.Gpu.PowerW:F0}W" : DashPower;
        BarGpu.Value = snap.Gpu.UsagePercent; BarGpu.Foreground = UsageBrush(snap.Gpu.UsagePercent);

        TxtVramUsage.Text = $"{snap.Gpu.VramUsedMb:F0}/{snap.Gpu.VramTotalMb:F0} MB";
        TxtVramPercent.Text = $"{snap.Gpu.VramUsagePercent:F0}%";
        BarVram.Value = snap.Gpu.VramUsagePercent; BarVram.Foreground = UsageBrush(snap.Gpu.VramUsagePercent);

        UpdateFrametimeGraph();
    }

    private void UpdateCompactUi(TelemetrySnapshot snap)
    {
        float fps = snap.Frame.Fps;
        TxtCompactFps.Text = fps > 0 ? $"{fps:F0}" : DashDash;
        SetFpsColor(TxtCompactFps, fps, ref _lastCompactFpsColor);
        TxtCompactFps1Low.Text = snap.Frame.Fps1PercentLow > 0 ? $"{snap.Frame.Fps1PercentLow:F0}" : DashDash;
        TxtCompactApi.Text = GraphicsApiDetector.GetApiDisplayName(snap.Frame.DetectedApi);
    }

    // ═══ Frametime Graph (Dynamic Y-Axis + EMA smoothing) ════════════════

    private void SetupFrametimeGraph()
    {
        _frametimeLine.StrokeThickness = 1.5;
        _frametimeLine.StrokeLineJoin = PenLineJoin.Round;
        _frametimeLine.SetResourceReference(Shape.StrokeProperty, "GraphLineBrush");
        FrametimeGraph.Children.Add(_frametimeLine);
    }

    private void UpdateFrametimeGraph()
    {
        if (_pipeline == null) return;

        var (history, startIdx) = _pipeline.GetFrametimeHistorySnapshot();
        int len = history.Length;
        int count = Math.Min(len, (int)FrametimeGraph.ActualWidth);
        if (count <= 1) return;

        double w = FrametimeGraph.ActualWidth, h = FrametimeGraph.ActualHeight;

        // ── Converter frametime bruto → FPS instantâneo ──
        // history[i] = frametime em ms (bruto, sem EMA)
        // fps = 1000 / frametime
        // Gráfico de PERFORMANCE: FPS alto = linha no TOPO, queda = mergulho

        // Encontrar max FPS no buffer (para normalizar o eixo Y)
        float maxFps = 1f;
        for (int i = 0; i < len; i++)
        {
            if (history[i] > 0.01f)
            {
                float fps = Math.Min(1000f / history[i], 10000f);
                if (fps > maxFps) maxFps = fps;
            }
        }

        // Padding +15% no eixo Y (linha não encosta no topo)
        float targetMaxFps = maxFps * 1.15f;
        targetMaxFps = Math.Max(targetMaxFps, 30f); // mínimo 30 FPS no eixo

        // EMA suave APENAS no range do eixo Y (não nos dados)
        // Isso evita que o eixo pule de 120→300 abruptamente quando
        // um jogo diferente é aberto, mas mantém os DADOS brutos.
        _smoothMaxFt = MaxFtAlpha * targetMaxFps + (1f - MaxFtAlpha) * _smoothMaxFt;

        // Snap imediato se o range precisou crescer muito (evita corte)
        if (targetMaxFps > _smoothMaxFt * 1.3f)
            _smoothMaxFt = targetMaxFps;

        float yMax = _smoothMaxFt;

        // Reuse PointCollection
        if (_graphPoints == null || _graphPoints.Count != count)
        {
            _graphPoints = new PointCollection(count);
            for (int i = 0; i < count; i++) _graphPoints.Add(default);
        }

        for (int i = 0; i < count; i++)
        {
            int idx = (startIdx - count + i + len) % len;
            float ft = history[idx];

            // Converter para FPS (0 se sem dados, cap em 10000 para evitar spike absurdo)
            float fps = ft > 0.01f ? Math.Min(1000f / ft, 10000f) : 0f;

            double x = (i / (double)(count - 1)) * w;
            // FPS alto → fração grande → y perto de 0 (topo) ✓
            // FPS baixo (stutter) → fração pequena → y perto de h (base) ✓
            double y = h - (Math.Min(fps, yMax) / yMax) * h;
            _graphPoints[i] = new Point(x, Math.Max(0, y));
        }
        _frametimeLine.Points = _graphPoints;
    }

    // ═══ Hotkeys / Window ════════════════════════════════════════════════

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeInterop.WM_HOTKEY && _hotkeys != null)
            if (_hotkeys.Invoke(wParam.ToInt32())) handled = true;
        return IntPtr.Zero;
    }

    private void ToggleVisibility()
    {
        _overlayVisible = !_overlayVisible;
        Visibility = _overlayVisible ? Visibility.Visible : Visibility.Hidden;
    }

    private void CyclePosition()
    {
        _positionIndex = (_positionIndex + 1) % 4;
        PositionOverlay(_positionIndex);
    }

    private void ToggleMode() => ApplyMode(_currentMode == OverlayMode.Full ? OverlayMode.Compact : OverlayMode.Full);

    private void PositionOverlay(int pos)
    {
        var mon = NativeInterop.MonitorFromWindow(_hwnd, NativeInterop.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeInterop.MONITORINFO();
        info.cbSize = Marshal.SizeOf<NativeInterop.MONITORINFO>();
        if (!NativeInterop.GetMonitorInfo(mon, ref info)) return;

        var r = info.rcMonitor;
        int mx = _config.MarginX, my = _config.MarginY;
        switch ((OverlayPosition)pos)
        {
            case OverlayPosition.TopLeft: Left = r.Left + mx; Top = r.Top + my; break;
            case OverlayPosition.TopRight: Left = r.Right - ActualWidth - mx; Top = r.Top + my; break;
            case OverlayPosition.BottomLeft: Left = r.Left + mx; Top = r.Bottom - ActualHeight - my; break;
            case OverlayPosition.BottomRight: Left = r.Right - ActualWidth - mx; Top = r.Bottom - ActualHeight - my; break;
        }
    }

    private static SolidColorBrush UsageBrush(float p) => p switch { < 60 => BrushGood, < 85 => BrushWarning, _ => BrushCritical };

    protected override void OnClosed(EventArgs e)
    {
        _hotkeys?.Dispose();
        if (_pipeline != null) _pipeline.TelemetryUpdated -= OnTelemetryUpdated;
        base.OnClosed(e);
    }
}
