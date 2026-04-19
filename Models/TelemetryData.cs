namespace PerformanceOverlay.Models;

public struct TelemetrySnapshot
{
    public long TimestampTicks;
    public CpuTelemetry Cpu;
    public GpuTelemetry Gpu;
    public RamTelemetry Ram;
    public FrameTelemetry Frame;
}

public struct CpuTelemetry
{
    public string Name;
    public float UsagePercent;
    public float ClockMhz;
    public float TemperatureC;
    public float PowerW;
}

public struct GpuTelemetry
{
    public string Name;
    public float UsagePercent;
    public float ClockMhz;
    public float TemperatureC;
    public float PowerW;
    public float VramUsedMb;
    public float VramTotalMb;
    public float VramUsagePercent;
}

public struct RamTelemetry
{
    public float TotalMb;
    public float UsedMb;
    public float UsagePercent;
    public float FrequencyMhz;
    public float ProcessUsedMb;
}

public struct FrameTelemetry
{
    public float Fps;
    public float FrametimeMs;
    public float Fps1PercentLow;
    public float Frametime99thMs;
    public GraphicsApi DetectedApi;
    public int TargetProcessId;
    public string TargetProcessName;
    /// <summary>Título real da janela (ex: "Minecraft 1.12.1 - Singleplayer")</summary>
    public string WindowTitle;
}

public enum GraphicsApi : byte
{
    Unknown = 0, DirectX9 = 1, DirectX11 = 2,
    DirectX12 = 3, Vulkan = 4, OpenGL = 5
}

public sealed class DriverInfo
{
    public GpuVendor Vendor { get; init; }
    public string DriverVersion { get; init; } = "N/A";
    public string FriendlyVersion { get; init; } = "N/A";
    public string DriverDate { get; init; } = "N/A";
}

public enum GpuVendor : byte { Unknown = 0, AMD = 1, NVIDIA = 2, Intel = 3 }

public enum OverlayMode : byte { Full = 0, Compact = 1 }

public sealed class OverlayConfig
{
    public OverlayPosition Position { get; set; } = OverlayPosition.TopLeft;
    public int MarginX { get; set; } = 12;
    public int MarginY { get; set; } = 12;
    public double Opacity { get; set; } = 0.92;
    public double Scale { get; set; } = 1.0;
    public string AccentColorHex { get; set; } = "#E53935";
    public int SensorIntervalMs { get; set; } = 500;
    public int FpsWindowMs { get; set; } = 1000;
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowFrameTiming { get; set; } = true;
    public bool ShowDriverInfo { get; set; } = true;
    public bool ShowFrametimeGraph { get; set; } = true;
    public string ToggleVisibilityHotkey { get; set; } = "F8";
    public string CyclePositionHotkey { get; set; } = "Ctrl+F8";
    public string ToggleModeHotkey { get; set; } = "Shift+F8";
    public string ManualProcessName { get; set; } = "";
    public OverlayMode Mode { get; set; } = OverlayMode.Full;
    /// <summary>"auto" = detectar do sistema, ou "en","pt","es","fr","de","zh"</summary>
    public string Language { get; set; } = "auto";
}

public enum OverlayPosition : byte
{
    TopLeft = 0, TopRight = 1, BottomLeft = 2, BottomRight = 3
}
