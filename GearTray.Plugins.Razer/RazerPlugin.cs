using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GearTray.Contracts;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace GearTray.Plugins.Razer
{
    public class RazerPlugin : IDevicePlugin, IMMNotificationClient
    {
        public string PluginId => "GearTray.Plugins.Razer";
        public string DisplayName => "Razer Microphone Monitor";

        public event EventHandler<DeviceStatusEventArgs>? DeviceStatusChanged;

        private readonly ConcurrentDictionary<string, MMDevice> _monitoredDevices = new();
        private readonly ConcurrentDictionary<string, DeviceStatusEventArgs> _deviceCache = new();
        private readonly object _syncLock = new();

        private MMDeviceEnumerator? _deviceEnumerator;
        private System.Windows.Threading.Dispatcher? _dispatcher;
        private bool _isInitialized;

        public void Initialize()
        {
            lock (_syncLock)
            {
                if (_isInitialized) return;
                _isInitialized = true;

                _dispatcher = System.Windows.Application.Current?.Dispatcher;

                try
                {
                    _deviceEnumerator = new MMDeviceEnumerator();
                    _deviceEnumerator.RegisterEndpointNotificationCallback(this);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"RazerPlugin: Failed to register endpoint notifications: {ex.Message}");
                }

                RefreshRazerMics();
            }
        }

        public void Shutdown()
        {
            lock (_syncLock)
            {
                if (!_isInitialized) return;
                _isInitialized = false;

                if (_deviceEnumerator != null)
                {
                    try
                    {
                        _deviceEnumerator.UnregisterEndpointNotificationCallback(this);
                    }
                    catch { }
                }

                foreach (var device in _monitoredDevices.Values)
                {
                    try
                    {
                        device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                        device.Dispose();
                    }
                    catch { }
                }

                _monitoredDevices.Clear();
                _deviceCache.Clear();
            }
        }

        public IEnumerable<DeviceStatusEventArgs> GetActiveDevices()
        {
            return _deviceCache.Values.ToList();
        }

        private void RefreshRazerMics()
        {
            lock (_syncLock)
            {
                if (!_isInitialized || _deviceEnumerator == null) return;

                var activeMics = new List<MMDevice>();
                try
                {
                    var endpoints = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    foreach (var endpoint in endpoints)
                    {
                        if (endpoint.FriendlyName.Contains("Razer", StringComparison.OrdinalIgnoreCase))
                        {
                            activeMics.Add(endpoint);
                        }
                        else
                        {
                            endpoint.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"RazerPlugin: Error enumerating capture endpoints: {ex.Message}");
                }

                var activeIds = activeMics.Select(d => d.ID).ToHashSet();

                // 1. Handle removed devices
                var removedIds = _monitoredDevices.Keys.Where(id => !activeIds.Contains(id)).ToList();
                foreach (var id in removedIds)
                {
                    if (_monitoredDevices.TryRemove(id, out var device))
                    {
                        try
                        {
                            device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                            device.Dispose();
                        }
                        catch { }
                    }

                    if (_deviceCache.TryRemove(id, out var cached))
                    {
                        var offlineArgs = new DeviceStatusEventArgs(
                            deviceId: id,
                            displayName: cached.DisplayName,
                            type: cached.Type,
                            batteryPercentage: -1,
                            power: PowerStatus.Unknown,
                            isOnline: false
                        );
                        DeviceStatusChanged?.Invoke(this, offlineArgs);
                        EventLogger.Log("POWER_OFF", $"Razer device offline: {cached.DisplayName}", "#C62828");
                    }
                }

                // 2. Add or update active devices
                foreach (var mic in activeMics)
                {
                    bool isNew = !_monitoredDevices.ContainsKey(mic.ID);
                    if (isNew)
                    {
                        _monitoredDevices[mic.ID] = mic;
                        try
                        {
                            mic.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"RazerPlugin: Error binding notification: {ex.Message}");
                        }
                    }

                    NotifyDeviceStatus(mic);
                }
            }
        }

        private void NotifyDeviceStatus(MMDevice mic)
        {
            try
            {
                bool isMuted = mic.AudioEndpointVolume.Mute;
                float volume = mic.AudioEndpointVolume.MasterVolumeLevelScalar;

                var controls = new List<DeviceControl>
                {
                    new DeviceControl
                    {
                        ControlId = $"{mic.ID}_mute",
                        DisplayName = "Mute",
                        ControlType = "Toggle",
                        Value = isMuted ? 1.0 : 0.0,
                        OnControlChanged = (val) =>
                        {
                            RunOnDispatcher(() =>
                            {
                                try
                                {
                                    mic.AudioEndpointVolume.Mute = val > 0.5;
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"RazerPlugin: Mute failed: {ex.Message}");
                                }
                            });
                        }
                    },
                    new DeviceControl
                    {
                        ControlId = $"{mic.ID}_gain",
                        DisplayName = "Gain",
                        ControlType = "Slider",
                        Value = volume,
                        OnControlChanged = (val) =>
                        {
                            RunOnDispatcher(() =>
                            {
                                try
                                {
                                    mic.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(val, 0.0, 1.0);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"RazerPlugin: Gain adjust failed: {ex.Message}");
                                }
                            });
                        }
                    }
                };

                var args = new DeviceStatusEventArgs(
                    deviceId: mic.ID,
                    displayName: mic.FriendlyName,
                    type: DeviceType.Generic,
                    batteryPercentage: -1,
                    power: PowerStatus.Wired,
                    isOnline: true,
                    controls: controls
                );

                // Filter redundant updates
                if (_deviceCache.TryGetValue(mic.ID, out var existing))
                {
                    var existingMute = existing.Controls.FirstOrDefault(c => c.DisplayName == "Mute")?.Value;
                    var existingGain = existing.Controls.FirstOrDefault(c => c.DisplayName == "Gain")?.Value;

                    if (existing.IsOnline == args.IsOnline &&
                        existingMute == (isMuted ? 1.0 : 0.0) &&
                        Math.Abs((existingGain ?? 0.0) - volume) < 0.01 &&
                        existing.DisplayName == args.DisplayName)
                    {
                        return; // No change
                    }
                }

                _deviceCache[mic.ID] = args;
                DeviceStatusChanged?.Invoke(this, args);

                if (!existing?.IsOnline ?? true)
                {
                    EventLogger.Log("POWER_ON", $"Razer device online: {mic.FriendlyName} (Muted={isMuted}, Gain={(int)(volume * 100)}%)", "#2E7D32");
                }
                else
                {
                    EventLogger.Log("SYSTEM", $"Razer device status updated: {mic.FriendlyName} (Muted={isMuted}, Gain={(int)(volume * 100)}%)", "#888888");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RazerPlugin: Error notifying status for {mic.FriendlyName}: {ex.Message}");
            }
        }

        private void RunOnDispatcher(Action action)
        {
            if (_dispatcher != null && !_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data)
        {
            RunOnDispatcher(() =>
            {
                // Re-notify status for all monitored microphones since volume notification triggers
                lock (_syncLock)
                {
                    foreach (var mic in _monitoredDevices.Values)
                    {
                        NotifyDeviceStatus(mic);
                    }
                }
            });
        }

        // IMMNotificationClient Implementation
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            RunOnDispatcher(() => RefreshRazerMics());
        }

        public void OnDeviceAdded(string deviceId)
        {
            RunOnDispatcher(() => RefreshRazerMics());
        }

        public void OnDeviceRemoved(string deviceId)
        {
            RunOnDispatcher(() => RefreshRazerMics());
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture)
            {
                RunOnDispatcher(() => RefreshRazerMics());
            }
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            // Optional property updates
        }
    }
}
