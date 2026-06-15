using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
        private readonly ConcurrentDictionary<string, AudioEndpointVolume> _endpointVolumes = new();
        private readonly ConcurrentDictionary<string, AudioEndpointVolumeNotificationDelegate> _volumeDelegates = new();
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

                foreach (var id in _monitoredDevices.Keys)
                {
                    if (_monitoredDevices.TryRemove(id, out var device))
                    {
                        try
                        {
                            if (_endpointVolumes.TryRemove(id, out var volume))
                            {
                                if (_volumeDelegates.TryRemove(id, out var del))
                                {
                                    volume.OnVolumeNotification -= del;
                                }
                                volume.Dispose();
                            }
                            device.Dispose();
                        }
                        catch { }
                    }
                }

                _monitoredDevices.Clear();
                _endpointVolumes.Clear();
                _deviceCache.Clear();
            }
        }

        public IEnumerable<DeviceStatusEventArgs> GetActiveDevices()
        {
            return [.. _deviceCache.Values];
        }

        private void RefreshRazerMics()
        {
            lock (_syncLock)
            {
                if (!_isInitialized || _deviceEnumerator == null) return;

                List<MMDevice> activeMics = [];
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
                             if (_endpointVolumes.TryRemove(id, out var volume))
                             {
                                 if (_volumeDelegates.TryRemove(id, out var del))
                                 {
                                     volume.OnVolumeNotification -= del;
                                 }
                                 volume.Dispose();
                             }
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
                     string micId = mic.ID;
                     bool isNew = !_monitoredDevices.ContainsKey(micId);
                     if (isNew)
                     {
                         _monitoredDevices[micId] = mic;
                         try
                         {
                             var volume = mic.AudioEndpointVolume;
                             _endpointVolumes[micId] = volume;
                             var del = new AudioEndpointVolumeNotificationDelegate(OnVolumeNotification);
                             _volumeDelegates[micId] = del;
                             volume.OnVolumeNotification += del;
                         }
                         catch (Exception ex)
                         {
                             System.Diagnostics.Debug.WriteLine($"RazerPlugin: Error binding notification: {ex.Message}");
                         }
                     }
                     else
                     {
                         mic.Dispose();
                     }
 
                     if (_monitoredDevices.TryGetValue(micId, out var monitoredMic))
                     {
                         NotifyDeviceStatus(monitoredMic);
                     }
                 }
            }
        }

        private void NotifyDeviceStatus(MMDevice mic)
        {
            try
            {
                bool isMuted = mic.AudioEndpointVolume.Mute;
                float volume = mic.AudioEndpointVolume.MasterVolumeLevelScalar;

                string defaultCaptureId = string.Empty;
                try
                {
                    using var defaultDev = _deviceEnumerator?.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                    defaultCaptureId = defaultDev?.ID ?? string.Empty;
                }
                catch { }

                bool isDefault = string.Equals(mic.ID, defaultCaptureId, StringComparison.OrdinalIgnoreCase);

                List<DeviceControl> controls = [];
                
                // Mute and Gain are always exposed so the user can view/control them even when the mic is not default
                controls.Add(new DeviceControl
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
                });
                
                controls.Add(new DeviceControl
                {
                    ControlId = $"{mic.ID}_gain",
                    DisplayName = "Gain",
                    ControlType = "Slider",
                    Value = volume * 100,
                    OnControlChanged = (val) =>
                    {
                        RunOnDispatcher(() =>
                        {
                            try
                            {
                                mic.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(val / 100.0, 0.0, 1.0);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"RazerPlugin: Gain adjust failed: {ex.Message}");
                            }
                        });
                    }
                });

                if (!isDefault)
                {
                    controls.Add(new DeviceControl
                    {
                        ControlId = $"{mic.ID}_activate",
                        DisplayName = "Activate",
                        ControlType = "Action",
                        Value = 0.0,
                        OnControlChanged = (val) =>
                        {
                            if (val > 0.5)
                            {
                                RunOnDispatcher(() => SetDefaultCaptureDevice(mic.ID));
                            }
                        }
                    });
                }

                var args = new DeviceStatusEventArgs(
                    deviceId: mic.ID,
                    displayName: mic.FriendlyName,
                    type: DeviceType.Microphone,
                    batteryPercentage: -1,
                    power: PowerStatus.Wired,
                    isOnline: true,
                    controls: controls,
                    isDefault: isDefault
                );

                // Filter redundant updates
                if (_deviceCache.TryGetValue(mic.ID, out var existing))
                {
                    var existingMute = existing.Controls.FirstOrDefault(c => c.DisplayName == "Mute")?.Value;
                    var existingGain = existing.Controls.FirstOrDefault(c => c.DisplayName == "Gain")?.Value;
                    var newMute = isMuted ? 1.0 : 0.0;
                    var newGain = volume * 100;

                    if (existing.IsOnline == args.IsOnline &&
                        existing.IsDefault == args.IsDefault &&
                        existingMute == newMute &&
                        Math.Abs((existingGain ?? 0.0) - newGain) < 0.01 &&
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
                EventLogger.Log("SYSTEM", $"Razer Mic volume notification: Muted={data.Muted}, Volume={(int)(data.MasterVolume * 100)}%", "#888888");
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

        private static void SetDefaultCaptureDevice(string deviceId)
        {
            try
            {
                var policyConfig = (IPolicyConfig)new CPolicyConfigClient();
                // Set default for Console, Multimedia, and Communications roles
                policyConfig.SetDefaultEndpoint(deviceId, 0); // eConsole
                policyConfig.SetDefaultEndpoint(deviceId, 1); // eMultimedia
                policyConfig.SetDefaultEndpoint(deviceId, 2); // eCommunications
                EventLogger.Log("AUDIO_SWITCH", $"Setting default capture device to Razer Mic: {deviceId}", "#6F42C1");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RazerPlugin: SetDefaultCaptureDevice failed: {ex.Message}");
            }
        }
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class CPolicyConfigClient { }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, uint eRole);
        [PreserveSig] int SetEndpointVisibility();
    }
}
