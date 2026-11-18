using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirrivo.Models;
using Mirrivo.Services;
using Mirrivo.Views;

namespace Mirrivo.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AdbService _adb = AdbService.Instance;
    private readonly MirrorService _mirror = MirrorService.Instance;
    private readonly PhoneStorageService _storage = PhoneStorageService.Instance;
    private readonly DispatcherTimer _pollTimer;

    [ObservableProperty]
    private ObservableCollection<DeviceItem> _devices = new();

    [ObservableProperty]
    private DeviceItem? _selectedDevice;

    [ObservableProperty]
    private ObservableCollection<LogEntry> _logs = new();

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isBusy = false;

    [ObservableProperty]
    private string _activeTab = "Mirror"; // "Mirror", "Settings", "Wireless", "Console"

    [RelayCommand]
    public void SetActiveTab(string tab)
    {
        ActiveTab = tab;
    }

    [ObservableProperty]
    private bool _isConsoleExpanded = false;

    [ObservableProperty]
    private StreamConfig _config = new();

    // Live Stream FPS (Permanently active without any on/off toggle)
    [ObservableProperty]
    private double _currentStreamFps = 0.0;

    public string FpsDisplay => CurrentStreamFps > 0 ? $"{CurrentStreamFps:F1} FPS" : "-- FPS";

    [ObservableProperty]
    private bool _alwaysOnTop = true;

    // OTA In-App Update Properties
    [ObservableProperty]
    private bool _hasUpdate = false;

    [ObservableProperty]
    private string _latestVersion = "";

    [ObservableProperty]
    private UpdateInfo? _availableUpdate = null;

    public string VersionBadgeText => HasUpdate ? $"★ v{LatestVersion} UPDATE" : $"v{UpdateService.CurrentVersionString}";
    public string VersionBadgeBackground => HasUpdate ? "#10B981" : "#171717";
    public string VersionBadgeForeground => HasUpdate ? "#000000" : "#A1A1A1";

    partial void OnAlwaysOnTopChanged(bool value)
    {
        Config.AlwaysOnTop = value;
        if (SelectedDevice != null)
        {
            _mirror.SetAlwaysOnTop(SelectedDevice.Serial, value);
        }
    }

    [ObservableProperty]
    private bool _stayAwake = true;

    partial void OnStayAwakeChanged(bool value)
    {
        Config.StayAwake = value;
        if (SelectedDevice != null)
        {
            _ = _adb.SetScreenAwakeAsync(SelectedDevice.Serial, value);
        }
    }

    [ObservableProperty]
    private bool _turnScreenOff = false;

    partial void OnTurnScreenOffChanged(bool value)
    {
        Config.TurnScreenOff = value;
        if (SelectedDevice != null && IsMirroringActive)
        {
            _ = Task.Run(async () =>
            {
                Log(value ? "Applying screen off mode to device stream..." : "Restoring phone display...", LogLevel.Info);
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    _mirror.StopMirroring(SelectedDevice.Serial);
                    await Task.Delay(250);
                    await _mirror.StartMirroringAsync(SelectedDevice, Config);
                });
            });
        }
    }

    // Upload & File Sharing Live Progress State
    [ObservableProperty]
    private bool _isUploading = false;

    [ObservableProperty]
    private string _uploadFileName = "";

    [ObservableProperty]
    private string _uploadDestination = "";

    [ObservableProperty]
    private double _uploadProgressPercent = 0.0;

    [ObservableProperty]
    private string _uploadStatusDetails = "";

    [ObservableProperty]
    private bool _isUploadCompleted = false;

    [ObservableProperty]
    private string _uploadSuccessMessage = "";

    private CancellationTokenSource? _uploadCts;

    [RelayCommand]
    public void CancelUpload()
    {
        if (_uploadCts != null && !_uploadCts.IsCancellationRequested)
        {
            _uploadCts.Cancel();
            UploadStatusDetails = "Cancelling transfer...";
            Log("File upload cancelled by user.", LogLevel.Warning);
        }
    }

    // Screen Recording State
    [ObservableProperty]
    private bool _isRecording = false;

    [ObservableProperty]
    private string _recordingTimerText = "00:00";

    [ObservableProperty]
    private string _lastRecordingPath = "";

    public string RecordButtonText => IsRecording ? $"⏹ STOP ({RecordingTimerText})" : "⏺ RECORD";
    public string RecordButtonBackground => IsRecording ? "#DC2626" : "#171717";
    public string RecordButtonForeground => IsRecording ? "#FFFFFF" : "#EDEDED";

    private DispatcherTimer? _recordingTimer;
    private DateTime _recordingStartTime;

    // Clipboard Auto-Sync
    [ObservableProperty]
    private bool _clipboardAutoSync = true;

    partial void OnClipboardAutoSyncChanged(bool value)
    {
        Config.ClipboardAutoSync = value;
        Log($"Clipboard auto-sync set to {(value ? "ENABLED" : "DISABLED")}.", LogLevel.Info);
    }

    // Audio Routing Options
    public List<string> AudioRoutingOptions { get; } = new()
    {
        "Media Audio (PC Speakers)",
        "Phone Microphone (Mic ➔ PC)",
        "Mute Stream (Video Only)"
    };

    [ObservableProperty]
    private string _selectedAudioRouting = "Media Audio (PC Speakers)";

    partial void OnSelectedAudioRoutingChanged(string value)
    {
        Config.AudioRoutingMode = value;
        Config.ForwardAudio = value != "Mute Stream (Video Only)";
        Log($"Audio routing set to: {value}", LogLevel.Info);

        if (SelectedDevice != null && IsMirroringActive)
        {
            _ = Task.Run(async () =>
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    _mirror.StopMirroring(SelectedDevice.Serial);
                    await Task.Delay(250);
                    await _mirror.StartMirroringAsync(SelectedDevice, Config);
                });
            });
        }
    }

    // Floating Mini-Bar
    private FloatingMiniBar? _floatingBar;

    [ObservableProperty]
    private bool _isFloatingBarActive = false;

    // Mirroring State & Button Bindings
    public bool HasSelectedDevice => SelectedDevice != null;
    public bool IsMirroringActive => SelectedDevice != null && _mirror.IsMirroring(SelectedDevice.Serial);
    public string MirrorButtonText => IsMirroringActive ? "⏹ STOP MIRRORING" : "▶ START MIRRORING";
    public string MirrorButtonBackground => IsMirroringActive ? "#DC2626" : "#FFFFFF"; // Red when active, White when stopped
    public string MirrorButtonForeground => IsMirroringActive ? "#FFFFFF" : "#000000";

    // Phone Storage File Explorer Mounting
    public bool IsPhoneStorageMounted => _storage.IsMounted;
    public string PhoneStorageDriveLetterDisplay => _storage.MountedDriveLetter.HasValue ? $"{_storage.MountedDriveLetter.Value}:" : "";
    public string PhoneStorageButtonText => _storage.IsMounted
        ? $"📁 Phone ({_storage.MountedDriveLetter}:)"
        : "📁 Phone Files";
    public string PhoneStorageStatusText => _storage.IsMounted
        ? $"Mounted on {_storage.MountedDriveLetter}: in File Explorer"
        : "Mount phone storage wirelessly in File Explorer";

    // Detailed Devices List for Status & Header
    public string DetailedDevicesSummary => Devices.Count > 0 
        ? string.Join("   ✦   ", Devices.Select(d => $"{d.Model} ({(d.IsWireless ? d.Serial + " [Wi-Fi]" : "USB Cable")})"))
        : "No device connected";

    // Stream Options for ComboBoxes
    public List<string> ResolutionOptions { get; } = new() 
    { 
        "1080p (FHD - Recommended)", 
        "720p (HD - Fastest Wi-Fi)", 
        "Native Resolution" 
    };
    [ObservableProperty]
    private string _selectedResolution = "1080p (FHD - Recommended)";

    public List<string> BitrateOptions { get; } = new() 
    { 
        "6 Mbps (Ultra-Low Latency)", 
        "8 Mbps (Fast Wi-Fi)", 
        "16 Mbps (High Quality)", 
        "24 Mbps (Ultra HD)" 
    };
    [ObservableProperty]
    private string _selectedBitrate = "8 Mbps (Fast Wi-Fi)";

    public List<string> FpsOptions { get; } = new() 
    { 
        "60 FPS (Smooth)", 
        "120 FPS (High Refresh)", 
        "30 FPS (Power Saver)" 
    };
    [ObservableProperty]
    private string _selectedFps = "60 FPS (Smooth)";

    public List<string> LatencyOptions { get; } = new()
    {
        "Ultra-Low Latency (Sub-30ms)",
        "Balanced (Standard)",
        "High Fidelity (USB Only)"
    };
    [ObservableProperty]
    private string _selectedLatency = "Ultra-Low Latency (Sub-30ms)";

    public MainViewModel()
    {
        _adb.OnLog += HandleLog;
        _mirror.OnLog += HandleLog;
        _mirror.OnMirrorStateChanged += HandleMirrorStateChanged;
        _mirror.OnFpsUpdated += HandleFpsUpdated;
        _storage.OnMountStateChanged += (mounted) =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                OnPropertyChanged(nameof(IsPhoneStorageMounted));
                OnPropertyChanged(nameof(PhoneStorageButtonText));
                OnPropertyChanged(nameof(PhoneStorageDriveLetterDisplay));
                OnPropertyChanged(nameof(PhoneStorageStatusText));
            });
        };

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _pollTimer.Tick += async (s, e) => await RefreshDevicesSilentAsync();
        _pollTimer.Start();

        Config.MaxSize = 1080;
        Config.VideoBitrateMbps = 8;
        Config.MaxFps = 60;
        Config.LowLatencyWifiMode = true;
        Config.StayAwake = true;
        Config.NeverTimeout = true;
        Config.TurnScreenOff = false;
        Config.AlwaysOnTop = true;

        _ = RefreshDevicesAsync();

        // Background OTA Update Check (runs 3 seconds after launch)
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            try
            {
                var info = await UpdateService.Instance.CheckForUpdateAsync();
                if (info.HasUpdate)
                {
                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        AvailableUpdate = info;
                        HasUpdate = true;
                        LatestVersion = info.LatestVersion;
                        OnPropertyChanged(nameof(VersionBadgeText));
                        OnPropertyChanged(nameof(VersionBadgeBackground));
                        OnPropertyChanged(nameof(VersionBadgeForeground));
                        Log($"OTA Update Available: v{info.LatestVersion}. Click the version pill to install.", LogLevel.Success);
                    });
                }
            }
            catch
            {
                // Silently ignore network failures in background check
            }
        });
    }

    private void Log(string message, LogLevel level = LogLevel.Info)
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            Logs.Insert(0, new LogEntry { Timestamp = DateTime.Now, Level = level, Message = message });
            if (Logs.Count > 150)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        });
    }

    private void HandleLog(LogEntry entry)
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            Logs.Insert(0, entry);
            if (Logs.Count > 150)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        });
    }

    private void HandleFpsUpdated(string serial, double fps)
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            if (SelectedDevice?.Serial == serial || string.IsNullOrEmpty(serial))
            {
                CurrentStreamFps = fps;
                OnPropertyChanged(nameof(FpsDisplay));
            }
        });
    }

    private void HandleMirrorStateChanged(string serial, bool isMirroring)
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            var dev = Devices.FirstOrDefault(d => d.Serial == serial);
            if (dev != null)
            {
                dev.Status = isMirroring ? DeviceConnectionStatus.Mirroring : DeviceConnectionStatus.Ready;
            }
            if (!isMirroring)
            {
                CurrentStreamFps = 0.0;
                OnPropertyChanged(nameof(FpsDisplay));
                if (IsRecording)
                {
                    _ = StopRecordingAsync();
                }
                if (_floatingBar != null && _floatingBar.IsLoaded)
                {
                    _floatingBar.Close();
                    _floatingBar = null;
                    IsFloatingBarActive = false;
                }
            }
            else
            {
                // Auto-open docked vertical companion bar attached to phone mirror window
                OpenFloatingBar();
            }
            NotifyMirrorButtonState();
        });
    }

    private void NotifyMirrorButtonState()
    {
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(HasSelectedDevice));
        OnPropertyChanged(nameof(DetailedDevicesSummary));
        OnPropertyChanged(nameof(IsMirroringActive));
        OnPropertyChanged(nameof(MirrorButtonText));
        OnPropertyChanged(nameof(MirrorButtonBackground));
        OnPropertyChanged(nameof(MirrorButtonForeground));
    }

    partial void OnSelectedDeviceChanged(DeviceItem? value)
    {
        NotifyMirrorButtonState();
        if (value != null && IsMirroringActive)
        {
            _mirror.SetAlwaysOnTop(value.Serial, AlwaysOnTop);
        }
    }

    [RelayCommand]
    public void SelectTargetDevice(DeviceItem? dev)
    {
        if (dev != null)
        {
            SelectedDevice = dev;
        }
    }

    partial void OnSelectedResolutionChanged(string value)
    {
        Config.MaxSize = value switch
        {
            "720p (HD - Fastest Wi-Fi)" => 720,
            "1080p (FHD - Recommended)" => 1080,
            _ => 0
        };
    }

    partial void OnSelectedBitrateChanged(string value)
    {
        Config.VideoBitrateMbps = value switch
        {
            "6 Mbps (Ultra-Low Latency)" => 6,
            "8 Mbps (Fast Wi-Fi)" => 8,
            "24 Mbps (Ultra HD)" => 24,
            _ => 16
        };
    }

    partial void OnSelectedFpsChanged(string value)
    {
        Config.MaxFps = value switch
        {
            "120 FPS (High Refresh)" => 120,
            "30 FPS (Power Saver)" => 30,
            _ => 60
        };
    }

    partial void OnSelectedLatencyChanged(string value)
    {
        Config.LatencyPreset = value;
        Config.LowLatencyWifiMode = value.StartsWith("Ultra-Low");
    }

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        IsBusy = true;
        StatusText = "Scanning devices...";
        await RefreshDevicesSilentAsync();
        IsBusy = false;
        StatusText = Devices.Count > 0 ? $"{Devices.Count} device(s) connected" : "No device connected";
    }

    private async Task RefreshDevicesSilentAsync()
    {
        try
        {
            var list = await _adb.GetDevicesAsync();

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                var currentSelectedSerial = SelectedDevice?.Serial;
                var listSerials = list.Select(d => d.Serial).ToHashSet();

                // 1. Remove disconnected devices
                for (int i = Devices.Count - 1; i >= 0; i--)
                {
                    if (!listSerials.Contains(Devices[i].Serial))
                    {
                        Devices.RemoveAt(i);
                    }
                }

                // 2. Add new or update existing devices in-place
                foreach (var d in list)
                {
                    var existing = Devices.FirstOrDefault(x => x.Serial == d.Serial);
                    bool isMirroring = _mirror.IsMirroring(d.Serial);
                    var status = isMirroring ? DeviceConnectionStatus.Mirroring :
                                 d.State == "unauthorized" ? DeviceConnectionStatus.Unauthorized :
                                 DeviceConnectionStatus.Ready;

                    if (existing != null)
                    {
                        existing.Model = d.Model;
                        existing.State = d.State;
                        existing.Product = d.Product;
                        existing.Status = status;
                    }
                    else
                    {
                        d.Status = status;
                        Devices.Add(d);
                    }
                }

                // 3. Preserve or assign SelectedDevice smoothly
                if (!string.IsNullOrEmpty(currentSelectedSerial))
                {
                    SelectedDevice = Devices.FirstOrDefault(d => d.Serial == currentSelectedSerial) ?? Devices.FirstOrDefault();
                }
                else
                {
                    SelectedDevice = Devices.FirstOrDefault();
                }

                NotifyMirrorButtonState();
            });

            if (SelectedDevice != null && SelectedDevice.Status == DeviceConnectionStatus.Ready)
            {
                int battery = await _adb.GetBatteryLevelAsync(SelectedDevice.Serial);
                if (battery > 0)
                {
                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        if (SelectedDevice != null)
                        {
                            SelectedDevice.BatteryLevel = battery;
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            HandleLog(new LogEntry { Message = $"Polling error: {ex.Message}", Level = LogLevel.Error });
        }
    }

    [RelayCommand]
    public async Task StartMirrorAsync(DeviceItem? device = null)
    {
        var target = device ?? SelectedDevice;
        if (target == null)
        {
            MessageBox.Show("Please connect and select an Android device first.", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_mirror.IsMirroring(target.Serial))
        {
            _mirror.StopMirroring(target.Serial);
            NotifyMirrorButtonState();
            StatusText = "Mirroring stopped";
            return;
        }

        if (target.Status == DeviceConnectionStatus.Unauthorized)
        {
            MessageBox.Show("Device is unauthorized. Please unlock phone and tap 'Allow USB debugging'.", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        StatusText = $"Starting stream for {target.Model}...";
        bool ok = await _mirror.StartMirroringAsync(target, Config);
        IsBusy = false;
        StatusText = ok ? "Mirroring active" : "Failed to start mirror";
        NotifyMirrorButtonState();
    }

    [RelayCommand]
    public void StopMirror(DeviceItem? device = null)
    {
        var target = device ?? SelectedDevice;
        if (target != null)
        {
            _mirror.StopMirroring(target.Serial);
            NotifyMirrorButtonState();
            StatusText = "Stream stopped";
        }
    }

    [RelayCommand]
    public async Task SwitchModeAsync(DeviceItem? device = null)
    {
        var target = device ?? SelectedDevice;
        if (target == null) return;

        if (target.IsWireless)
        {
            IsBusy = true;
            StatusText = "Switching device to USB mode...";
            var (ok, msg) = await _adb.SwitchToUsbAsync(target.Serial);
            IsBusy = false;
            MessageBox.Show(msg, "MIRRIVO // USB Mode", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshDevicesAsync();
        }
        else
        {
            IsBusy = true;
            StatusText = "Switching to Wireless Wi-Fi ADB...";
            var (ok, msg) = await _adb.SwitchToWirelessAsync(target.Serial);
            IsBusy = false;

            if (ok)
            {
                MessageBox.Show(msg, "MIRRIVO // Wireless Ready", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshDevicesAsync();
            }
            else
            {
                MessageBox.Show(msg, "MIRRIVO // Wireless Switch Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    [RelayCommand]
    public async Task DisconnectOrResetAsync(DeviceItem? device = null)
    {
        var target = device ?? SelectedDevice;
        if (target == null) return;

        if (target.IsWireless)
        {
            await _adb.DisconnectAsync(target.Serial);
            await RefreshDevicesAsync();
        }
        else
        {
            await _adb.ReconnectUsbAsync();
            await RefreshDevicesAsync();
            StatusText = "USB link refreshed";
        }
    }

    // ==============================================
    // WIRELESS INLINE PAIRING STATE & COMMANDS
    // ==============================================
    [ObservableProperty]
    private string _wirelessIp = "192.168.1.";

    [ObservableProperty]
    private string _wirelessPort = "5555";

    [ObservableProperty]
    private string _wirelessPairCode = "";

    [ObservableProperty]
    private bool _isWirelessPairingMode = false;

    [ObservableProperty]
    private bool _isConnectingWireless = false;

    [ObservableProperty]
    private string _wirelessStatusMessage = "";

    [RelayCommand]
    public void SetWirelessMode(string mode)
    {
        IsWirelessPairingMode = mode.Equals("Pair", StringComparison.OrdinalIgnoreCase);
        WirelessStatusMessage = "";
        if (!IsWirelessPairingMode && (string.IsNullOrWhiteSpace(WirelessPort) || WirelessPort == ""))
        {
            WirelessPort = "5555";
        }
    }

    [RelayCommand]
    public async Task ConnectWirelessDirectAsync()
    {
        string ip = WirelessIp.Trim();
        if (string.IsNullOrWhiteSpace(ip) || !int.TryParse(WirelessPort.Trim(), out int port))
        {
            WirelessStatusMessage = "Please enter a valid IP address and Port.";
            return;
        }

        IsConnectingWireless = true;
        WirelessStatusMessage = $"Connecting to {ip}:{port}...";
        try
        {
            if (IsWirelessPairingMode)
            {
                string code = WirelessPairCode.Trim();
                if (string.IsNullOrWhiteSpace(code) || code.Length < 6)
                {
                    WirelessStatusMessage = "Please enter 6-digit pairing code.";
                    return;
                }
                var (pairOk, pairMsg) = await _adb.PairWirelessAsync(ip, port, code);
                if (!pairOk)
                {
                    WirelessStatusMessage = $"Pairing failed: {pairMsg}";
                    return;
                }
                // Once paired, connect
                await _adb.ConnectWirelessAsync(ip, 5555);
            }
            else
            {
                var (connOk, connMsg) = await _adb.ConnectWirelessAsync(ip, port);
                if (!connOk)
                {
                    WirelessStatusMessage = $"Connection failed: {connMsg}";
                    return;
                }
            }

            WirelessStatusMessage = "✓ Connected successfully!";
            await RefreshDevicesAsync();
            ActiveTab = "Mirror";
        }
        catch (Exception ex)
        {
            WirelessStatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsConnectingWireless = false;
        }
    }

    [RelayCommand]
    public void OpenWirelessDialog()
    {
        var dlg = new WirelessConnectDialog
        {
            Owner = Application.Current.MainWindow
        };
        if (dlg.ShowDialog() == true)
        {
            _ = RefreshDevicesAsync();
        }
    }

    [RelayCommand]
    public async Task OpenUsbConnectAsync()
    {
        await _adb.ReconnectUsbAsync();
        await RefreshDevicesAsync();

        var usbDev = Devices.FirstOrDefault(d => !d.IsWireless);
        if (usbDev != null)
        {
            SelectedDevice = usbDev;
            MessageBox.Show($"USB device detected: {usbDev.Model} ({usbDev.Serial})\nReady for mirroring!", "MIRRIVO // USB Connected", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("USB Connection Instructions:\n\n1. Connect phone to PC using a USB data cable.\n2. On your phone: Settings > Developer Options > enable 'USB Debugging'.\n3. Tap 'Allow / Always allow from this computer' on your phone.\n\nThen click Refresh!", "MIRRIVO // Connect via USB", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    public async Task SendKeyAsync(string keyName)
    {
        if (SelectedDevice == null) return;

        int code = keyName switch
        {
            "Home" => 3,
            "Back" => 4,
            "AppSwitch" => 187,
            "Power" => 26,
            "VolUp" => 24,
            "VolDown" => 25,
            _ => 0
        };

        if (code > 0)
        {
            await _adb.SendKeyEventAsync(SelectedDevice.Serial, code);
        }
    }

    [RelayCommand]
    public async Task TakeScreenshotAsync()
    {
        if (SelectedDevice == null)
        {
            MessageBox.Show("Select a device first.", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusText = "Capturing screenshot...";
        var (ok, path) = await _adb.TakeScreenshotAsync(SelectedDevice.Serial);
        if (ok)
        {
            StatusText = "Screenshot saved!";
            MessageBox.Show($"Screenshot saved successfully:\n{path}", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            StatusText = "Screenshot failed";
            MessageBox.Show($"Screenshot failed: {path}", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public async Task ToggleRecordingAsync()
    {
        if (SelectedDevice == null)
        {
            MessageBox.Show("Please connect and select an Android phone first.", "MIRRIVO // Recording", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsRecording)
        {
            await StopRecordingAsync();
        }
        else
        {
            await StartRecordingAsync();
        }
    }

    public async Task StartRecordingAsync()
    {
        if (SelectedDevice == null) return;

        string videosFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MIRRIVO");
        Directory.CreateDirectory(videosFolder);

        string fileName = $"MIRRIVO_Recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
        string filePath = Path.Combine(videosFolder, fileName);
        LastRecordingPath = filePath;

        Config.RecordScreen = true;
        Config.RecordFilePath = filePath;

        IsRecording = true;
        _recordingStartTime = DateTime.Now;
        RecordingTimerText = "00:00";

        _recordingTimer?.Stop();
        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordingTimer.Tick += (s, e) =>
        {
            var elapsed = DateTime.Now - _recordingStartTime;
            RecordingTimerText = $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            OnPropertyChanged(nameof(RecordButtonText));
        };
        _recordingTimer.Start();

        OnPropertyChanged(nameof(RecordButtonText));
        OnPropertyChanged(nameof(RecordButtonBackground));
        OnPropertyChanged(nameof(RecordButtonForeground));

        Log($"[Recording] Started screen & audio recording: {fileName}", LogLevel.Success);
        StatusText = $"Recording active: {fileName}";

        // If stream is already active, restart seamlessly with record argument
        if (IsMirroringActive)
        {
            _mirror.StopMirroring(SelectedDevice.Serial);
            await Task.Delay(250);
            await _mirror.StartMirroringAsync(SelectedDevice, Config);
        }
        else
        {
            await StartMirrorAsync(SelectedDevice);
        }
    }

    public async Task StopRecordingAsync()
    {
        if (!IsRecording) return;

        _recordingTimer?.Stop();
        _recordingTimer = null;
        IsRecording = false;
        Config.RecordScreen = false;

        OnPropertyChanged(nameof(RecordButtonText));
        OnPropertyChanged(nameof(RecordButtonBackground));
        OnPropertyChanged(nameof(RecordButtonForeground));

        Log($"[Recording] Saved recording to: {LastRecordingPath}", LogLevel.Success);
        StatusText = $"Recording saved: {Path.GetFileName(LastRecordingPath)}";

        // Restart mirror session cleanly without record flag if still active
        if (SelectedDevice != null && IsMirroringActive)
        {
            _mirror.StopMirroring(SelectedDevice.Serial);
            await Task.Delay(250);
            await _mirror.StartMirroringAsync(SelectedDevice, Config);
        }

        var res = MessageBox.Show($"Recording saved successfully!\n\nPath: {LastRecordingPath}\n\nWould you like to open the recording folder?", "MIRRIVO // Recording Saved", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (res == MessageBoxResult.Yes)
        {
            OpenRecordingsFolder();
        }
    }

    [RelayCommand]
    public void OpenRecordingsFolder()
    {
        try
        {
            string videosFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MIRRIVO");
            Directory.CreateDirectory(videosFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{videosFolder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"Failed to open recordings folder: {ex.Message}", LogLevel.Warning);
        }
    }

    [RelayCommand]
    public async Task SyncClipboardAsync()
    {
        if (SelectedDevice == null)
        {
            MessageBox.Show("Please connect a device first.", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            string pcText = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (!string.IsNullOrEmpty(pcText))
            {
                bool ok = await _adb.SetDeviceClipboardAsync(SelectedDevice.Serial, pcText);
                if (ok)
                {
                    StatusText = "Pushed PC clipboard to phone!";
                    string preview = pcText.Length > 30 ? pcText.Substring(0, 30) + "..." : pcText;
                    Log($"[Clipboard] Sent PC text to phone: \"{preview}\"", LogLevel.Success);
                }
            }
            else
            {
                var (ok, phoneText) = await _adb.GetDeviceClipboardAsync(SelectedDevice.Serial);
                if (ok && !string.IsNullOrEmpty(phoneText))
                {
                    Clipboard.SetText(phoneText);
                    StatusText = "Received phone clipboard on PC!";
                    string preview = phoneText.Length > 30 ? phoneText.Substring(0, 30) + "..." : phoneText;
                    Log($"[Clipboard] Received phone text to PC: \"{preview}\"", LogLevel.Success);
                }
                else
                {
                    StatusText = "Clipboard is empty on both PC and phone.";
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Clipboard sync error: {ex.Message}", LogLevel.Warning);
        }
    }

    [RelayCommand]
    public void TogglePhoneDisplay()
    {
        TurnScreenOff = !TurnScreenOff;
    }

    public void OpenFloatingBar()
    {
        if (_floatingBar == null || !_floatingBar.IsLoaded)
        {
            _floatingBar = new FloatingMiniBar
            {
                DataContext = this,
                Owner = Application.Current?.MainWindow
            };
            _floatingBar.Closed += (s, e) =>
            {
                _floatingBar = null;
                IsFloatingBarActive = false;
            };
            _floatingBar.Show();
            IsFloatingBarActive = true;
            Log("Attached Quick Control Bar docked to phone mirror.", LogLevel.Success);
        }
    }

    [RelayCommand]
    public void ToggleFloatingBar()
    {
        if (_floatingBar != null && _floatingBar.IsLoaded)
        {
            _floatingBar.Close();
            _floatingBar = null;
            IsFloatingBarActive = false;
            Log("Quick Control Bar closed.", LogLevel.Info);
        }
        else
        {
            OpenFloatingBar();
        }
    }

    [RelayCommand]
    public async Task TogglePhoneStorageAsync()
    {
        if (SelectedDevice == null)
        {
            MessageBox.Show("Please connect and select an Android device first.", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_storage.IsMounted)
        {
            _storage.OpenInExplorer();
        }
        else
        {
            IsBusy = true;
            StatusText = $"Mounting {SelectedDevice.Model} in Windows File Explorer...";
            bool ok = await _storage.MountDriveAsync(SelectedDevice.Serial, SelectedDevice.Model);
            IsBusy = false;
            StatusText = ok
                ? $"Mounted phone storage on {_storage.MountedDriveLetter}: in File Explorer"
                : "Failed to mount phone storage. Ensure device is connected.";
        }
    }

    [RelayCommand]
    public void UnmountPhoneStorage()
    {
        _storage.UnmountDrive();
        StatusText = "Phone storage unmounted from Windows";
    }

    [RelayCommand]
    public void ToggleConsole()
    {
        IsConsoleExpanded = !IsConsoleExpanded;
    }

    [RelayCommand]
    public void ClearLogs()
    {
        Logs.Clear();
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        StatusText = "Checking for updates...";
        Log("Querying GitHub for latest MIRRIVO release...", LogLevel.Info);

        var info = await UpdateService.Instance.CheckForUpdateAsync();
        AvailableUpdate = info;
        HasUpdate = info.HasUpdate;
        LatestVersion = info.LatestVersion;
        OnPropertyChanged(nameof(VersionBadgeText));
        OnPropertyChanged(nameof(VersionBadgeBackground));
        OnPropertyChanged(nameof(VersionBadgeForeground));

        StatusText = info.HasUpdate ? $"Update available: v{info.LatestVersion}" : "MIRRIVO is up to date";

        var dialog = new UpdateDialog(info)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
    }

    public async Task HandleDropFilesAsync(string[] files)
    {
        if (SelectedDevice == null)
        {
            MessageBox.Show("Please connect and select an active phone first.", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (files == null || files.Length == 0) return;

        _uploadCts = new CancellationTokenSource();
        IsUploading = true;
        IsUploadCompleted = false;

        try
        {
            foreach (var file in files)
            {
                if (_uploadCts.Token.IsCancellationRequested) break;

                UploadFileName = Path.GetFileName(file);
                UploadProgressPercent = 0;
                UploadStatusDetails = "Starting transfer...";
                bool isMedia = AdbService.IsMediaFile(file);
                UploadDestination = isMedia ? "DCIM/Mirrivo Shared" : "Downloads";

                var progress = new Progress<(double Percent, long TransferredBytes, long TotalBytes, string Speed)>(p =>
                {
                    UploadProgressPercent = p.Percent;
                    double curMb = p.TransferredBytes / (1024.0 * 1024.0);
                    double totalMb = p.TotalBytes / (1024.0 * 1024.0);
                    UploadStatusDetails = $"{p.Percent:F0}% ({curMb:F1} MB / {totalMb:F1} MB) • {p.Speed}";
                    StatusText = $"Sharing {UploadFileName} ➔ {UploadDestination}: {p.Percent:F0}%";
                });

                var (ok, msg, dest) = await _adb.SmartPushFileAsync(SelectedDevice.Serial, file, progress, _uploadCts.Token);
                if (ok)
                {
                    UploadSuccessMessage = $"✓ Saved to {dest}";
                    Log($"Shared '{UploadFileName}' successfully to {dest}", LogLevel.Success);
                }
                else
                {
                    UploadSuccessMessage = _uploadCts.Token.IsCancellationRequested ? "✕ Upload cancelled" : $"✕ Failed: {msg}";
                    Log(_uploadCts.Token.IsCancellationRequested ? "File transfer cancelled." : $"Failed to share '{UploadFileName}': {msg}", LogLevel.Warning);
                    if (_uploadCts.Token.IsCancellationRequested) break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            UploadSuccessMessage = "✕ Upload cancelled";
            Log("File transfer cancelled by user.", LogLevel.Warning);
        }
        finally
        {
            IsUploadCompleted = true;
            StatusText = UploadSuccessMessage;
            _uploadCts?.Dispose();
            _uploadCts = null;

            // Auto-dismiss upload state after 3 seconds
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    IsUploading = false;
                    IsUploadCompleted = false;
                    UploadProgressPercent = 0;
                    StatusText = "Ready";
                });
            });
        }
    }
}
