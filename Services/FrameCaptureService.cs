// ═══════════════════════════════════════════════════════════════════════════════
// FrameCaptureService.cs — PresentMon architecture + Code Review fixes
// ═══════════════════════════════════════════════════════════════════════════════
// REVIEW FIXES:
//   [CRITICAL] AppDomain.ProcessExit handler para limpar sessão ETW em crash
//   [MEDIUM]   Pré-alocar _frametimeBuffer capacity, evitar Sort() alloc
// ═══════════════════════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using PerformanceOverlay.Models;

namespace PerformanceOverlay.Services;

public sealed class FrameCaptureService : IDisposable
{
    private const string SessionName = "PerformanceOverlay_ETW_Session";

    private static readonly Guid DxgiProvider =
        new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider =
        new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnlProvider =
        new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    private readonly int _fpsWindowMs;

    private TraceEventSession? _session;
    private Thread? _processingThread;
    private volatile bool _running;

    private readonly ConcurrentQueue<double> _runtimeTimestamps = new();
    private readonly ConcurrentQueue<double> _kernelTimestamps = new();

    private FrameTelemetry _currentTelemetry;
    private readonly object _telemetryLock = new();
    private volatile int _targetPid;

    // [MEDIUM] Pré-alocar com capacity fixa. Clear() não desaloca — reutiliza.
    private readonly List<double> _frametimeBuffer = new(2048);

    private volatile bool _runtimeActive;
    // [FIX #2] Flag atômica: pede ao CalculationLoop para limpar o buffer
    // no seu próprio thread, evitando race condition com SetTargetProcess.
    private volatile bool _clearRequested;

    public FrameCaptureService(int fpsWindowMs = 1000)
    {
        _fpsWindowMs = fpsWindowMs;

        // [CRITICAL] Garantir limpeza da sessão ETW mesmo em crash/kill.
        // Sessões ETW são recursos do kernel que persistem até reboot
        // se não forem fechadas. Sem este handler, um crash deixa a
        // sessão "PerformanceOverlay_ETW_Session" pendente e a próxima
        // execução falha com "session already exists".
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public FrameTelemetry CurrentTelemetry
    {
        get { lock (_telemetryLock) { return _currentTelemetry; } }
    }

    public void SetTargetProcess(int pid, string processName = "")
    {
        _targetPid = pid;
        lock (_telemetryLock)
        {
            _currentTelemetry.TargetProcessId = pid;
            _currentTelemetry.TargetProcessName = processName;
            _currentTelemetry.Fps = 0;
            _currentTelemetry.FrametimeMs = 0;
            _currentTelemetry.Fps1PercentLow = 0;
            _currentTelemetry.Frametime99thMs = 0;
        }
        while (_runtimeTimestamps.TryDequeue(out _)) { }
        while (_kernelTimestamps.TryDequeue(out _)) { }
        _clearRequested = true; // CalculationLoop limpará no próprio thread
        _runtimeActive = false;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        // Limpar sessão órfã de crash anterior
        try { TraceEventSession.GetActiveSession(SessionName)?.Dispose(); } catch { }

        _session = new TraceEventSession(SessionName) { BufferSizeMB = 4 };

        _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, 0x1);
        _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, 0x1);
        _session.EnableProvider(DxgKrnlProvider, TraceEventLevel.Informational, 0x1);

        _processingThread = new Thread(ProcessEtwEvents)
        {
            Name = "ETW_FrameCapture",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _processingThread.Start();

        new Thread(CalculationLoop)
        {
            Name = "FPS_Calculator",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        }.Start();

        Trace.WriteLine("[FrameCapture] ETW started.");
    }

    private void ProcessEtwEvents()
    {
        if (_session == null) return;
        _session.Source.Dynamic.All += OnEtwEvent;
        _session.Source.Process();
    }

    // Event IDs (evt.ID) — campo estável do provider, mesmo que PresentMon usa.
    // evt.Opcode = opcode genérico ETW (Start=1, Stop=2) — NÃO é o event ID.
    // evt.ID = TraceEventID = EVENT_DESCRIPTOR.Id = identificador único do evento.
    private const int DXGI_PresentStart_ID = 42;
    private const int D3D9_PresentStart_ID = 1;

