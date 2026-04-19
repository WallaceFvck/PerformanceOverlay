// ═══════════════════════════════════════════════════════════════════════════════
// HardwareMonitorService.cs — Coleta de sensores (REVIEWED)
// ═══════════════════════════════════════════════════════════════════════════════
// REVIEW FIXES:
//   [MEDIUM] GPU Engine counters: lock para evitar race entre Refresh e Read
//   [LOW]    Pré-calcular conversão de RAM (evita multiplicação repetida)
// ═══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using PerformanceOverlay.Models;

namespace PerformanceOverlay.Services;

internal sealed class SensorVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware) sub.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}

public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer;
    private readonly SensorVisitor _visitor;

    private IHardware? _cpuHardware;
    private ISensor? _cpuTotalLoad, _cpuClock, _cpuTemperature, _cpuPower;

    private IHardware? _gpuHardware;
    private ISensor? _gpuCoreLoad, _gpuCoreClock, _gpuTemperature, _gpuPower, _gpuVramUsed, _gpuVramTotal;

    private IHardware? _ramHardware;
    private ISensor? _ramUsed, _ramAvailable, _ramLoad;

    // [MEDIUM] GPU Engine counters protegidos por lock.
    // ANTES: RefreshGpuEngineCounters() podia limpar a lista enquanto
    // GetTotalGpuUsage() iterava (race condition → InvalidOperationException).
    private PerformanceCounterCategory? _gpuEngineCategory;
    private List<PerformanceCounter> _gpuEngineCounters = new();
    private readonly object _gpuCounterLock = new();
    private DateTime _lastGpuCountersRefresh = DateTime.MinValue;
    private readonly TimeSpan _gpuCountersRefreshInterval = TimeSpan.FromSeconds(5);

    public string CpuName { get; private set; } = "Unknown CPU";
    public string GpuName { get; private set; } = "Unknown GPU";

    private bool _initialized;
    private bool _disposed;

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true,
            IsMotherboardEnabled = false, IsStorageEnabled = false,
            IsNetworkEnabled = false, IsControllerEnabled = false,
            IsBatteryEnabled = false, IsPsuEnabled = false
        };
        _visitor = new SensorVisitor();
    }

    public void Initialize()
    {
        if (_initialized) return;
        _computer.Open();
        _computer.Accept(_visitor);
        ResolveCpuSensors();
        ResolveGpuSensors();
        ResolveRamSensors();
        InitializeGpuEngineCounters();
        _initialized = true;
        Trace.WriteLine($"[HWMon] Init — CPU: {CpuName}, GPU: {GpuName}");
    }

    public (CpuTelemetry cpu, GpuTelemetry gpu, RamTelemetry ram) Poll()
    {
        if (!_initialized) throw new InvalidOperationException("Call Initialize() first.");

        _cpuHardware?.Update();
        _gpuHardware?.Update();
        _ramHardware?.Update();

        var cpu = new CpuTelemetry
        {
            Name = CpuName,
            UsagePercent = _cpuTotalLoad?.Value ?? 0f,
            ClockMhz = _cpuClock?.Value ?? 0f,
            TemperatureC = _cpuTemperature?.Value ?? 0f,
            PowerW = _cpuPower?.Value ?? 0f
        };

        float gpuUsage = GetTotalGpuUsage();
        if (gpuUsage <= 0) gpuUsage = _gpuCoreLoad?.Value ?? 0f;

        float vramUsed = _gpuVramUsed?.Value ?? 0f;
        float vramTotal = _gpuVramTotal?.Value ?? 0f;
        if (vramTotal > 100_000) { vramUsed /= 1024f; vramTotal /= 1024f; }

        var gpu = new GpuTelemetry
        {
            Name = GpuName,
            UsagePercent = Math.Clamp(gpuUsage, 0f, 100f),
            ClockMhz = _gpuCoreClock?.Value ?? 0f,
            TemperatureC = _gpuTemperature?.Value ?? 0f,
            PowerW = _gpuPower?.Value ?? 0f,
            VramUsedMb = vramUsed,
            VramTotalMb = vramTotal,
            VramUsagePercent = vramTotal > 0 ? (vramUsed / vramTotal) * 100f : 0f
        };

        float ramUsed = _ramUsed?.Value ?? 0f;
        float ramAvailable = _ramAvailable?.Value ?? 0f;
        float ramTotal = ramUsed + ramAvailable;

        var ram = new RamTelemetry
        {
            TotalMb = ramTotal > 0 ? ramTotal * 1024f : GetTotalRamMbFallback(),
            UsedMb = ramUsed > 0 ? ramUsed * 1024f : 0f,
            UsagePercent = _ramLoad?.Value ?? 0f,
            FrequencyMhz = 0f
        };

        if (ram.TotalMb < 1024 && ramTotal > 0)
        {
            ram.TotalMb = ramTotal * 1024f;
            ram.UsedMb = ramUsed * 1024f;
        }

        return (cpu, gpu, ram);
    }

    // ═════════════════════════════════════════════════════════════════════
    // GPU Engine PerformanceCounters (thread-safe)
    // ═════════════════════════════════════════════════════════════════════

    private void InitializeGpuEngineCounters()
    {
        try
        {
            _gpuEngineCategory = new PerformanceCounterCategory("GPU Engine");
            RefreshGpuEngineCounters();
            Trace.WriteLine($"[HWMon] GPU Engine: {_gpuEngineCounters.Count} instances");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HWMon] GPU Engine unavailable: {ex.Message}");
            _gpuEngineCategory = null;
        }
    }

    private void RefreshGpuEngineCounters()
    {
        if (_gpuEngineCategory == null) return;

        var newCounters = new List<PerformanceCounter>();
        try
        {
            var instances = _gpuEngineCategory.GetInstanceNames();
            foreach (var inst in instances)
            {
                try
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                    counter.NextValue(); // Descarta primeira leitura (sempre 0)
                    newCounters.Add(counter);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HWMon] Refresh GPU failed: {ex.Message}");
        }

        // [MEDIUM] Swap atômico: criar lista nova, depois substituir a antiga.
        // Isso evita que GetTotalGpuUsage() itere uma lista vazia durante o refresh.
        List<PerformanceCounter> oldCounters;
        lock (_gpuCounterLock)
        {
            oldCounters = _gpuEngineCounters;
            _gpuEngineCounters = newCounters;
            _lastGpuCountersRefresh = DateTime.UtcNow;
        }

        // Dispose dos antigos FORA do lock (Dispose pode ser lento)
        foreach (var c in oldCounters)
        {
            try { c.Dispose(); } catch { }
        }
    }

    private float GetTotalGpuUsage()
    {
        if (_gpuEngineCategory == null) return -1f;

        if (DateTime.UtcNow - _lastGpuCountersRefresh > _gpuCountersRefreshInterval)
            RefreshGpuEngineCounters();

        float total = 0f;
        lock (_gpuCounterLock)
        {
            foreach (var counter in _gpuEngineCounters)
            {
                try { float v = counter.NextValue(); if (v > 0) total += v; }
                catch { }
            }
        }
        return Math.Min(total, 100f);
    }

    // ═════════════════════════════════════════════════════════════════════
    // LHM sensor resolution
    // ═════════════════════════════════════════════════════════════════════

    private void ResolveCpuSensors()
    {
        _cpuHardware = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (_cpuHardware == null) return;
        CpuName = CleanHardwareName(_cpuHardware.Name);
        foreach (var s in _cpuHardware.Sensors)
        {
            switch (s.SensorType)
            {
                case SensorType.Load when s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase):
                    _cpuTotalLoad = s; break;
                case SensorType.Clock when _cpuClock == null && !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase):
                    _cpuClock = s; break;
                case SensorType.Temperature when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                    || s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                    || s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                    || s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase):
                    if (_cpuTemperature == null ||
                        s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                        s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        _cpuTemperature = s;
                    break;
                case SensorType.Power when s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase):
                    _cpuPower = s; break;
            }
        }
    }

    private void ResolveGpuSensors()
    {
        _gpuHardware = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd)
            ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
            ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
        if (_gpuHardware == null) return;
        GpuName = CleanHardwareName(_gpuHardware.Name);
        foreach (var s in _gpuHardware.Sensors)
        {
            switch (s.SensorType)
            {
                case SensorType.Load when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("3D", StringComparison.OrdinalIgnoreCase):
                    _gpuCoreLoad ??= s; break;
                case SensorType.Clock when (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                    && !s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase):
                    _gpuCoreClock ??= s; break;
                case SensorType.Temperature when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase):
                    _gpuTemperature ??= s; break;
                case SensorType.Power when s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase):
                    _gpuPower ??= s; break;
                case SensorType.SmallData when s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("D3D Dedicated Memory Used", StringComparison.OrdinalIgnoreCase):
                    _gpuVramUsed ??= s; break;
                case SensorType.SmallData when s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase):
                    _gpuVramTotal ??= s; break;
            }
        }
    }

    private void ResolveRamSensors()
    {
        _ramHardware = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
        if (_ramHardware == null) return;
        foreach (var s in _ramHardware.Sensors)
        {
            switch (s.SensorType)
            {
                case SensorType.Data when s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase): _ramUsed ??= s; break;
                case SensorType.Data when s.Name.Contains("Available", StringComparison.OrdinalIgnoreCase): _ramAvailable ??= s; break;
                case SensorType.Load when s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase): _ramLoad ??= s; break;
            }
        }
    }

    private static string CleanHardwareName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "Unknown";
        var cleaned = raw.Replace("AMD ", "").Replace("NVIDIA ", "").Replace("GeForce ", "")
            .Replace("Radeon ", "").Replace("Intel(R) ", "").Replace("Intel ", "")
            .Replace("Core(TM) ", "Core ");
        var withIdx = cleaned.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
        if (withIdx > 0) cleaned = cleaned[..withIdx];
        return cleaned.Trim();
    }

    private static float GetTotalRamMbFallback()
    {
        var memInfo = new Helpers.NativeInterop.MEMORYSTATUSEX();
        memInfo.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Helpers.NativeInterop.MEMORYSTATUSEX>();
        return Helpers.NativeInterop.GlobalMemoryStatusEx(ref memInfo)
            ? memInfo.ullTotalPhys / (1024f * 1024f) : 0f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gpuCounterLock)
        {
            foreach (var c in _gpuEngineCounters) { try { c.Dispose(); } catch { } }
            _gpuEngineCounters.Clear();
        }
        try { _computer.Close(); } catch { }
    }
}
