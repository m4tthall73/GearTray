using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using GearTray.Contracts;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace GearTray.Plugins.Audio;

public class AudioPlugin : IDevicePlugin, IMMNotificationClient
{
    public virtual string PluginId => "GearTray.Plugins.Audio";
    public virtual string DisplayName => "Windows Audio Controller";
 
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _defaultRenderDevice;
    private AudioEndpointVolume? _volumeControl;
    private AudioEndpointVolumeNotificationDelegate? _volumeNotificationDelegate;
    private MMDevice? _defaultCaptureDevice;
    private AudioEndpointVolume? _captureVolumeControl;
    private AudioEndpointVolumeNotificationDelegate? _captureVolumeNotificationDelegate;
    private readonly List<DeviceControl> _controls = [];
 
    public event EventHandler<DeviceStatusEventArgs>? DeviceStatusChanged;
 
    private System.Windows.Threading.Dispatcher? _dispatcher;
    private DateTime _lastUserVolumeChangeTime = DateTime.MinValue;
    private DateTime _lastUserMuteChangeTime = DateTime.MinValue;

    private System.Threading.Timer? _pollTimer;
    private bool _headsetOnline = false;
    private int _headsetBattery = -1;
    private PowerStatus _headsetPower = PowerStatus.PoweredOff;
    private readonly Dictionary<string, (bool isOnline, int batteryPercentage, PowerStatus power)> _lastSentStatus = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct hid_device_info
    {
        public IntPtr path;
        public ushort vendor_id;
        public ushort product_id;
        public IntPtr serial_number;
        public ushort release_number;
        public IntPtr manufacturer_string;
        public IntPtr product_string;
        public ushort usage_page;
        public ushort usage;
        public int interface_number;
        public IntPtr next;
        public int bus_type; // Match native hidapi.dll struct size
    }