    private void OnEtwEvent(TraceEvent evt)
    {
        int targetPid = _targetPid;
        if (targetPid == 0 || evt.ProcessID != targetPid)
            return;

        int eventId = (int)evt.ID;

        // ── DXGI: Event ID 42 = IDXGISwapChain::Present() ──────────────
        // Este é o ÚNICO evento por frame no DXGI. Event ID 55 é
        // PresentMultiplaneOverlay que é evento SEPARADO.
        if (evt.ProviderGuid == DxgiProvider && eventId == DXGI_PresentStart_ID)
        {
            _runtimeActive = true;
            _runtimeTimestamps.Enqueue(evt.TimeStampRelativeMSec);
            return;
        }

        // ── D3D9: Event ID 1 = IDirect3DDevice9::Present() ─────────────
        if (evt.ProviderGuid == D3D9Provider && eventId == D3D9_PresentStart_ID)
        {
            _runtimeActive = true;
            _runtimeTimestamps.Enqueue(evt.TimeStampRelativeMSec);
            return;
        }

        // ── DxgKrnl: fallback para OpenGL/Vulkan ────────────────────────
        // Sem runtime ativo, aceitar eventos DxgKrnl com "Present" no nome.
        // Para OpenGL não há DXGI, então _runtimeActive fica false.
        if (evt.ProviderGuid == DxgKrnlProvider && !_runtimeActive)
        {
            string name = evt.EventName;
            if (name.StartsWith("Present", StringComparison.Ordinal) &&
                !name.StartsWith("PresentHistory", StringComparison.Ordinal))
            {
                _kernelTimestamps.Enqueue(evt.TimeStampRelativeMSec);
            }
        }
    }

    private void CalculationLoop()
    {
        double lastRuntimeTs = 0;
        double lastKernelTs = 0;

        while (_running)
        {
            Thread.Sleep(Math.Max(100, _fpsWindowMs / 2));
            if (_targetPid == 0) continue;

            // Reset timestamps quando processo muda (via flag atômica do SetTargetProcess)
            if (_clearRequested)
            {
                _clearRequested = false;
                lastRuntimeTs = 0;
                lastKernelTs = 0;
            }

            _frametimeBuffer.Clear();
            GraphicsApi detectedApi = GraphicsApi.Unknown;

            bool hasRuntimeData = false;
            while (_runtimeTimestamps.TryDequeue(out double ts))
            {
                hasRuntimeData = true;
                if (lastRuntimeTs > 0)
                {
                    double ft = ts - lastRuntimeTs;
                    if (ft > 0 && ft < 1000)
                        _frametimeBuffer.Add(ft);
                }
                lastRuntimeTs = ts;
            }

            if (hasRuntimeData)
            {
                detectedApi = GraphicsApi.DirectX12;
                while (_kernelTimestamps.TryDequeue(out _)) { }
                lastKernelTs = 0;
            }
            else
            {
                while (_kernelTimestamps.TryDequeue(out double ts))
                {
                    if (lastKernelTs > 0)
                    {
                        double ft = ts - lastKernelTs;
                        if (ft > 0 && ft < 1000)
                            _frametimeBuffer.Add(ft);
                    }
                    lastKernelTs = ts;
                }
                if (_frametimeBuffer.Count > 0)
                    detectedApi = GraphicsApi.Unknown; // DxgKrnl não indica API — módulos decidem via Refine()
            }

            if (_frametimeBuffer.Count == 0) continue;

            // [MEDIUM] Cálculo sem Sort() para FPS médio (Sort só pra 1% Low)
            double totalTime = 0;
            for (int i = 0; i < _frametimeBuffer.Count; i++)
                totalTime += _frametimeBuffer[i];

            float fps = totalTime > 0
                ? Math.Min((float)(_frametimeBuffer.Count / (totalTime / 1000.0)), 10000f)
                : 0f;
            float avgFrametime = (float)(totalTime / _frametimeBuffer.Count);

            _frametimeBuffer.Sort();
            int idx99 = Math.Min((int)(_frametimeBuffer.Count * 0.99),
                                  _frametimeBuffer.Count - 1);
            float ft99 = (float)_frametimeBuffer[idx99];
            float fps1Low = ft99 > 0 ? Math.Min(1000f / ft99, fps) : 0f;

            lock (_telemetryLock)
            {
                _currentTelemetry.Fps = Sanitize(MathF.Round(fps, 1));
                _currentTelemetry.FrametimeMs = Sanitize(MathF.Round(avgFrametime, 2));
                _currentTelemetry.Fps1PercentLow = Sanitize(MathF.Round(fps1Low, 1));
                _currentTelemetry.Frametime99thMs = Sanitize(MathF.Round(ft99, 2));
                if (detectedApi != GraphicsApi.Unknown)
                    _currentTelemetry.DetectedApi = detectedApi;
            }
        }
    }

    // [FIX #6] Guard contra NaN/Infinity de edge cases extremos
    private static float Sanitize(float v) => float.IsFinite(v) ? v : 0f;

    private volatile bool _disposed;

    public void Stop()
    {
        _running = false;
        try { _session?.Source?.StopProcessing(); _session?.Stop(); }
        catch (Exception ex) { Trace.WriteLine($"[FrameCapture] Stop: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _session?.Dispose(); } catch { }
        _session = null;
    }
}
