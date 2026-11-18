using CommunityToolkit.Mvvm.ComponentModel;

namespace Mirrivo.Models;

public enum DeviceConnectionStatus
{
    Ready,
    Mirroring,
    Connecting,
    Offline,
    Unauthorized
}

public partial class DeviceItem : ObservableObject
{
    [ObservableProperty]
    private string _serial = string.Empty;

    [ObservableProperty]
    private string _model = "Unknown Android";

    [ObservableProperty]
    private string _product = string.Empty;

    [ObservableProperty]
    private string _state = "device";

    [ObservableProperty]
    private string _transportId = string.Empty;

    public bool IsWireless => Serial.Contains(':') || Serial.StartsWith("192.") || Serial.StartsWith("10.") || Serial.StartsWith("172.");
    public string ConnectionTypeDisplay => IsWireless ? "Wireless Wi-Fi" : "USB Cable";
    
    public string SwitchModeText => IsWireless ? "🔌 Switch to USB" : "📶 Switch to Wi-Fi";
    public string DisconnectOrReconnectText => IsWireless ? "✕ Disconnect Wi-Fi" : "⚡ Reset USB Link";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryDisplay))]
    private int _batteryLevel = -1;

    public string BatteryDisplay => BatteryLevel >= 0 ? $"{BatteryLevel}%" : "--%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    private DeviceConnectionStatus _status = DeviceConnectionStatus.Ready;

    public string StatusText => Status switch
    {
        DeviceConnectionStatus.Ready => "READY",
        DeviceConnectionStatus.Mirroring => "LIVE MIRROR",
        DeviceConnectionStatus.Connecting => "CONNECTING...",
        DeviceConnectionStatus.Unauthorized => "UNAUTHORIZED",
        _ => "OFFLINE"
    };

    public string StatusColor => Status switch
    {
        DeviceConnectionStatus.Ready => "#10B981", // Emerald green
        DeviceConnectionStatus.Mirroring => "#0070F3", // Vercel blue
        DeviceConnectionStatus.Connecting => "#F5A623", // Amber warning
        DeviceConnectionStatus.Unauthorized => "#EE0000", // Error red
        _ => "#8F8F8F"
    };
}
