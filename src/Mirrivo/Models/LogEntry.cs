namespace Mirrivo.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
    Command
}

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; } = LogLevel.Info;
    public string Message { get; set; } = string.Empty;

    public string TimeFormatted => Timestamp.ToString("HH:mm:ss");

    public string LevelColor => Level switch
    {
        LogLevel.Success => "#10B981", // Emerald
        LogLevel.Warning => "#F5A623", // Amber
        LogLevel.Error => "#EE0000",   // Vercel Red
        LogLevel.Command => "#0070F3", // Vercel Blue
        _ => "#A1A1A1"                // Geist Body Gray
    };
}
