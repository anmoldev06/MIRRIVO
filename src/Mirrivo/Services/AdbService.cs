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

    public async Task SetScreenAwakeAsync(string serial, bool stayAwake, bool neverTimeout)
    {
        try
        {
            if (stayAwake)
            {
                await RunAdbCommandAsync($"-s {serial} shell svc power stayon true", 2000);
                await RunAdbCommandAsync($"-s {serial} shell settings put global stay_on_while_plugged_in 15", 2000);
            }

            if (neverTimeout)
            {
                await RunAdbCommandAsync($"-s {serial} shell settings put system screen_off_timeout 2147483647", 2000);
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

    public async Task<(bool Success, string Message)> DisconnectAsync(string address)
    {
        Log($"Disconnecting {address}...", LogLevel.Command);
        var (code, output, _) = await RunAdbCommandAsync($"disconnect {address}");
        Log(output, LogLevel.Info);
        return (code == 0, output);
    }

    public async Task SendKeyEventAsync(string serial, int keycode)
    {
        await RunAdbCommandAsync($"-s {serial} shell input keyevent {keycode}");
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
}