    [DllImport("hidapi", EntryPoint = "hid_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_init();

    [DllImport("hidapi", EntryPoint = "hid_exit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_exit();

    [DllImport("hidapi", EntryPoint = "hid_enumerate", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe IntPtr hid_enumerate(ushort vendor_id, ushort product_id);

    [DllImport("hidapi", EntryPoint = "hid_free_enumeration", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void hid_free_enumeration(IntPtr devs);

    [DllImport("hidapi", EntryPoint = "hid_open_path", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe IntPtr hid_open_path(IntPtr path);

    [DllImport("hidapi", EntryPoint = "hid_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void hid_close(IntPtr dev);

    [DllImport("hidapi", EntryPoint = "hid_write", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int hid_write(IntPtr dev, byte[] data, UIntPtr length);

    [DllImport("hidapi", EntryPoint = "hid_read_timeout", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int hid_read_timeout(IntPtr dev, byte[] data, UIntPtr length, int milliseconds);

    private void PollSteelSeriesHeadset()
    {
        try
        {
            _ = hid_init();
            IntPtr devs = hid_enumerate(0x1038, 0);
            if (devs == IntPtr.Zero)
            {
                _headsetOnline = false;
                _headsetBattery = -1;
                _headsetPower = PowerStatus.PoweredOff;
                return;
            }

            // Supported SteelSeries Arctis Nova 7 product IDs from original C++ codebase
            ushort[] nova7Pids = { 0x2202, 0x22A1, 0x227e, 0x2206, 0x2258, 0x229e, 0x22ad, 0x223a, 0x22a9, 0x227a, 0x22a4, 0x22a5 };
            // Original models (discrete battery: 0-4 levels -> 0%/25%/50%/75%/100%)
            ushort[] discretePids = { 0x2202, 0x2206, 0x223a, 0x227a, 0x22a4 };

            IntPtr matchedPath = IntPtr.Zero;
            ushort matchedProductId = 0;
            IntPtr curPtr = devs;
            while (curPtr != IntPtr.Zero)
            {
                var info = Marshal.PtrToStructure<hid_device_info>(curPtr);
                
                // Only consider matched product IDs to avoid other SteelSeries mice/keyboards
                if (Array.IndexOf(nova7Pids, info.product_id) >= 0)
                {
                    if (info.usage_page == 0xffc0 && info.usage == 0x01)
                    {
                        matchedPath = info.path;
                        matchedProductId = info.product_id;
                        break;
                    }
                    if (info.interface_number == 3 && matchedPath == IntPtr.Zero)
                    {
                        matchedPath = info.path;
                        matchedProductId = info.product_id;
                    }
                }
                curPtr = info.next;
            }

            bool polledSuccess = false;
            if (matchedPath != IntPtr.Zero)
            {
                IntPtr handle = hid_open_path(matchedPath);
                if (handle != IntPtr.Zero)
                {
                    byte[] writeBuf = new byte[] { 0x00, 0xb0 };
                    int written = hid_write(handle, writeBuf, (UIntPtr)writeBuf.Length);
                    if (written >= 0)
                    {
                        byte[] readBuf = new byte[128];
                        int bytesRead = hid_read_timeout(handle, readBuf, (UIntPtr)readBuf.Length, 500);
                        if (bytesRead >= 4)
                        {
                            polledSuccess = true;
                            if (readBuf[3] == 0x00) // HEADSET_OFFLINE
                            {
                                _headsetOnline = false;
                                _headsetBattery = -1;
                                _headsetPower = PowerStatus.PoweredOff;
                            }
                            else
                            {
                                _headsetOnline = true;
                                bool charging = (readBuf[3] == 0x01 || readBuf[3] == 0x02);
                                _headsetPower = charging ? PowerStatus.Charging : PowerStatus.Discharging;
                                
                                int level = readBuf[2];
                                bool isDiscrete = Array.IndexOf(discretePids, matchedProductId) >= 0;
                                if (isDiscrete)
                                {
                                    level = Math.Clamp(level, 0, 4) * 25;
                                }
                                _headsetBattery = Math.Clamp(level, 0, 100);
                            }
                        }
                    }
                    hid_close(handle);
                }
            }

            hid_free_enumeration(devs);

            if (!polledSuccess)
            {
                _headsetOnline = false;
                _headsetBattery = -1;
                _headsetPower = PowerStatus.PoweredOff;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error polling SteelSeries headset HID: {ex.Message}");
        }
    }
 
    public virtual void Initialize()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        _deviceEnumerator = new MMDeviceEnumerator();
        _deviceEnumerator.RegisterEndpointNotificationCallback(this);
        RefreshAudioEndpoint();
        RefreshCaptureAudioEndpoint();
 
        // Start SteelSeries headset polling timer (every 5 seconds, first tick immediate)
        _pollTimer = new System.Threading.Timer(OnPollTimerTick, null, 0, 5000);
    }
 
    public virtual void Shutdown()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;

        if (_deviceEnumerator != null)
        {
            _deviceEnumerator.UnregisterEndpointNotificationCallback(this);
        }
        if (_volumeControl != null)
        {
            if (_volumeNotificationDelegate != null)
            {
                _volumeControl.OnVolumeNotification -= _volumeNotificationDelegate;
            }
            _volumeControl.Dispose();
        }
        if (_captureVolumeControl != null)
        {
            if (_captureVolumeNotificationDelegate != null)
            {
                _captureVolumeControl.OnVolumeNotification -= _captureVolumeNotificationDelegate;
            }
            _captureVolumeControl.Dispose();
        }
        _defaultRenderDevice?.Dispose();
        _defaultCaptureDevice?.Dispose();
        _deviceEnumerator?.Dispose();
    }

    private void OnPollTimerTick(object? state)
    {
        PollSteelSeriesHeadset();

        if (_dispatcher != null)
        {
            _dispatcher.BeginInvoke(new Action(() => ProcessStatusUpdates()));
        }
        else
        {
            ProcessStatusUpdates();
        }
    }

    private void ProcessStatusUpdates()
    {
        try
        {
            var devices = GetActiveDevices().ToList();
            var activeIds = devices.Select(d => d.DeviceId).ToHashSet();

            // Cleanup stale statuses
            var keysToRemove = _lastSentStatus.Keys.Where(k => !activeIds.Contains(k)).ToList();
            foreach (var key in keysToRemove)
            {
                _lastSentStatus.Remove(key);
            }

            // Check for changes and notify coordinator
            foreach (var dev in devices)
            {
                bool changed = false;
                if (_lastSentStatus.TryGetValue(dev.DeviceId, out var last))
                {
                    if (last.isOnline != dev.IsOnline ||
                        last.batteryPercentage != dev.BatteryPercentage ||
                        last.power != dev.Power)
                    {
                        changed = true;
                    }
                }
                else
                {
                    changed = true;
                }

                if (changed)
                {
                    _lastSentStatus[dev.DeviceId] = (dev.IsOnline, dev.BatteryPercentage, dev.Power);
                    DeviceStatusChanged?.Invoke(this, dev);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ProcessStatusUpdates Error: {ex.Message}");
        }
    }
 
    public virtual IEnumerable<DeviceStatusEventArgs> GetActiveDevices()
    {
        RefreshAudioEndpoint();
        
        var deviceList = new List<DeviceStatusEventArgs>();

        if (_deviceEnumerator == null) return deviceList;

        try
        {
            var endpoints = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
            foreach (var device in endpoints)
            {
                if (device.State == DeviceState.NotPresent || device.State == DeviceState.Unplugged)
                {
                    device.Dispose();
                    continue;
                }
                string devId = device.ID.ToLowerInvariant();
                bool isDefault = (_defaultRenderDevice != null && string.Equals(_defaultRenderDevice.ID, devId, StringComparison.OrdinalIgnoreCase));
                
                deviceList.Add(CreateRenderDeviceStatus(device, isDefault));
                device.Dispose();
            }

            // Enumerate Capture endpoints
            var captureEndpoints = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All);
            string defaultCaptureId = string.Empty;
            try
            {
                using (var defaultCapture = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
                {
                    defaultCaptureId = defaultCapture?.ID ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetActiveDevices Capture Default Error: {ex.Message}");
            }

            foreach (var device in captureEndpoints)
            {
                if (device.State == DeviceState.NotPresent || device.State == DeviceState.Unplugged)
                {
                    device.Dispose();
                    continue;
                }
                string friendlyName = device.FriendlyName;

                // Skip Razer microphones as they are handled by RazerPlugin
                if (friendlyName.Contains("Razer", StringComparison.OrdinalIgnoreCase))
                {
                    device.Dispose();
                    continue;
                }

                bool isDefault = string.Equals(defaultCaptureId, device.ID, StringComparison.OrdinalIgnoreCase);
                deviceList.Add(CreateCaptureDeviceStatus(device, isDefault));
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetActiveDevices Error: {ex.Message}");
        }

        return deviceList;
    }

    private DeviceStatusEventArgs CreateRenderDeviceStatus(MMDevice device, bool isDefault)
    {
        string devId = device.ID.ToLowerInvariant();
        string friendlyName = device.FriendlyName;

        // Determine DeviceType for the icon
        DeviceType type = DeviceType.AudioOutput;
        string lowerName = friendlyName.ToLowerInvariant();
        if (lowerName.Contains("headset") || lowerName.Contains("headphone") || 
            lowerName.Contains("earphone") || lowerName.Contains("buds") || 
            lowerName.Contains("headphones"))
        {
            type = DeviceType.Headset;
        }

        var controls = new List<DeviceControl>();

        if (isDefault)
        {
            var mute = new DeviceControl
            {
                ControlId = $"audio_mute_{devId}",
                DisplayName = "Mute",
                ControlType = "Toggle",
                Value = IsMuted() ? 1 : 0,
                OnControlChanged = (val) => SetMuted(val > 0.5)
            };

            var vol = new DeviceControl
            {
                ControlId = $"audio_volume_{devId}",
                DisplayName = "Volume",
                ControlType = "Slider",
                Value = GetVolume() * 100,
                OnControlChanged = (val) => SetVolume((float)(val / 100.0))
            };

            controls.Add(mute);
            controls.Add(vol);
        }
        else
        {
            var activateAction = new DeviceControl
            {
                ControlId = $"audio_activate_{devId}",
                DisplayName = "Activate",
                ControlType = "Action",
                Value = 0,
                OnControlChanged = (val) =>
                {
                    if (val > 0.5)
                    {
                        SetDefaultDevice(devId);
                    }
                }
            };
            controls.Add(activateAction);
        }

        PowerStatus power = PowerStatus.Wired;
        bool isOnline = device.State == DeviceState.Active;
        int battery = -1;

        bool isSteelSeries = lowerName.Contains("arctis") || lowerName.Contains("nova") || lowerName.Contains("steelseries");
        if (isSteelSeries)
        {
            isOnline = _headsetOnline;
            power = _headsetPower;
            battery = _headsetBattery;
        }
        else if (lowerName.Contains("wireless") || lowerName.Contains("bluetooth"))
        {
            power = isOnline ? PowerStatus.Unknown : PowerStatus.PoweredOff;
        }
        else if (!isOnline)
        {
            power = type == DeviceType.Headset ? PowerStatus.PoweredOff : PowerStatus.Unknown;
        }

        return new DeviceStatusEventArgs(
            deviceId: "audio_dev_" + devId,
            displayName: friendlyName,
            type: type,
            batteryPercentage: battery,
            power: power,
            isOnline: isOnline,
            controls: controls,
            isDefault: isDefault
        );
    }

    private DeviceStatusEventArgs CreateCaptureDeviceStatus(MMDevice device, bool isDefault)
    {
        string devId = device.ID.ToLowerInvariant();
        string friendlyName = device.FriendlyName;
        DeviceType type = DeviceType.Microphone;

        var controls = new List<DeviceControl>();

        if (isDefault)
        {
            string capturedDeviceId = device.ID;
            
            bool isMuted = false;
            float volLevel = 1.0f;
            try
            {
                isMuted = device.AudioEndpointVolume.Mute;
                volLevel = device.AudioEndpointVolume.MasterVolumeLevelScalar;
            }
            catch { }

            var mute = new DeviceControl
            {
                ControlId = $"audio_mute_{devId}",
                DisplayName = "Mute",
                ControlType = "Toggle",
                Value = isMuted ? 1.0 : 0.0,
                OnControlChanged = (val) =>
                {
                    try
                    {
                        if (_deviceEnumerator != null)
                        {
                            using (var dev = _deviceEnumerator.GetDevice(capturedDeviceId))
                            {
                                dev.AudioEndpointVolume.Mute = val > 0.5;
                            }
                        }
                        TriggerCaptureStatusChanged();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"AudioPlugin: Mute capture failed: {ex.Message}");
                    }
                }
            };

            var gain = new DeviceControl
            {
                ControlId = $"audio_gain_{devId}",
                DisplayName = "Gain",
                ControlType = "Slider",
                Value = volLevel * 100,
                OnControlChanged = (val) =>
                {
                    try
                    {
                        if (_deviceEnumerator != null)
                        {
                            using (var dev = _deviceEnumerator.GetDevice(capturedDeviceId))
                            {
                                dev.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(val / 100.0, 0.0, 1.0);
                            }
                        }
                        TriggerCaptureStatusChanged();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"AudioPlugin: Gain capture failed: {ex.Message}");
                    }
                }
            };

            controls.Add(mute);
            controls.Add(gain);
        }
        else
        {
            var activateAction = new DeviceControl
            {
                ControlId = $"audio_activate_{devId}",
                DisplayName = "Activate",
                ControlType = "Action",
                Value = 0,
                OnControlChanged = (val) =>
                {
                    if (val > 0.5)
                    {
                        SetDefaultCaptureDevice(device.ID);
                    }
                }
            };
            controls.Add(activateAction);
        }

        PowerStatus power = PowerStatus.Wired;
        bool isOnline = device.State == DeviceState.Active;
        int battery = -1;

        string lowerName = friendlyName.ToLowerInvariant();
        bool isSteelSeries = lowerName.Contains("arctis") || lowerName.Contains("nova") || lowerName.Contains("steelseries");
        if (isSteelSeries)
        {
            isOnline = _headsetOnline;
            power = _headsetPower;
            battery = _headsetBattery;
        }
        else if (lowerName.Contains("wireless") || lowerName.Contains("bluetooth"))
        {
            power = isOnline ? PowerStatus.Unknown : PowerStatus.PoweredOff;
        }
        else if (!isOnline)
        {
            power = PowerStatus.Unknown;
        }

        return new DeviceStatusEventArgs(
            deviceId: "audio_dev_" + devId,
            displayName: friendlyName,
            type: type,
            batteryPercentage: battery,
            power: power,
            isOnline: isOnline,
            controls: controls,
            isDefault: isDefault
        );
    }

    private void TriggerRenderStatusChanged()
    {
        if (_defaultRenderDevice != null)
        {
            var args = CreateRenderDeviceStatus(_defaultRenderDevice, true);
            DeviceStatusChanged?.Invoke(this, args);
        }
    }

    private void TriggerCaptureStatusChanged()
    {
        if (_defaultCaptureDevice != null)
        {
            var args = CreateCaptureDeviceStatus(_defaultCaptureDevice, true);
            DeviceStatusChanged?.Invoke(this, args);
        }
    }

    private void RefreshAudioEndpoint()
    {
        MMDevice? newDevice = null;
        try
        {
            if (_deviceEnumerator == null) return;
            
            newDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            if (newDevice == null) return;

            if (_defaultRenderDevice == null || !string.Equals(_defaultRenderDevice.ID, newDevice.ID, StringComparison.OrdinalIgnoreCase))
            {
                if (_volumeControl != null)
                {
                    if (_volumeNotificationDelegate != null)
                    {
                        _volumeControl.OnVolumeNotification -= _volumeNotificationDelegate;
                    }
                    _volumeControl.Dispose();
                    _volumeControl = null;
                }
                _defaultRenderDevice?.Dispose();
                _defaultRenderDevice = newDevice;
                newDevice = null; // Transfer ownership to prevent dispose in finally
                _volumeControl = _defaultRenderDevice.AudioEndpointVolume;
                _volumeNotificationDelegate = new AudioEndpointVolumeNotificationDelegate(OnVolumeChanged);
                _volumeControl.OnVolumeNotification += _volumeNotificationDelegate;
                System.Diagnostics.Debug.WriteLine($"RefreshAudioEndpoint: Switched to default device '{_defaultRenderDevice.FriendlyName}' (ID={_defaultRenderDevice.ID})");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RefreshAudioEndpoint Error: {ex.Message}");
        }
        finally
        {
            newDevice?.Dispose();
        }
    }

    private void RefreshCaptureAudioEndpoint()
    {
        MMDevice? newDevice = null;
        try
        {
            if (_deviceEnumerator == null) return;
            
            newDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            if (newDevice == null) return;

            if (_defaultCaptureDevice == null || !string.Equals(_defaultCaptureDevice.ID, newDevice.ID, StringComparison.OrdinalIgnoreCase))
            {
                if (_captureVolumeControl != null)
                {
                    if (_captureVolumeNotificationDelegate != null)
                    {
                        _captureVolumeControl.OnVolumeNotification -= _captureVolumeNotificationDelegate;
                    }
                    _captureVolumeControl.Dispose();
                    _captureVolumeControl = null;
                }
                _defaultCaptureDevice?.Dispose();
                _defaultCaptureDevice = newDevice;
                newDevice = null; // Transfer ownership to prevent dispose in finally
                if (_defaultCaptureDevice != null)
                {
                    _captureVolumeControl = _defaultCaptureDevice.AudioEndpointVolume;
                    _captureVolumeNotificationDelegate = new AudioEndpointVolumeNotificationDelegate(OnCaptureVolumeChanged);
                    _captureVolumeControl.OnVolumeNotification += _captureVolumeNotificationDelegate;
                }
                System.Diagnostics.Debug.WriteLine($"RefreshCaptureAudioEndpoint: Switched to default capture device '{_defaultCaptureDevice?.FriendlyName}' (ID={_defaultCaptureDevice?.ID})");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RefreshCaptureAudioEndpoint Error: {ex.Message}");
        }
        finally
        {
            newDevice?.Dispose();
        }
    }

    private void OnCaptureVolumeChanged(AudioVolumeNotificationData data)
    {
        if (_dispatcher != null && !_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => OnCaptureVolumeChanged(data)));
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[AUDIO PLUGIN] OnCaptureVolumeChanged: Muted={data.Muted}, MasterVolume={data.MasterVolume}");
        EventLogger.Log("SYSTEM", $"[AudioPlugin] OnCaptureVolumeChanged ({_defaultCaptureDevice?.FriendlyName ?? "Unknown"}): Muted={data.Muted}, Volume={(int)(data.MasterVolume * 100)}%", "#888888");
        
        // If the change was initiated by the user in our app recently, ignore it
        if ((DateTime.UtcNow - _lastUserVolumeChangeTime).TotalMilliseconds < 500 ||
            (DateTime.UtcNow - _lastUserMuteChangeTime).TotalMilliseconds < 500)
        {
            return;
        }
        
        TriggerCaptureStatusChanged();
    }
 
    private float GetVolume()
    {
        float vol = _volumeControl?.MasterVolumeLevelScalar ?? 0.0f;
        return vol;
    }

    private void SetVolume(float level)
    {
        if (_volumeControl != null)
        {
            System.Diagnostics.Debug.WriteLine($"SetVolume: Requesting volume level={level}");
            _lastUserVolumeChangeTime = DateTime.UtcNow;
            _volumeControl.MasterVolumeLevelScalar = Math.Clamp(level, 0.0f, 1.0f);
            TriggerRenderStatusChanged();
        }
    }

    private bool IsMuted()
    {
        bool muted = _volumeControl?.Mute ?? false;
        return muted;
    }

    private void SetMuted(bool mute)
    {
        if (_volumeControl != null)
        {
            System.Diagnostics.Debug.WriteLine($"SetMuted: Requesting mute={mute}");
            _lastUserMuteChangeTime = DateTime.UtcNow;
            _volumeControl.Mute = mute;
            TriggerRenderStatusChanged();
        }
    }

    private void OnVolumeChanged(AudioVolumeNotificationData data)
    {
        if (_dispatcher != null && !_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => OnVolumeChanged(data)));
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[AUDIO PLUGIN] OnVolumeChanged: Muted={data.Muted}, MasterVolume={data.MasterVolume}");
        EventLogger.Log("SYSTEM", $"[AudioPlugin] OnVolumeChanged ({_defaultRenderDevice?.FriendlyName ?? "Unknown"}): Muted={data.Muted}, Volume={(int)(data.MasterVolume * 100)}%", "#888888");
        
        // If the change was initiated by the user in our app recently, ignore it
        if ((DateTime.UtcNow - _lastUserVolumeChangeTime).TotalMilliseconds < 500 ||
            (DateTime.UtcNow - _lastUserMuteChangeTime).TotalMilliseconds < 500)
        {
            return;
        }
        
        TriggerRenderStatusChanged();
    }

    private void SetDefaultDevice(string deviceId)
    {
        try
        {
            EventLogger.Log("AUDIO_SWITCH", $"Setting default audio device to ID={deviceId}", "#6F42C1");
            var policyConfig = (IPolicyConfig)new _CPolicyConfigClient();
            int hr = policyConfig.SetDefaultEndpoint(deviceId, (uint)ERole.eConsole);
            EventLogger.Log("AUDIO_SWITCH", $"SetDefaultEndpoint (Console) returned: 0x{hr:X}", "#6F42C1");
            hr = policyConfig.SetDefaultEndpoint(deviceId, (uint)ERole.eMultimedia);
            EventLogger.Log("AUDIO_SWITCH", $"SetDefaultEndpoint (Multimedia) returned: 0x{hr:X}", "#6F42C1");
            hr = policyConfig.SetDefaultEndpoint(deviceId, (uint)ERole.eCommunications);
            EventLogger.Log("AUDIO_SWITCH", $"SetDefaultEndpoint (Communications) returned: 0x{hr:X}", "#6F42C1");

            RefreshAudioEndpoint();
            TriggerStatusChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to set default audio device: {ex.Message}");
            EventLogger.Log("SYSTEM", $"Failed to set default audio device: {ex.Message}", "#C62828");
        }
    }

    public virtual string? FindCaptureDeviceIdByName(string nameSubstring)
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            var match = endpoints.FirstOrDefault(e => e.FriendlyName.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase));
            return match?.ID;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error finding capture device by name '{nameSubstring}': {ex.Message}");
            return null;
        }
    }

    public virtual string? GetDefaultCaptureDeviceId()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return dev?.ID;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting default capture device ID: {ex.Message}");
            return null;
        }
    }

    public virtual void SetDefaultCaptureDevice(string deviceId)
    {
        try
        {
            EventLogger.Log("AUDIO_SWITCH", $"Setting default capture/recording device to ID={deviceId}", "#8A2BE2");
            var policyConfig = (IPolicyConfig)new _CPolicyConfigClient();
            int hr = policyConfig.SetDefaultEndpoint(deviceId, (uint)ERole.eConsole);
            EventLogger.Log("AUDIO_SWITCH", $"SetDefaultCaptureEndpoint (Console) returned: 0x{hr:X}", "#8A2BE2");
            hr = policyConfig.SetDefaultEndpoint(deviceId, (uint)ERole.eMultimedia);
            EventLogger.Log("AUDIO_SWITCH", $"SetDefaultCaptureEndpoint (Multimedia) returned: 0x{hr:X}", "#8A2BE2");
            hr = policyConfig.SetDefaultEndpoint(deviceId, (uint)ERole.eCommunications);
            EventLogger.Log("AUDIO_SWITCH", $"SetDefaultCaptureEndpoint (Communications) returned: 0x{hr:X}", "#8A2BE2");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error setting default capture device: {ex.Message}");
            EventLogger.Log("SYSTEM", $"Failed to set default capture device: {ex.Message}", "#C62828");
        }
    }

    private void TriggerStatusChanged()
    {
        foreach (var device in GetActiveDevices())
        {
            DeviceStatusChanged?.Invoke(this, device);
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        EventLogger.Log($"Audio Event: OnDeviceStateChanged - ID={deviceId}, State={newState}");
        if (_dispatcher != null)
        {
            _dispatcher.BeginInvoke(new Action(() => {
                RefreshAudioEndpoint();
                RefreshCaptureAudioEndpoint();
                TriggerStatusChanged();
            }));
        }
        else
        {
            RefreshAudioEndpoint();
            RefreshCaptureAudioEndpoint();
            TriggerStatusChanged();
        }
    }

    public void OnDeviceAdded(string deviceId)
    {
        EventLogger.Log($"Audio Event: OnDeviceAdded - ID={deviceId}");
        if (_dispatcher != null)
        {
            _dispatcher.BeginInvoke(new Action(() => {
                RefreshAudioEndpoint();
                RefreshCaptureAudioEndpoint();
                TriggerStatusChanged();
            }));
        }
        else
        {
            RefreshAudioEndpoint();
            RefreshCaptureAudioEndpoint();
            TriggerStatusChanged();
        }
    }

    public void OnDeviceRemoved(string deviceId)
    {
        EventLogger.Log($"Audio Event: OnDeviceRemoved - ID={deviceId}");
        if (_dispatcher != null)
        {
            _dispatcher.BeginInvoke(new Action(() => {
                RefreshAudioEndpoint();
                RefreshCaptureAudioEndpoint();
                TriggerStatusChanged();
            }));
        }
        else
        {
            RefreshAudioEndpoint();
            RefreshCaptureAudioEndpoint();
            TriggerStatusChanged();
        }
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role == Role.Console)
        {
            EventLogger.Log($"Audio Event: OnDefaultDeviceChanged - ID={defaultDeviceId}");
            if (_dispatcher != null)
            {
                _dispatcher.BeginInvoke(new Action(() => {
                    if (flow == DataFlow.Render)
                    {
                        RefreshAudioEndpoint();
                    }
                    else if (flow == DataFlow.Capture)
                    {
                        RefreshCaptureAudioEndpoint();
                    }
                    TriggerStatusChanged();
                }));
            }
            else
            {
                if (flow == DataFlow.Render)
                {
                    RefreshAudioEndpoint();
                }
                else if (flow == DataFlow.Capture)
                {
                    RefreshCaptureAudioEndpoint();
                }
                TriggerStatusChanged();
            }
        }
    }

    public void OnPropertyValueChanged(string deviceId, PropertyKey key)
    {
    }
}
