using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Mirrivo.Models;

namespace Mirrivo.Services;

public class AdbService
{
    private static readonly Lazy<AdbService> _instance = new(() => new AdbService());
    public static AdbService Instance => _instance.Value;

    private readonly EngineService _engine = EngineService.Instance;

    public event Action<LogEntry>? OnLog;

    private void Log(string message, LogLevel level = LogLevel.Info)
    {
        OnLog?.Invoke(new LogEntry { Message = message, Level = level });
    }

    public async Task<(int ExitCode, string Output, string Error)> RunAdbCommandAsync(string arguments, int timeoutMs = 15000)
    {
        if (!File.Exists(_engine.AdbPath))
        {
            return (-1, string.Empty, "adb.exe not found.");
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _engine.AdbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            process.Start();
            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            await process.WaitForExitAsync(cts.Token);

            string output = await outTask;
            string error = await errTask;

            return (process.ExitCode, output.Trim(), error.Trim());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { }
            return (-1, string.Empty, "Command timed out.");
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    public async Task<List<DeviceItem>> GetDevicesAsync()
    {
        var devices = new List<DeviceItem>();
        var (_, output, _) = await RunAdbCommandAsync("devices -l");

        if (string.IsNullOrWhiteSpace(output))
            return devices;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = Regex.Match(line.Trim(), @"^([^\s]+)\s+([^\s]+)(.*)$");
            if (!match.Success) continue;

            string serial = match.Groups[1].Value.Trim();
            string stateStr = match.Groups[2].Value.Trim();
            string extra = match.Groups[3].Value.Trim();

            var dev = new DeviceItem
            {
                Serial = serial,
                State = stateStr
            };

            if (stateStr.Equals("unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                dev.Status = DeviceConnectionStatus.Unauthorized;
            }
            else if (stateStr.Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                dev.Status = DeviceConnectionStatus.Ready;
            }
            else
            {
                dev.Status = DeviceConnectionStatus.Offline;
            }

            var modelMatch = Regex.Match(extra, @"model:([^\s]+)");
            if (modelMatch.Success)
            {
                dev.Model = modelMatch.Groups[1].Value.Replace('_', ' ');
            }

            var productMatch = Regex.Match(extra, @"product:([^\s]+)");
            if (productMatch.Success)
            {
                dev.Product = productMatch.Groups[1].Value;
            }

            var transportMatch = Regex.Match(extra, @"transport_id:([^\s]+)");
            if (transportMatch.Success)
            {
                dev.TransportId = transportMatch.Groups[1].Value;
            }

            devices.Add(dev);
        }

        return devices;
    }

    public async Task<int> GetBatteryLevelAsync(string serial)
    {
        try
        {
            var (code1, out1, _) = await RunAdbCommandAsync($"-s {serial} shell cmd battery get level", 2500);
            if (code1 == 0 && int.TryParse(out1.Trim(), out int lvl1) && lvl1 >= 0 && lvl1 <= 100)
            {
                return lvl1;
            }

            var (_, out2, _) = await RunAdbCommandAsync($"-s {serial} shell dumpsys battery", 3000);
            if (!string.IsNullOrEmpty(out2))
            {
                var match = Regex.Match(out2, @"\blevel:\s*(\d+)", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int lvl2) && lvl2 >= 0 && lvl2 <= 100)
                {
                    return lvl2;
                }
            }

            var (_, out3, _) = await RunAdbCommandAsync($"-s {serial} shell dumpsys batteryproperties", 2500);
            if (!string.IsNullOrEmpty(out3))
            {
                var match = Regex.Match(out3, @"\blevel:\s*(\d+)", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int lvl3) && lvl3 >= 0 && lvl3 <= 100)
                {
                    return lvl3;
                }
            }
        }
        catch { }

        return -1;
    }

