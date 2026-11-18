namespace Mirrivo.Models;

public class StreamConfig
{
    public int MaxSize { get; set; } = 0; // 0 = native / full resolution
    public int VideoBitrateMbps { get; set; } = 8; // 8M is optimal for both Wi-Fi & USB
    public int MaxFps { get; set; } = 60;
    
    // Latency & Wi-Fi Tuning
    public bool LowLatencyWifiMode { get; set; } = true;
    public string LatencyPreset { get; set; } = "Ultra-Low Latency (Sub-30ms)";
    
    // Screen & Power Optimizations
    public bool TurnScreenOff { get; set; } = false;
    public bool StayAwake { get; set; } = true;
    public bool NeverTimeout { get; set; } = true;
    public bool ShowFps { get; set; } = false;
    public bool ForwardAudio { get; set; } = true;
    public string AudioRoutingMode { get; set; } = "Media Audio (PC Speakers)";
    public bool ClipboardAutoSync { get; set; } = true;
    public bool ShowTouches { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = true;
    public bool Fullscreen { get; set; } = false;
    public bool RecordScreen { get; set; } = false;
    public string? RecordFilePath { get; set; }
}
