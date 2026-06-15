using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using GearTray.Contracts;

namespace GearTrayUI;

public class DeviceCacheInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DeviceType Type { get; set; } = DeviceType.Generic;
}

public class AppConfig
{
    public bool ShowOfflineDevices { get; set; } = true;
    public bool AutoSwitchHeadphones { get; set; } = false;
    public string PreviousDefaultDeviceId { get; set; } = string.Empty;
    public bool AutoSwitchMicrophone { get; set; } = false;
    public string PreviousDefaultCaptureDeviceId { get; set; } = string.Empty;
    public List<DeviceCacheInfo> CachedDevices { get; set; } = [];
    public int GlobalBatteryThreshold { get; set; } = 15;
    public bool EnableNotifications { get; set; } = true;
    public bool BlinkTrayIcon { get; set; } = true;
    public bool PauseNotificationOnFullScreen { get; set; } = true;
    public Dictionary<string, string> DeviceCustomNames { get; set; } = [];
    public Dictionary<string, bool> DeviceUseDefaultThreshold { get; set; } = [];
    public Dictionary<string, int> DeviceBatteryThresholds { get; set; } = [];
}

public class PluginCoordinator(IEnumerable<IDevicePlugin> plugins)
{
    private readonly IEnumerable<IDevicePlugin> _plugins = plugins;
    private AppConfig _config = new();
    private readonly Dictionary<string, DeviceStatusEventArgs> _allDevices = [];
    private readonly Dictionary<string, string> _deviceLastUpdated = [];
    private readonly HashSet<string> _alertedDevices = [];

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public event Action<string, string>? OnRaiseNotification;
    public event Action? DeviceListChanged;
    
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static bool IsFullScreenAppActive()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            
            if (GetWindowRect(hwnd, out RECT rect))
            {
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                
                int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
                int screenHeight = (int)SystemParameters.PrimaryScreenHeight;
                
                return width >= screenWidth && height >= screenHeight;
            }
        }
        catch
        {
            // Fallback
        }
        return false;
    }
    
    // Background discovery timer fields
    private System.Threading.Timer? _discoveryTimer;
    private int _discoveryAttempts;
    private readonly object _timerSync = new();
    private bool _isTimerRunning;

    // UI-bindable collection of all active device statuses across all plugins
    public ObservableCollection<DeviceStatusEventArgs> ActiveDevices { get; } = [];

    public bool ShowOfflineDevices
    {
        get => _config.ShowOfflineDevices;
        set
        {
            if (_config.ShowOfflineDevices != value)
            {
                _config.ShowOfflineDevices = value;
                SaveConfig(_config);
                RefreshActiveDevicesList();
            }
        }
    }

    public bool AutoSwitchHeadphones
    {
        get => _config.AutoSwitchHeadphones;
        set
        {
            if (_config.AutoSwitchHeadphones != value)
            {
                _config.AutoSwitchHeadphones = value;
                SaveConfig(_config);
            }
        }
    }

    public bool AutoSwitchMicrophone
    {
        get => _config.AutoSwitchMicrophone;
        set
        {
            if (_config.AutoSwitchMicrophone != value)
            {
                _config.AutoSwitchMicrophone = value;
                SaveConfig(_config);
            }
        }
    }

    public bool EnableNotifications
    {
        get => _config.EnableNotifications;
        set
        {
            if (_config.EnableNotifications != value)
            {
                _config.EnableNotifications = value;
                SaveConfig(_config);
            }
        }
    }

    public bool BlinkTrayIcon
    {
        get => _config.BlinkTrayIcon;
        set
        {
            if (_config.BlinkTrayIcon != value)
            {
                _config.BlinkTrayIcon = value;
                SaveConfig(_config);
            }
        }
    }

    public bool PauseNotificationOnFullScreen
    {
        get => _config.PauseNotificationOnFullScreen;
        set
        {
            if (_config.PauseNotificationOnFullScreen != value)
            {
                _config.PauseNotificationOnFullScreen = value;
                SaveConfig(_config);
            }
        }
    }

    public int GlobalBatteryThreshold
    {
        get => _config.GlobalBatteryThreshold;
        set
        {
            if (_config.GlobalBatteryThreshold != value)
            {
                _config.GlobalBatteryThreshold = value;
                SaveConfig(_config);
            }
        }
    }

    public bool GetDeviceUseDefaultThreshold(string deviceId)
    {
        if (_config.DeviceUseDefaultThreshold.TryGetValue(deviceId, out bool useDefault))
        {
            return useDefault;
        }
        return true;
    }

    public void SetDeviceUseDefaultThreshold(string deviceId, bool useDefault)
    {
        _config.DeviceUseDefaultThreshold[deviceId] = useDefault;
        SaveConfig(_config);
    }

    public string GetDeviceCustomName(string deviceId)
    {
        if (_config.DeviceCustomNames.TryGetValue(deviceId, out string? customName))
        {
            return customName ?? string.Empty;
        }
        return string.Empty;
    }

    public void SetDeviceCustomName(string deviceId, string customName)
    {
        _config.DeviceCustomNames[deviceId] = customName;
        SaveConfig(_config);
    }

    public string GetDeviceLastUpdated(string deviceId)
    {
        if (_deviceLastUpdated.TryGetValue(deviceId, out string? lastUpdated))
        {
            return lastUpdated;
        }
        return "Unknown";
    }

    public int GetDeviceThreshold(string deviceId)
    {
        if (!GetDeviceUseDefaultThreshold(deviceId) && _config.DeviceBatteryThresholds.TryGetValue(deviceId, out int threshold))
        {
            return threshold;
        }
        return _config.GlobalBatteryThreshold;
    }

    public void SetDeviceThreshold(string deviceId, int val)
    {
        _config.DeviceBatteryThresholds[deviceId] = val;
        SaveConfig(_config);
    }

    public static string? ConfigPathOverride { get; set; }

    private static string GetConfigPath()
    {
        if (!string.IsNullOrEmpty(ConfigPathOverride))
        {
            string? dir = Path.GetDirectoryName(ConfigPathOverride);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return ConfigPathOverride;
        }
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string folder = Path.Combine(appData, "GearTray");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "config.json");
    }

    private static AppConfig LoadConfig()
    {
        try
        {
            string path = GetConfigPath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<AppConfig>(json, _serializerOptions) ?? new AppConfig();
                // Deduplicate CachedDevices by DisplayName on load
                config.CachedDevices = [..config.CachedDevices
                    .GroupBy(x => x.DisplayName)
                    .Select(g => g.First())];
                return config;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load config: {ex.Message}");
        }
        return new AppConfig();
    }

    private static void SaveConfig(AppConfig config)
    {
        try
        {
            string path = GetConfigPath();
            string json = JsonSerializer.Serialize(config, _serializerOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save config: {ex.Message}");
        }
    }

    public void Initialize()
    {
        ActiveDevices.Clear();
        _allDevices.Clear();

        // 1. Load configuration and populate cached devices as offline initially
        _config = LoadConfig();
        foreach (var cache in _config.CachedDevices)
        {
            var dev = new DeviceStatusEventArgs(
                deviceId: cache.DeviceId,
                displayName: cache.DisplayName,
                type: cache.Type,
                batteryPercentage: -1,
                power: cache.Type == DeviceType.Headset ? PowerStatus.PoweredOff : PowerStatus.Unknown,
                isOnline: false
            );
            _allDevices[cache.DeviceId] = dev;
        }
        RefreshActiveDevicesList();

        // 2. Initialize plugins
        foreach (var plugin in _plugins)
        {
            plugin.DeviceStatusChanged += OnDeviceStatusChanged;
            plugin.Initialize();

            // Populate initial active devices
            foreach (var device in plugin.GetActiveDevices())
            {
                UpdateDeviceStatus(device);
            }
        }

        // 3. Start discovery timer to scan for offline expected devices
        StartDiscoveryTimer();
    }

    public void Shutdown()
    {
        StopDiscoveryTimer();

        foreach (var plugin in _plugins)
        {
            plugin.DeviceStatusChanged -= OnDeviceStatusChanged;
            plugin.Shutdown();
        }
    }

    private void OnDeviceStatusChanged(object? sender, DeviceStatusEventArgs e)
    {
        // Marshall to UI thread if updating an ObservableCollection
        if (Application.Current != null)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                UpdateDeviceStatus(e);
            });
        }
        else
        {
            UpdateDeviceStatus(e);
        }
    }

    private void UpdateDeviceStatus(DeviceStatusEventArgs status)
    {
        var wasOnline = _allDevices.TryGetValue(status.DeviceId, out var existing) && existing.IsOnline;

        // If the device is offline, set its Power status appropriately (PoweredOff only for headsets)
        var updatedStatus = status;
        if (!status.IsOnline)
        {
            updatedStatus = new DeviceStatusEventArgs(
                status.DeviceId,
                status.DisplayName,
                status.Type,
                status.BatteryPercentage,
                status.Type == DeviceType.Headset ? PowerStatus.PoweredOff : PowerStatus.Unknown,
                status.IsOnline,
                status.Controls,
                isDefault: status.IsDefault
            );
        }

        _deviceLastUpdated[status.DeviceId] = DateTime.Now.ToString("dd/MM/yyyy HH:mm");

        _allDevices[status.DeviceId] = updatedStatus;
        
        if (existing == null || existing.IsOnline != status.IsOnline)
        {
            GearTray.Contracts.EventLogger.Log(status.IsOnline ? "POWER_ON" : "POWER_OFF", $"Device status: {status.DisplayName} (Online={status.IsOnline}, Power={updatedStatus.Power})", status.IsOnline ? "#2E7D32" : "#C62828");
        }
        else if (existing.Power != updatedStatus.Power || existing.BatteryPercentage != updatedStatus.BatteryPercentage)
        {
            GearTray.Contracts.EventLogger.Log("SYSTEM", $"Device status updated: {status.DisplayName} (Online={status.IsOnline}, Power={updatedStatus.Power}, Battery={updatedStatus.BatteryPercentage}%)", "#888888");
        }

        // If the device is online, ensure it is saved in the cache
        if (status.IsOnline)
        {
            var existingCache = _config.CachedDevices.FirstOrDefault(x => x.DisplayName == status.DisplayName);
            if (existingCache != null)
            {
                bool modified = false;
                if (existingCache.DeviceId != status.DeviceId)
                {
                    existingCache.DeviceId = status.DeviceId;
                    modified = true;
                }
                if (existingCache.Type != status.Type)
                {
                    existingCache.Type = status.Type;
                    modified = true;
                }
                if (modified)
                {
                    SaveConfig(_config);
                    GearTray.Contracts.EventLogger.Log("SYSTEM", $"Cache updated: {status.DisplayName} with ID {status.DeviceId} (Type: {status.Type})", "#888888");
                }
            }
            else
            {
                _config.CachedDevices.Add(new DeviceCacheInfo
                {
                    DeviceId = status.DeviceId,
                    DisplayName = status.DisplayName,
                    Type = status.Type
                });
                SaveConfig(_config);
                GearTray.Contracts.EventLogger.Log("SYSTEM", $"Added to cache: {status.DisplayName}", "#888888");
            }
            // Check low battery threshold
            if (status.BatteryPercentage >= 0)
            {
                int threshold = GetDeviceThreshold(status.DeviceId);
                if (status.BatteryPercentage <= threshold)
                {
                    if (!_alertedDevices.Contains(status.DeviceId))
                    {
                        _alertedDevices.Add(status.DeviceId);
                        bool isFullScreen = IsFullScreenAppActive();
                        if (_config.EnableNotifications && (!isFullScreen || !_config.PauseNotificationOnFullScreen))
                        {
                            OnRaiseNotification?.Invoke("Low Battery Alert", $"{status.DisplayName} is at {status.BatteryPercentage}%.");
                        }
                        GearTray.Contracts.EventLogger.Log("ALERT", $"Low Battery Alert triggered for {status.DisplayName} ({status.BatteryPercentage}%) [NotificationsEnabled={_config.EnableNotifications}, FullScreen={isFullScreen}, PauseOnFullScreen={_config.PauseNotificationOnFullScreen}]", "#EF6C00");
                    }
                }
                else if (status.BatteryPercentage > threshold + 5)
                {
                    _alertedDevices.Remove(status.DeviceId);
                }
            }

            // Auto-switch default audio output to headphones if enabled
            if (_config.AutoSwitchHeadphones && status.Type == DeviceType.Headset && !wasOnline)
            {
                // Record the previous online default audio device ID
                var currentDefault = _allDevices.Values.FirstOrDefault(d => d.IsOnline && d.DeviceId != status.DeviceId && d.DeviceId.StartsWith("audio_dev_"));
                if (currentDefault != null)
                {
                    _config.PreviousDefaultDeviceId = currentDefault.DeviceId;
                    SaveConfig(_config);
                    GearTray.Contracts.EventLogger.Log("AUDIO_SWITCH", $"AutoSwitch: Stored previous default audio device: {currentDefault.DisplayName}", "#8A2BE2");
                }

                // Switch to headset
                var activateControl = status.Controls.FirstOrDefault(c => c.ControlType == "Action" && c.DisplayName == "Activate");
                if (activateControl != null)
                {
                    GearTray.Contracts.EventLogger.Log("AUDIO_SWITCH", $"AutoSwitch: Automatically selected headphones '{status.DisplayName}' as default playback device", "#8A2BE2");
                    activateControl.OnControlChanged?.Invoke(1.0);
                }
            }

            // Auto-switch default capture input to headset microphone if enabled
            if (_config.AutoSwitchMicrophone && status.Type == DeviceType.Headset && !wasOnline)
            {
                var audioPlugin = _plugins.OfType<GearTray.Plugins.Audio.AudioPlugin>().FirstOrDefault();
                if (audioPlugin != null)
                {
                    string? currentMic = audioPlugin.GetDefaultCaptureDeviceId();
                    
                    // Extract core model name from display name (e.g. "Arctis Nova 7X")
                    string cleanModel = status.DisplayName
                        .Replace("Headphones", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("Headset", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("Wireless", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("(", "").Replace(")", "").Trim();

                    // Check if Windows has already switched to the headset mic by default
                    bool isHeadsetMic = false;
                    string? headsetMicId = audioPlugin.FindCaptureDeviceIdByName(cleanModel);
                    if (currentMic != null && headsetMicId != null && string.Equals(currentMic, headsetMicId, StringComparison.OrdinalIgnoreCase))
                    {
                        isHeadsetMic = true;
                    }

                    if (currentMic == null || isHeadsetMic)
                    {
                        // Search for another online microphone in our cache to use as the previous default (e.g. Razer Seiren)
                        var otherMic = _allDevices.Values.FirstOrDefault(d => 
                            d.IsOnline && 
                            d.Type == DeviceType.Microphone && 
                            !d.DisplayName.Contains("Arctis", StringComparison.OrdinalIgnoreCase) &&
                            !d.DisplayName.Contains("Nova", StringComparison.OrdinalIgnoreCase) &&
                            !d.DisplayName.Contains("SteelSeries", StringComparison.OrdinalIgnoreCase));
                        
                        if (otherMic != null)
                        {
                            currentMic = otherMic.DeviceId;
                        }
                        else
                        {
                            // Fallback to cached device list
                            var cachedMic = _config.CachedDevices.FirstOrDefault(c =>
                                c.Type == DeviceType.Microphone &&
                                !c.DisplayName.Contains("Arctis", StringComparison.OrdinalIgnoreCase) &&
                                !c.DisplayName.Contains("Nova", StringComparison.OrdinalIgnoreCase) &&
                                !c.DisplayName.Contains("SteelSeries", StringComparison.OrdinalIgnoreCase));

                            if (cachedMic != null)
                            {
                                currentMic = cachedMic.DeviceId;
                            }
                        }

                        if (currentMic != null && currentMic.StartsWith("audio_dev_"))
                        {
                            currentMic = currentMic["audio_dev_".Length..];
                        }
                    }

                    // Check if currentMic is still the headset mic
                    bool isStillHeadsetMic = false;
                    if (currentMic != null && headsetMicId != null && string.Equals(currentMic, headsetMicId, StringComparison.OrdinalIgnoreCase))
                    {
                        isStillHeadsetMic = true;
                    }

                    if (currentMic != null && !isStillHeadsetMic)
                    {
                        _config.PreviousDefaultCaptureDeviceId = currentMic;
                        SaveConfig(_config);
                        GearTray.Contracts.EventLogger.Log("AUDIO_SWITCH", $"AutoSwitch: Stored previous default recording device: {currentMic}", "#8A2BE2");
                    }

                    if (headsetMicId != null)
                    {
                        GearTray.Contracts.EventLogger.Log("AUDIO_SWITCH", $"AutoSwitch: Automatically selected microphone for '{status.DisplayName}' as default recording device", "#8A2BE2");
                        audioPlugin.SetDefaultCaptureDevice(headsetMicId);
                    }
                }
            }
        }
        else
        {
            // Auto-restore previous audio output when headphones go offline
            if (_config.AutoSwitchHeadphones && status.Type == DeviceType.Headset && wasOnline)
            {
                GearTray.Contracts.EventLogger.Log("AUDIO_SWITCH", $"AutoSwitch: Headset '{status.DisplayName}' went offline", "#8A2BE2");
                if (!string.IsNullOrEmpty(_config.PreviousDefaultDeviceId))
                {
                    if (_allDevices.TryGetValue(_config.PreviousDefaultDeviceId, out var prevDev) && prevDev.IsOnline)
                    {
                        var activateControl = prevDev.Controls.FirstOrDefault(c => c.ControlType == "Action" && c.DisplayName == "Activate");
                        if (activateControl != null)
                        {
                            GearTray.Contracts.EventLogger.Log("AUDIO_SWITCH", $"AutoSwitch: Restoring previous audio device '{prevDev.DisplayName}'", "#8A2BE2");
                            activateControl.OnControlChanged?.Invoke(1.0);
                        }
                    }
                }
            }

            // Auto-restore previous capture input when headphones go offline
            if (_config.AutoSwitchMicrophone && status.Type == DeviceType.Headset && wasOnline)
            {
                var audioPlugin = _plugins.OfType<GearTray.Plugins.Audio.AudioPlugin>().FirstOrDefault();
                if (audioPlugin != null)
                {
                    string? targetMicId = _config.PreviousDefaultCaptureDeviceId;
                    
                    // Check if targetMicId is empty or is the headset mic itself
                    string cleanModel = status.DisplayName
                        .Replace("Headphones", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("Headset", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("Wireless", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("(", "").Replace(")", "").Trim();
                    string? headsetMicId = audioPlugin.FindCaptureDeviceIdByName(cleanModel);
                    
                    bool isTargetHeadset = false;
                    if (!string.IsNullOrEmpty(targetMicId) && headsetMicId != null && string.Equals(targetMicId, headsetMicId, StringComparison.OrdinalIgnoreCase))
                    {
                        isTargetHeadset = true;
                    }
                    
                    if (string.IsNullOrEmpty(targetMicId) || isTargetHeadset)
                    {
                        // Search for another online microphone in our cache to use as the previous default (e.g. Razer Seiren)
                        var otherMic = _allDevices.Values.FirstOrDefault(d => 
                            d.IsOnline && 
                            d.Type == DeviceType.Microphone && 
                            !d.DisplayName.Contains("Arctis", StringComparison.OrdinalIgnoreCase) &&
                            !d.DisplayName.Contains("Nova", StringComparison.OrdinalIgnoreCase) &&
                            !d.DisplayName.Contains("SteelSeries", StringComparison.OrdinalIgnoreCase));
                        
                        if (otherMic != null)
                        {
                            targetMicId = otherMic.DeviceId;
                        }
                        else
                        {
                            // Fallback to cached device list
                            var cachedMic = _config.CachedDevices.FirstOrDefault(c =>
                                c.Type == DeviceType.Microphone &&
                                !c.DisplayName.Contains("Arctis", StringComparison.OrdinalIgnoreCase) &&
                                !c.DisplayName.Contains("Nova", StringComparison.OrdinalIgnoreCase) &&
                                !c.DisplayName.Contains("SteelSeries", StringComparison.OrdinalIgnoreCase));

                            if (cachedMic != null)
                            {
                                targetMicId = cachedMic.DeviceId;
                            }
                        }

                        if (targetMicId != null && targetMicId.StartsWith("audio_dev_"))
                        {
                            targetMicId = targetMicId["audio_dev_".Length..];
                        }
                    }

                    if (!string.IsNullOrEmpty(targetMicId))
                    {
                        GearTray.Contracts.EventLogger.Log("AUDIO_SWITCH", $"AutoSwitch: Headset went offline, restoring previous recording device: {targetMicId}", "#8A2BE2");
                        audioPlugin.SetDefaultCaptureDevice(targetMicId);
                    }
                }
            }
        }

        RefreshActiveDevicesList();

        // Auto-resume discovery timer if a cached expected input device goes offline
        bool isInputDevice = status.Type == DeviceType.Keyboard || status.Type == DeviceType.Mouse || status.Type == DeviceType.Controller;
        if (!status.IsOnline && isInputDevice && _config.CachedDevices.Any(c => c.DeviceId == status.DeviceId))
        {
            StartDiscoveryTimer();
        }
    }

    private void RefreshActiveDevicesList()
    {
        var listToDisplay = _allDevices.Values
            .Where(d => d.IsOnline || _config.ShowOfflineDevices)
            .GroupBy(d => d.DisplayName)
            .Select(g => g.OrderByDescending(d => d.IsOnline).First())
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.DisplayName)
            .ToList();

        // Adjust ActiveDevices to match the exact order of listToDisplay
        for (int i = 0; i < listToDisplay.Count; i++)
        {
            var targetDev = listToDisplay[i];
            var existing = ActiveDevices.FirstOrDefault(x => x.DeviceId == targetDev.DeviceId);
            
            if (existing != null)
            {
                int existingIdx = ActiveDevices.IndexOf(existing);
                bool structuralChanged = existing.IsOnline != targetDev.IsOnline || 
                                         existing.Type != targetDev.Type ||
                                         existing.BatteryPercentage != targetDev.BatteryPercentage || 
                                         existing.Power != targetDev.Power ||
                                         existing.DisplayName != targetDev.DisplayName ||
                                         existing.IsDefault != targetDev.IsDefault ||
                                         existing.Controls.Count != targetDev.Controls.Count ||
                                         existing.Controls.Any(c => {
                                             var tc = targetDev.Controls.FirstOrDefault(dc => dc.ControlId == c.ControlId);
                                             return tc == null;
                                         });
                
                if (structuralChanged)
                {
                    ActiveDevices.RemoveAt(existingIdx);
                    ActiveDevices.Insert(i, targetDev);
                }
                else
                {
                    // No structural changes, just update control values in-place
                    foreach (var c in existing.Controls)
                    {
                        var tc = targetDev.Controls.FirstOrDefault(dc => dc.ControlId == c.ControlId);
                        if (tc != null && Math.Abs(c.Value - tc.Value) > 0.01)
                        {
                            c.Value = tc.Value;
                            if (c.DisplayName.Equals("Mute", StringComparison.OrdinalIgnoreCase))
                            {
                                existing.RaiseMuteChanged();
                            }
                        }
                    }
                    if (existingIdx != i)
                    {
                        ActiveDevices.Move(existingIdx, i);
                    }
                }
            }
            else
            {
                ActiveDevices.Insert(i, targetDev);
            }
        }

        // Remove any extra devices at the end
        while (ActiveDevices.Count > listToDisplay.Count)
        {
            ActiveDevices.RemoveAt(ActiveDevices.Count - 1);
        }

        DeviceListChanged?.Invoke();
    }

    public void StartDiscoveryTimer()
    {
        lock (_timerSync)
        {
            _discoveryAttempts = 0;
            if (_isTimerRunning)
            {
                // Already running, but reset attempts for a fresh cycle
                return;
            }
            _isTimerRunning = true;
            _discoveryTimer?.Dispose();
            _discoveryTimer = new System.Threading.Timer(OnDiscoveryTimerTick, null, 0, 5000);
            System.Diagnostics.Debug.WriteLine("Background discovery timer started.");
        }
    }

    public void StopDiscoveryTimer()
    {
        lock (_timerSync)
        {
            if (!_isTimerRunning) return;
            _isTimerRunning = false;
            _discoveryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            System.Diagnostics.Debug.WriteLine("Background discovery timer suspended.");
        }
    }

    private void OnDiscoveryTimerTick(object? state)
    {

        try
        {
            LGSTrayHID.HidppManagerContext.Instance.RediscoverDevices();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to trigger background rediscover: {ex.Message}");
        }

        _discoveryAttempts++;

        // Auto-suspend: Stop timer if all cached input devices are online, or if we timed out (60 attempts / 5 mins)
        bool anyExpectedInputOffline = _allDevices.Values.Any(d => !d.IsOnline &&
            (d.Type == DeviceType.Keyboard || d.Type == DeviceType.Mouse || d.Type == DeviceType.Controller) &&
            _config.CachedDevices.Any(c => c.DeviceId == d.DeviceId));

        if (!anyExpectedInputOffline || _discoveryAttempts >= 60)
        {
            StopDiscoveryTimer();
        }
    }

    public IEnumerable<DeviceCacheInfo> GetCachedDevices() => _config.CachedDevices;

    public void RaiseTestAlert()
    {
        OnRaiseNotification?.Invoke("Low Battery Alert (Test)", "Wireless Mouse MX Master 2S is at 10% battery.");
        GearTray.Contracts.EventLogger.Log("Test low battery alert triggered by user.");
    }
}
