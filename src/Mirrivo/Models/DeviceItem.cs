namespace Mirrivo.Models;

public enum DeviceConnectionStatus
{
    Ready,
    Mirroring,
    Connecting,
    Offline,
    Unauthorized
}

public class DeviceItem
{
    public string Serial { get; set; } = string.Empty;
    public string Model { get; set; } = "Unknown Android";
    public string Product { get; set; } = string.Empty;
    public string State { get; set; } = "device";
    public string TransportId { get; set; } = string.Empty;
    
    public bool IsWireless => Serial.Contains(':') || Serial.StartsWith("192.") || Serial.StartsWith("10.") || Serial.StartsWith("172.");
    public string ConnectionTypeDisplay => IsWireless ? "Wireless Wi-Fi" : "USB Cable";
    
    public string SwitchModeText => IsWireless ? "🔌 Switch to USB" : "📶 Switch to Wi-Fi";
    public string DisconnectOrReconnectText => IsWireless ? "✕ Disconnect Wi-Fi" : "⚡ Reset USB Link";
    
    public int BatteryLevel { get; set; } = -1;
    public string BatteryDisplay => BatteryLevel >= 0 ? $"{BatteryLevel}%" : "--%";
    
    public DeviceConnectionStatus Status { get; set; } = DeviceConnectionStatus.Ready;
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