    public async Task SetShowTouchesAsync(string serial, bool show)
    {
        try
        {
            int val = show ? 1 : 0;
            Log($"Setting physical screen touches to {(show ? "ON" : "OFF")} on {serial}...", LogLevel.Command);
            var (code, _, err) = await RunAdbCommandAsync($"-s {serial} shell settings put system show_touches {val}", 2500);
            if (code == 0)
            {
                Log($"Screen touches {(show ? "enabled (white touch dots active)" : "disabled")}.", LogLevel.Success);
            }
            else
            {
                Log($"Show touches status: {err}", LogLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            Log($"Error toggling show touches: {ex.Message}", LogLevel.Warning);
        }
    }

    public async Task SetShowFpsAsync(string serial, bool showFps)
    {
        try
        {
            int val = showFps ? 1 : 0;
            Log($"Setting real-time screen FPS / Refresh Rate counter to {(showFps ? "ON" : "OFF")} on {serial}...", LogLevel.Command);
            await RunAdbCommandAsync($"-s {serial} shell settings put system show_refresh_rate {val}", 2500);
            await RunAdbCommandAsync($"-s {serial} shell settings put system show_fps {val}", 2500);
            Log($"Real-time FPS / Refresh Rate counter is now {(showFps ? "VISIBLE on phone screen" : "HIDDEN")}.", LogLevel.Success);
        }
        catch (Exception ex)
        {
            Log($"Error toggling FPS counter: {ex.Message}", LogLevel.Warning);
        }
    }

    public async Task SetScreenAwakeAsync(string serial, bool stayAwake, bool neverTimeout = true)
    {
        try
        {
            if (stayAwake)
            {
                await RunAdbCommandAsync($"-s {serial} shell svc power stayon true", 2000);
                await RunAdbCommandAsync($"-s {serial} shell settings put global stay_on_while_plugged_in 15", 2000);
                await RunAdbCommandAsync($"-s {serial} shell settings put system screen_off_timeout 2147483647", 2000);
                Log("Stay Awake activated (Screen will stay on and never sleep).", LogLevel.Success);
            }
            else
            {
                await RunAdbCommandAsync($"-s {serial} shell svc power stayon false", 2000);
                await RunAdbCommandAsync($"-s {serial} shell settings put global stay_on_while_plugged_in 0", 2000);
                await RunAdbCommandAsync($"-s {serial} shell settings put system screen_off_timeout 60000", 2000);
                Log("Stay Awake deactivated (Restored normal 1-minute screen timeout).", LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to configure screen awake settings: {ex.Message}", LogLevel.Warning);
        }
    }

    public async Task RestoreScreenAwakeAsync(string serial)
    {
        try
        {
            await RunAdbCommandAsync($"-s {serial} shell svc power stayon false", 2000);
            await RunAdbCommandAsync($"-s {serial} shell settings put global stay_on_while_plugged_in 0", 2000);
            await RunAdbCommandAsync($"-s {serial} shell settings put system screen_off_timeout 120000", 2000);
        }
        catch { }
    }

    public async Task<string?> GetDeviceIpAddressAsync(string serial)
    {
        var (_, out1, _) = await RunAdbCommandAsync($"-s {serial} shell ip -f inet addr show wlan0", 5000);
        var match1 = Regex.Match(out1, @"inet\s+(\d+\.\d+\.\d+\.\d+)");
        if (match1.Success)
            return match1.Groups[1].Value;

        var (_, out2, _) = await RunAdbCommandAsync($"-s {serial} shell ip route", 5000);
        var match2 = Regex.Match(out2, @"src\s+(\d+\.\d+\.\d+\.\d+)");
        if (match2.Success)
            return match2.Groups[1].Value;

        var (_, out3, _) = await RunAdbCommandAsync($"-s {serial} shell ifconfig wlan0", 5000);
        var match3 = Regex.Match(out3, @"inet\s+addr:(\d+\.\d+\.\d+\.\d+)");
        if (match3.Success)
            return match3.Groups[1].Value;

        var (_, out4, _) = await RunAdbCommandAsync($"-s {serial} shell getprop dhcp.wlan0.ipaddress", 5000);
        if (!string.IsNullOrWhiteSpace(out4) && Regex.IsMatch(out4.Trim(), @"^\d+\.\d+\.\d+\.\d+$"))
            return out4.Trim();

        return null;
    }

    public async Task<(bool Success, string Message)> SwitchToWirelessAsync(string serial, int port = 5555)
    {
        Log($"Switching device {serial} to Wireless TCP/IP mode on port {port}...", LogLevel.Command);

        string? ip = await GetDeviceIpAddressAsync(serial);
        if (string.IsNullOrEmpty(ip))
        {
            string err = "Failed to detect device Wi-Fi IP. Please ensure phone is connected to the same Wi-Fi network.";
            Log(err, LogLevel.Error);
            return (false, err);
        }

        Log($"Found device Wi-Fi IP: {ip}", LogLevel.Info);

        var (code1, out1, err1) = await RunAdbCommandAsync($"-s {serial} tcpip {port}");
        if (code1 != 0 && !out1.Contains("restarting in TCP mode", StringComparison.OrdinalIgnoreCase))
        {
            string err = $"Failed to restart ADB in TCP mode: {err1}";
            Log(err, LogLevel.Error);
            return (false, err);
        }

        Log("ADB restarted in TCP mode. Connecting wirelessly...", LogLevel.Info);
        await Task.Delay(1500);

        var (code2, out2, err2) = await RunAdbCommandAsync($"connect {ip}:{port}");
        if (out2.Contains("connected", StringComparison.OrdinalIgnoreCase))
        {
            string msg = $"Successfully connected wirelessly to {ip}:{port}! You can now disconnect the USB cable.";
            Log(msg, LogLevel.Success);
            return (true, msg);
        }
        else
        {
            string msg = $"Connected response: {out2} {err2}";
            Log(msg, LogLevel.Warning);
            return (true, msg);
        }
    }

    public async Task<(bool Success, string Message)> SwitchToUsbAsync(string serial)
    {
        Log($"Switching device {serial} back to USB mode...", LogLevel.Command);
        var (code, output, error) = await RunAdbCommandAsync($"-s {serial} usb");
        string msg = !string.IsNullOrEmpty(output) ? output : error;
        Log($"ADB USB Mode: {msg}", LogLevel.Info);
        return (code == 0, "Switched device back to USB mode. Please ensure phone is plugged in via USB cable.");
    }

    public async Task ReconnectUsbAsync()
    {
        Log("Refreshing ADB USB connection...", LogLevel.Command);
        await RunAdbCommandAsync("reconnect");
        await RunAdbCommandAsync("devices -l");
    }

    public async Task<(bool Success, string Message)> ConnectWirelessAsync(string ip, int port = 5555)
    {
        string address = $"{ip}:{port}";
        Log($"Connecting to wireless device {address}...", LogLevel.Command);

        var (code, output, error) = await RunAdbCommandAsync($"connect {address}");
        if (output.Contains("connected to", StringComparison.OrdinalIgnoreCase))
        {
            Log($"Connected successfully to {address}", LogLevel.Success);
            return (true, output);
        }

        string msg = !string.IsNullOrEmpty(error) ? error : output;
        Log($"Connection failed: {msg}", LogLevel.Error);
        return (false, msg);
    }

    public async Task<(bool Success, string Message)> PairWirelessAsync(string ip, int port, string pairingCode)
    {
        string address = $"{ip}:{port}";
        Log($"Pairing with wireless device {address} (Code: {pairingCode})...", LogLevel.Command);

        var (code, output, error) = await RunAdbCommandAsync($"pair {address} {pairingCode}");
        if (output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
        {
            Log($"Successfully paired with {address}", LogLevel.Success);
            return (true, output);
        }

        string msg = !string.IsNullOrEmpty(error) ? error : output;
        Log($"Pairing failed: {msg}", LogLevel.Error);
        return (false, msg);
    }

    public async Task<(bool Success, string Message)> DisconnectAsync(string address = "")
    {
        string cmd = string.IsNullOrWhiteSpace(address) ? "disconnect" : $"disconnect {address.Trim()}";
        Log($"Disconnecting wireless link {(string.IsNullOrWhiteSpace(address) ? "all" : address)}...", LogLevel.Command);
        var (code, output, _) = await RunAdbCommandAsync(cmd);
        Log(output, LogLevel.Info);
        return (code == 0, output);
    }

    public async Task SendKeyEventAsync(string serial, int keycode)
    {
        await RunAdbCommandAsync($"-s {serial} shell input keyevent {keycode}");
    }

    /// <summary>
    /// Wakes up the device CPU/display from sleep/dozing and attempts to dismiss keyguard.
    /// Returns true if device is still locked with secure PIN/pattern/fingerprint, false if fully unlocked.
    /// </summary>
    public async Task<bool> WakeAndUnlockDeviceAsync(string serial)
    {
        try
        {
            // 1. Send KEYCODE_WAKEUP to wake screen & CPU from sleep/dozing
            // 2. Dismiss insecure/swipe lockscreen
            // 3. Send KEYCODE_MENU (82) to dismiss non-secure lock prompt
            await RunAdbCommandAsync($"-s {serial} shell \"input keyevent KEYCODE_WAKEUP && wm dismiss-keyguard && input keyevent 82\"", 3000);

            // Small delay for WindowManager to update policy
            await Task.Delay(200);

            return await IsDeviceLockedAsync(serial);
        }
        catch (Exception ex)
        {
            Log($"Failed to wake/unlock device {serial}: {ex.Message}", LogLevel.Warning);
            return false;
        }
    }

    /// <summary>
    /// Checks whether the device is currently showing an active keyguard / lock screen.
    /// </summary>
    public async Task<bool> IsDeviceLockedAsync(string serial)
    {
        try
        {
            var (_, output, _) = await RunAdbCommandAsync($"-s {serial} shell \"dumpsys window policy | grep showing=\"", 2000);
            if (!string.IsNullOrWhiteSpace(output) && output.Contains("showing=true"))
            {
                return true;
            }
        }
        catch { }
        return false;
    }

    public async Task<(bool Success, string Text)> GetDeviceClipboardAsync(string serial)
    {
        try
        {
            var (code, outStr, _) = await RunAdbCommandAsync($"-s {serial} shell cmd clipboard get", 3000);
            if (code == 0 && !string.IsNullOrWhiteSpace(outStr) && !outStr.Contains("No shell command"))
            {
                return (true, outStr.Trim());
            }
        }
        catch { }
        return (false, string.Empty);
    }

    public async Task<bool> SetDeviceClipboardAsync(string serial, string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            // Ensure device is awake to accept clipboard / input
            await WakeAndUnlockDeviceAsync(serial);

            string safeText = text.Replace("\"", "\\\"").Replace("$", "\\$");
            var (code, outStr, _) = await RunAdbCommandAsync($"-s {serial} shell \"cmd clipboard set \\\"{safeText}\\\"\"", 3000);
            if (code == 0 && !outStr.Contains("No shell command")) return true;

            // Direct input text fallback
            string escapedInput = safeText.Replace(" ", "%s");
            await RunAdbCommandAsync($"-s {serial} shell input text \"{escapedInput}\"", 2000);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Error setting device clipboard: {ex.Message}", LogLevel.Warning);
            return false;
        }
    }

    public async Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath)
    {
        Log($"Installing APK '{Path.GetFileName(apkPath)}' on {serial}...", LogLevel.Command);
        var (code, output, error) = await RunAdbCommandAsync($"-s {serial} install -r \"{apkPath}\"", 60000);
        if (output.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            string msg = $"Successfully installed {Path.GetFileName(apkPath)}!";
            Log(msg, LogLevel.Success);
            return (true, msg);
        }

        string err = !string.IsNullOrEmpty(error) ? error : output;
        Log($"Install failed: {err}", LogLevel.Error);
        return (false, err);
    }

    public static bool IsMediaFile(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" or ".heif"
                   or ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".flv" or ".3gp" or ".wmv" or ".m4v";
    }

    public async Task<(bool Success, string Message, string Destination)> SmartPushFileAsync(
        string serial,
        string filePath,
        IProgress<(double Percent, long TransferredBytes, long TotalBytes, string Speed)>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return (false, "File does not exist.", "");

        string fileName = Path.GetFileName(filePath);
        long fileTotalBytes = new FileInfo(filePath).Length;
        bool isMedia = IsMediaFile(filePath);

        // Videos/Images -> /sdcard/DCIM/Mirrivo Shared/
        // APKs & all other files -> /sdcard/Download/
        string targetDir = isMedia ? "/sdcard/DCIM/Mirrivo Shared/" : "/sdcard/Download/";
        string targetRemotePath = targetDir + fileName;
        string destLabel = isMedia ? "DCIM/Mirrivo Shared" : "Downloads";

        Log($"Sharing '{fileName}' to {destLabel}...", LogLevel.Command);

        // Ensure remote directory exists on phone
        await RunAdbCommandAsync($"-s {serial} shell \"mkdir -p '{targetDir}'\"", 3000);

        var psi = new ProcessStartInfo
        {
            FileName = _engine.AdbPath,
            Arguments = $"-s {serial} push \"{filePath}\" \"{targetDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Monitor remote file size via stat -c %s to calculate real-time progress
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            while (!process.HasExited && !monitorCts.Token.IsCancellationRequested)
            {
                await Task.Delay(150, monitorCts.Token);
                try
                {
                    var (code, outStr, _) = await RunAdbCommandAsync($"-s {serial} shell \"stat -c %s '{targetRemotePath}'\"", 1000);
                    if (code == 0 && long.TryParse(outStr.Trim(), out long currentBytes))
                    {
                        double percent = fileTotalBytes > 0 ? Math.Min(99.0, (double)currentBytes / fileTotalBytes * 100.0) : 0;
                        double elapsedSec = sw.Elapsed.TotalSeconds;
                        double speedMb = elapsedSec > 0 ? (currentBytes / (1024.0 * 1024.0)) / elapsedSec : 0;
                        progress?.Report((percent, currentBytes, fileTotalBytes, $"{speedMb:F1} MB/s"));
                    }
                }
                catch
                {
                    // Ignore transient polling exceptions
                }
            }
        }, monitorCts.Token);

        using var reg = ct.Register(() =>
        {
            try { process.Kill(); } catch { }
        });

        string output = "";
        string error = "";
        try
        {
            output = await process.StandardOutput.ReadToEndAsync(ct);
            error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { }
            monitorCts.Cancel();
            await RunAdbCommandAsync($"-s {serial} shell \"rm -f '{targetRemotePath}'\"", 2000);
            Log($"Upload of '{fileName}' cancelled by user.", LogLevel.Warning);
            return (false, "Transfer cancelled.", destLabel);
        }

        monitorCts.Cancel();
        try { await monitorTask; } catch { }

        if (ct.IsCancellationRequested)
        {
            await RunAdbCommandAsync($"-s {serial} shell \"rm -f '{targetRemotePath}'\"", 2000);
            return (false, "Transfer cancelled.", destLabel);
        }

        if (process.ExitCode == 0)
        {
            progress?.Report((100.0, fileTotalBytes, fileTotalBytes, "Completed"));

            // Broadcast media scanner so images/videos appear immediately in Gallery
            if (isMedia)
            {
                await RunAdbCommandAsync($"-s {serial} shell \"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d 'file://{targetRemotePath}'\"", 3000);
            }

            string msg = $"Transferred {fileName} to {destLabel} successfully!";
            Log(msg, LogLevel.Success);
            return (true, msg, destLabel);
        }

        string err = !string.IsNullOrEmpty(error) ? error : output;
        Log($"Transfer failed: {err}", LogLevel.Error);
        return (false, err, destLabel);
    }

    public async Task<(bool Success, string Message)> PushFileAsync(string serial, string filePath, string targetDir = "/sdcard/Download/")
    {
        Log($"Pushing file '{Path.GetFileName(filePath)}' to {targetDir}...", LogLevel.Command);
        var (code, output, error) = await RunAdbCommandAsync($"-s {serial} push \"{filePath}\" \"{targetDir}\"", 60000);
        if (code == 0)
        {
            string msg = $"Transferred {Path.GetFileName(filePath)} to phone Downloads!";
            Log(msg, LogLevel.Success);
            return (true, msg);
        }

        string err = !string.IsNullOrEmpty(error) ? error : output;
        Log($"Transfer failed: {err}", LogLevel.Error);
        return (false, err);
    }

    public async Task<(bool Success, string FilePath)> TakeScreenshotAsync(string serial)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string localPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            $"MIRRIVO_Screenshot_{timestamp}.png"
        );

        Log($"Capturing screenshot from {serial}...", LogLevel.Command);
        string phoneTemp = $"/sdcard/mirrivo_screen_{timestamp}.png";

        await RunAdbCommandAsync($"-s {serial} shell screencap -p {phoneTemp}");
        var (code, _, err) = await RunAdbCommandAsync($"-s {serial} pull {phoneTemp} \"{localPath}\"");
        await RunAdbCommandAsync($"-s {serial} shell rm {phoneTemp}");

        if (code == 0 && File.Exists(localPath))
        {
            Log($"Screenshot saved to {localPath}", LogLevel.Success);
            return (true, localPath);
        }

        Log($"Screenshot failed: {err}", LogLevel.Error);
        return (false, err);
    }

