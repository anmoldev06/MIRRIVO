using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Mirrivo.Models;

namespace Mirrivo.Services;

public class MirrorService
{
    private static readonly Lazy<MirrorService> _instance = new(() => new MirrorService());
    public static MirrorService Instance => _instance.Value;

    private readonly EngineService _engine = EngineService.Instance;
    private readonly ConcurrentDictionary<string, Process> _activeSessions = new();

    public event Action<string, bool>? OnMirrorStateChanged;
    public event Action<string, double>? OnFpsUpdated;
    public event Action<LogEntry>? OnLog;

    #region Win32 Window Positioning & Topmost API
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    #endregion

    private void Log(string message, LogLevel level = LogLevel.Info)
    {
        OnLog?.Invoke(new LogEntry { Message = message, Level = level });
    }

    public bool IsMirroring(string serial) => _activeSessions.ContainsKey(serial);

    public void SetAlwaysOnTop(string serial, bool alwaysOnTop)
    {
        try
        {
            if (_activeSessions.TryGetValue(serial, out var proc) && !proc.HasExited)
            {
                uint targetPid = (uint)proc.Id;

                // 1. Try MainWindowHandle
                proc.Refresh();
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    ApplyTopMost(proc.MainWindowHandle, alwaysOnTop);
                }

                // 2. Enum all windows for this process to ensure SDL window is updated
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == targetPid)
                    {
                        ApplyTopMost(hWnd, alwaysOnTop);
                    }
                    return true;
                }, IntPtr.Zero);

                Log($"Mirror window Always-On-Top set to: {(alwaysOnTop ? "ON (Pinned)" : "OFF (Unpinned)")}", LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            Log($"Error setting Always-On-Top: {ex.Message}", LogLevel.Warning);
        }
    }

    private void ApplyTopMost(IntPtr hWnd, bool alwaysOnTop)
    {
        try
        {
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if (alwaysOnTop)
            {
                SetWindowLong(hWnd, GWL_EXSTYLE, exStyle | WS_EX_TOPMOST);
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            else
            {
                SetWindowLong(hWnd, GWL_EXSTYLE, exStyle & ~WS_EX_TOPMOST);
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }
        catch { }
    }

    public async Task<bool> StartMirroringAsync(DeviceItem device, StreamConfig config)
    {
        if (IsMirroring(device.Serial))
        {
            Log($"Device {device.Serial} is already mirroring.", LogLevel.Warning);
            return true;
        }

        if (!File.Exists(_engine.ScrcpyPath))
        {
            Log("scrcpy.exe not found in engine directory.", LogLevel.Error);
            return false;
        }

        // Apply Android-level settings (Awake)
        await AdbService.Instance.SetScreenAwakeAsync(device.Serial, config.StayAwake, config.NeverTimeout);

        var argsList = new List<string>
        {
            $"-s {device.Serial}",
            $"--window-title \"MIRRIVO // {device.Model}\"",
            "--print-fps" // Always print FPS stream stats so we can display real-time FPS
        };

        if (config.MaxSize > 0)
        {
            argsList.Add($"--max-size {config.MaxSize}");
        }
        else if (device.IsWireless && config.LowLatencyWifiMode)
        {
            argsList.Add("--max-size 1080");
        }

        int bitrate = config.VideoBitrateMbps;
        if (device.IsWireless && config.LowLatencyWifiMode && bitrate > 10)
        {
            bitrate = 8;
            Log("Auto-tuned bitrate to 8 Mbps for smooth wireless latency.", LogLevel.Info);
        }
        argsList.Add($"--video-bit-rate {bitrate}M");

        if (config.MaxFps > 0)
        {
            argsList.Add($"--max-fps {config.MaxFps}");
        }

        if (config.LowLatencyWifiMode || device.IsWireless)
        {
            argsList.Add("--video-buffer 0");
            argsList.Add("--audio-buffer 30");
            Log("Applied Ultra-Low Latency Wi-Fi buffers (--video-buffer 0).", LogLevel.Info);
        }

        argsList.Add("--video-codec h264");

        if (config.TurnScreenOff)
        {
            argsList.Add("--turn-screen-off");
        }

        if (config.StayAwake)
        {
            argsList.Add("--stay-awake");
        }

        // Audio Routing Configuration
        if (!config.ForwardAudio || config.AudioRoutingMode == "Mute Stream (Video Only)")
        {
            argsList.Add("--no-audio");
        }
        else if (config.AudioRoutingMode.Contains("Microphone"))
        {
            argsList.Add("--audio-source=mic");
            argsList.Add("--audio-codec=opus");
            argsList.Add("--audio-buffer=30");
        }
        else
        {
            argsList.Add("--audio-source=playback");
            argsList.Add("--audio-codec=opus");
            argsList.Add("--audio-buffer=30");
        }

        // Clipboard Auto-Sync
        if (!config.ClipboardAutoSync)
        {
            argsList.Add("--no-clipboard-autosync");
        }

        // Note: We don't pass --always-on-top CLI flag to Scrcpy directly,
        // so that our Win32 dynamic SetAlwaysOnTop can freely toggle it ON and OFF in real time!

        if (config.Fullscreen)
        {
            argsList.Add("--fullscreen");
        }

        if (config.RecordScreen && !string.IsNullOrWhiteSpace(config.RecordFilePath))
        {
            argsList.Add($"--record \"{config.RecordFilePath}\"");
        }

        string fullArgs = string.Join(" ", argsList);
        Log($"Launching Scrcpy Stream: scrcpy {fullArgs}", LogLevel.Command);

        var startInfo = new ProcessStartInfo
        {
            FileName = _engine.ScrcpyPath,
            Arguments = fullArgs,
            WorkingDirectory = _engine.EngineDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!currentPath.Contains(_engine.EngineDirectory))
        {
            startInfo.EnvironmentVariables["PATH"] = $"{_engine.EngineDirectory};{currentPath}";
        }

        try
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    // Parse live FPS if reported by scrcpy
                    var match = Regex.Match(e.Data, @"(\d+\.?\d*)\s+fps", RegexOptions.IgnoreCase);
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double fps))
                    {
                        OnFpsUpdated?.Invoke(device.Serial, fps);
                    }
                    else
                    {
                        Log($"[scrcpy] {e.Data}", LogLevel.Info);
                    }
                }
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var match = Regex.Match(e.Data, @"(\d+\.?\d*)\s+fps", RegexOptions.IgnoreCase);
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double fps))
                    {
                        OnFpsUpdated?.Invoke(device.Serial, fps);
                    }
                    else
                    {
                        LogLevel lvl = e.Data.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? LogLevel.Warning :
                                       e.Data.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ? LogLevel.Error : LogLevel.Info;
                        Log($"[scrcpy] {e.Data}", lvl);
                    }
                }
            };

            process.Exited += async (s, e) =>
            {
                if (_activeSessions.TryRemove(device.Serial, out _))
                {
                    OnMirrorStateChanged?.Invoke(device.Serial, false);
                    OnFpsUpdated?.Invoke(device.Serial, 0.0);
                    Log($"Mirror session ended for {device.Model} ({device.Serial})", LogLevel.Info);
                    await AdbService.Instance.RestoreScreenAwakeAsync(device.Serial);
                }
            };

            if (process.Start())
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                _activeSessions[device.Serial] = process;
                OnMirrorStateChanged?.Invoke(device.Serial, true);
                Log($"Mirroring live stream started for {device.Model}!", LogLevel.Success);

                // Apply initial AlwaysOnTop asynchronously once window is created
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Delay(250);
                        if (!IsMirroring(device.Serial)) break;
                        process.Refresh();
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            SetAlwaysOnTop(device.Serial, config.AlwaysOnTop);
                            break;
                        }
                    }
                });

                return true;
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to launch scrcpy: {ex.Message}", LogLevel.Error);
        }

        return false;
    }

    public void StopMirroring(string serial)
    {
        if (_activeSessions.TryRemove(serial, out var proc))
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch { }
            finally
            {
                OnMirrorStateChanged?.Invoke(serial, false);
                OnFpsUpdated?.Invoke(serial, 0.0);
                Log($"Stopped mirror session for {serial}", LogLevel.Info);
                _ = AdbService.Instance.RestoreScreenAwakeAsync(serial);
            }
        }
    }

    public void StopAll()
    {
        foreach (var serial in _activeSessions.Keys.ToList())
        {
            StopMirroring(serial);
        }
    }
}