    // ==============================================
    // APP MANAGEMENT & GHOST DISPLAY OPERATIONS
    // ==============================================
    private static readonly Dictionary<string, (string Name, string Category, string Icon, string Color)> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        { "com.whatsapp", ("WhatsApp", "Messaging", "💬", "#25D366") },
        { "com.whatsapp.w4b", ("WhatsApp Business", "Business", "💼", "#25D366") },
        { "com.instagram.android", ("Instagram", "Social", "📸", "#E1306C") },
        { "com.google.android.youtube", ("YouTube", "Video", "▶️", "#FF0000") },
        { "com.google.android.apps.youtube.creator", ("YouTube Studio", "Creator & Video", "▶️", "#FF0000") },
        { "com.android.chrome", ("Google Chrome", "Browser", "🌐", "#4285F4") },
        { "org.telegram.messenger", ("Telegram", "Messaging", "✈️", "#0088CC") },
        { "com.snapchat.android", ("Snapchat", "Social", "👻", "#FFFC00") },
        { "com.facebook.katana", ("Facebook", "Social", "👥", "#1877F2") },
        { "com.facebook.orca", ("Messenger", "Messaging", "⚡", "#006AFF") },
        { "com.spotify.music", ("Spotify", "Music", "🎵", "#1DB954") },
        { "com.netflix.mediaclient", ("Netflix", "Video", "🍿", "#E50914") },
        { "com.amazon.mShop.android.shopping", ("Amazon Shopping", "Shopping", "🛒", "#FF9900") },
        { "com.openai.chatgpt", ("ChatGPT", "AI Assistant", "🤖", "#10A37F") },
        { "com.google.android.apps.bard", ("Google Gemini", "AI Assistant", "✨", "#4285F4") },
        { "com.google.android.apps.docs.editors.docs", ("Google Docs", "Productivity", "📄", "#4285F4") },
        { "com.google.android.apps.docs.editors.sheets", ("Google Sheets", "Productivity", "📊", "#0F9D58") },
        { "com.google.android.apps.docs.editors.slides", ("Google Slides", "Productivity", "📽️", "#F4B400") },
        { "com.alibaba.intl.android.apps.poseidon", ("Alibaba.com", "Wholesale & Shopping", "📦", "#FF6A00") },
        { "com.scanbridge.app", ("ScanBridge", "Scanner Utility", "📷", "#0071E3") },
        { "in.gov.uidai.facerd", ("Aadhaar FaceRD", "Identity & Auth", "🪪", "#0088CC") },
        { "com.twitter.android", ("X (Twitter)", "Social", "✖️", "#1DA1F2") },
        { "com.reddit.frontpage", ("Reddit", "Community", "🤖", "#FF4500") },
        { "com.linkedin.android", ("LinkedIn", "Professional", "💼", "#0A66C2") },
        { "com.discord", ("Discord", "Gaming & Chat", "🎮", "#5865F2") },
        { "us.zoom.videomeetings", ("Zoom", "Meetings", "📹", "#2D8CFF") },
        { "com.google.android.gm", ("Gmail", "Mail", "✉️", "#EA4335") },
        { "com.google.android.apps.maps", ("Google Maps", "Navigation", "🗺️", "#34A853") },
        { "com.google.android.apps.photos", ("Google Photos", "Photos", "🖼️", "#4285F4") },
        { "com.google.android.apps.translate", ("Google Translate", "Utility", "🔤", "#4285F4") },
        { "com.microsoft.office.officehubrow", ("Microsoft 365", "Office", "📊", "#D83B01") },
        { "com.microsoft.office.outlook", ("Microsoft Outlook", "Mail", "📨", "#0078D4") },
        { "com.dji.mimo", ("DJI Mimo", "Camera / Drone", "🚁", "#000000") },
        { "com.activ.health", ("Activ Health", "Health & Fitness", "❤️", "#E60000") },
        { "com.brother.mfc.connect", ("Brother Mobile Connect", "Printer", "🖨️", "#003399") },
        { "com.airtel.airtelapp", ("Airtel Thanks", "Telecom", "📡", "#E40000") },
        { "com.sec.android.app.camera", ("Samsung Camera", "Camera", "📷", "#000000") },
        { "com.sec.android.gallery3d", ("Samsung Gallery", "Photos", "🎨", "#E4405F") },
        { "com.sec.android.app.sbrowser", ("Samsung Internet", "Browser", "🧭", "#6B38FB") },
        { "com.sec.android.app.myfiles", ("My Files", "File Manager", "📁", "#FF9900") },
        { "com.sec.android.app.clockpackage", ("Clock", "Utility", "⏰", "#2C2C2E") },
        { "com.sec.android.app.popupcalculator", ("Calculator", "Utility", "🔢", "#30D158") },
        { "com.android.settings", ("System Settings", "Settings", "⚙️", "#8E8E93") },
    };

    public static bool IsKnownApp(string packageName) => KnownApps.ContainsKey(packageName);

    private static bool IsJunkPackage(string pkg)
    {
        if (KnownApps.ContainsKey(pkg)) return false;
        string p = pkg.ToLowerInvariant();
        return p.Contains("bixby.ondevice") ||
               p.Contains(".overlay") ||
               p.Contains("contactkeys") ||
               p.Contains(".cts.") ||
               p.Contains(".test.") ||
               p.Contains("syncadapter") ||
               p.Contains("autofill") ||
               p.EndsWith(".overlay") ||
               p.StartsWith("com.android.internal") ||
               p.StartsWith("com.samsung.android.rubin") ||
               p.StartsWith("com.samsung.android.scs") ||
               p.StartsWith("com.samsung.android.mcfds");
    }

    public async Task<List<AppItem>> GetInstalledAppsAsync(string serial)
    {
        var appList = new List<AppItem>();
        var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Log($"Scanning installed apps on device {serial}...", LogLevel.Command);

            // 1. Get user installed (3rd-party) packages
            var thirdPartySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var (c1, thirdPartyOutput, _) = await RunAdbCommandAsync($"-s {serial} shell pm list packages -3", 10000);
            if (c1 == 0 && !string.IsNullOrWhiteSpace(thirdPartyOutput))
            {
                foreach (var line in thirdPartyOutput.Split('\n'))
                {
                    string pkg = line.Trim();
                    if (pkg.StartsWith("package:")) pkg = pkg.Substring("package:".Length).Trim();
                    if (!string.IsNullOrEmpty(pkg) && !IsJunkPackage(pkg)) thirdPartySet.Add(pkg);
                }
            }

            // 2. Query launchable activities (user-facing apps) via dumpsys package resolver table
            var (c2, launchableOutput, _) = await RunAdbCommandAsync($"-s {serial} shell \"dumpsys package | grep -B 1 'android.intent.category.LAUNCHER'\"", 12000);
            if (c2 == 0 && !string.IsNullOrWhiteSpace(launchableOutput))
            {
                foreach (var line in launchableOutput.Split('\n'))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"([a-zA-Z0-9_\.]+)/([a-zA-Z0-9_\.]+)");
                    if (match.Success)
                    {
                        string pkg = match.Groups[1].Value.Trim();
                        string act = match.Groups[2].Value.Trim();
                        if (!IsJunkPackage(pkg) && !seenPackages.Contains(pkg))
                        {
                            seenPackages.Add(pkg);
                            bool isThirdParty = thirdPartySet.Contains(pkg);
                            appList.Add(CreateAppItem(pkg, !isThirdParty, $"{pkg}/{act}"));
                        }
                    }
                }
            }

            // Fallback: If dumpsys grep returned few apps, query pm query-activities
            if (appList.Count < 5)
            {
                var (c3, pmOutput, _) = await RunAdbCommandAsync($"-s {serial} shell \"pm query-activities -a android.intent.action.MAIN -c android.intent.category.LAUNCHER\"", 8000);
                if (c3 == 0 && !string.IsNullOrWhiteSpace(pmOutput))
                {
                    foreach (var line in pmOutput.Split('\n'))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"([a-zA-Z0-9_\.]+)/([a-zA-Z0-9_\.]+)");
                        if (match.Success)
                        {
                            string pkg = match.Groups[1].Value.Trim();
                            string act = match.Groups[2].Value.Trim();
                            if (!IsJunkPackage(pkg) && !seenPackages.Contains(pkg))
                            {
                                seenPackages.Add(pkg);
                                bool isThirdParty = thirdPartySet.Contains(pkg);
                                appList.Add(CreateAppItem(pkg, !isThirdParty, $"{pkg}/{act}"));
                            }
                        }
                    }
                }
            }

            // 3. Add any valid remaining 3rd-party user packages
            foreach (var pkg in thirdPartySet)
            {
                if (!IsJunkPackage(pkg) && !seenPackages.Contains(pkg))
                {
                    seenPackages.Add(pkg);
                    appList.Add(CreateAppItem(pkg, false));
                }
            }

            // Sort: User apps first, then alphabetically
            appList = appList
                .OrderBy(a => a.IsSystemApp)
                .ThenBy(a => a.DisplayName)
                .ToList();

            Log($"Discovered {appList.Count} installed user-accessible applications on {serial}", LogLevel.Success);
        }
        catch (Exception ex)
        {
            Log($"Failed to scan installed apps: {ex.Message}", LogLevel.Error);
        }

        return appList;
    }

    private AppItem CreateAppItem(string packageName, bool isSystem, string? mainActivity = null)
    {
        string name;
        string cat = "App";
        string icon = "📱";
        string color = isSystem ? "#8E8E93" : "#0071E3";

        if (KnownApps.TryGetValue(packageName, out var meta))
        {
            name = meta.Name;
            cat = meta.Category;
            icon = meta.Icon;
            color = meta.Color;
        }
        else
        {
            name = HumanizePackageName(packageName);
            // Categorize by keywords
            if (packageName.Contains("camera", StringComparison.OrdinalIgnoreCase)) { cat = "Camera"; icon = "📷"; color = "#FF2D55"; }
            else if (packageName.Contains("gallery", StringComparison.OrdinalIgnoreCase) || packageName.Contains("photo", StringComparison.OrdinalIgnoreCase)) { cat = "Media"; icon = "🖼️"; color = "#AF52DE"; }
            else if (packageName.Contains("music", StringComparison.OrdinalIgnoreCase) || packageName.Contains("audio", StringComparison.OrdinalIgnoreCase)) { cat = "Audio"; icon = "🎵"; color = "#34C759"; }
            else if (packageName.Contains("video", StringComparison.OrdinalIgnoreCase) || packageName.Contains("player", StringComparison.OrdinalIgnoreCase)) { cat = "Video"; icon = "🎬"; color = "#FF9500"; }
            else if (packageName.Contains("browser", StringComparison.OrdinalIgnoreCase) || packageName.Contains("web", StringComparison.OrdinalIgnoreCase)) { cat = "Browser"; icon = "🌐"; color = "#0071E3"; }
            else if (packageName.Contains("mail", StringComparison.OrdinalIgnoreCase) || packageName.Contains("email", StringComparison.OrdinalIgnoreCase)) { cat = "Mail"; icon = "✉️"; color = "#5856D6"; }
            else if (packageName.Contains("chat", StringComparison.OrdinalIgnoreCase) || packageName.Contains("message", StringComparison.OrdinalIgnoreCase)) { cat = "Messaging"; icon = "💬"; color = "#30D158"; }
            else if (packageName.Contains("shop", StringComparison.OrdinalIgnoreCase) || packageName.Contains("store", StringComparison.OrdinalIgnoreCase)) { cat = "Shopping"; icon = "🛒"; color = "#FF9500"; }
            else if (packageName.Contains("game", StringComparison.OrdinalIgnoreCase)) { cat = "Gaming"; icon = "🎮"; color = "#FF3B30"; }
            else if (packageName.Contains("health", StringComparison.OrdinalIgnoreCase) || packageName.Contains("fit", StringComparison.OrdinalIgnoreCase)) { cat = "Health"; icon = "❤️"; color = "#FF2D55"; }
        }

        string? cachedIcon = AppIconService.Instance.GetCachedIconPath(packageName);

        return new AppItem
        {
            PackageName = packageName,
            DisplayName = name,
            MainActivity = mainActivity ?? "",
            Category = cat,
            IsSystemApp = isSystem,
            IconGlyph = icon,
            IconColor = color,
            IconPath = cachedIcon
        };
    }

    private static string HumanizePackageName(string packageName)
    {
        var parts = packageName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return packageName;

        // Take last or second-to-last meaningful part
        string segment = parts[^1];
        if (segment.Length <= 2 && parts.Length >= 2) segment = parts[^2];
        if (segment.Equals("android", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2) segment = parts[^2];

        // Replace underscores, dashes with space and title case
        segment = segment.Replace('_', ' ').Replace('-', ' ');
        var words = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
            {
                words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1).ToLower();
            }
        }
        return string.Join(" ", words);
    }

    public async Task<(bool Success, string Message)> UninstallAppAsync(string serial, string packageName)
    {
        Log($"Uninstalling package {packageName} from {serial}...", LogLevel.Command);
        var (code, output, error) = await RunAdbCommandAsync($"-s {serial} uninstall {packageName}");
        if (output.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            Log($"Uninstalled {packageName} successfully!", LogLevel.Success);
            return (true, "Uninstalled successfully");
        }
        string err = !string.IsNullOrEmpty(error) ? error : output;
        Log($"Failed to uninstall {packageName}: {err}", LogLevel.Error);
        return (false, err);
    }

    public async Task<(bool Success, string Message)> ClearAppDataAsync(string serial, string packageName)
    {
        Log($"Clearing data & cache for {packageName}...", LogLevel.Command);
        var (code, output, error) = await RunAdbCommandAsync($"-s {serial} shell pm clear {packageName}");
        if (output.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            Log($"Cleared data for {packageName} successfully!", LogLevel.Success);
            return (true, "App data cleared successfully");
        }
        string err = !string.IsNullOrEmpty(error) ? error : output;
        Log($"Failed to clear data for {packageName}: {err}", LogLevel.Error);
        return (false, err);
    }

    public async Task<(bool Success, string Message)> ForceStopAppAsync(string serial, string packageName)
    {
        Log($"Force-stopping {packageName}...", LogLevel.Command);
        await RunAdbCommandAsync($"-s {serial} shell am force-stop {packageName}");
        Log($"Force-stopped {packageName}", LogLevel.Info);
        return (true, "App stopped");
    }

    public async Task<(bool Success, string Message)> LaunchAppOnPhoneAsync(string serial, string packageName, string? mainActivity = null)
    {
        Log($"Launching {packageName} on phone display...", LogLevel.Command);
        int code;
        string output, error;

        if (!string.IsNullOrWhiteSpace(mainActivity))
        {
            (code, output, error) = await RunAdbCommandAsync($"-s {serial} shell am start -n {mainActivity}");
            if (code == 0)
            {
                Log($"Opened {packageName} on phone screen", LogLevel.Success);
                return (true, "Opened on phone screen");
            }
        }

        (code, output, error) = await RunAdbCommandAsync($"-s {serial} shell monkey -p {packageName} -c android.intent.category.LAUNCHER 1");
        if (code == 0)
        {
            Log($"Opened {packageName} on phone screen", LogLevel.Success);
            return (true, "Opened on phone screen");
        }
        string err = !string.IsNullOrEmpty(error) ? error : output;
        return (false, err);
    }
}
