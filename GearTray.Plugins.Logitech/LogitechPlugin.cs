using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GearTray.Contracts;
using LGSTrayHID;
using LGSTrayPrimitives.MessageStructs;

namespace GearTray.Plugins.Logitech;

public class LogitechPlugin : IDevicePlugin
{
    public string PluginId => "GearTray.Plugins.Logitech";
    public string DisplayName => "Logitech Battery Monitor";

    public event EventHandler<DeviceStatusEventArgs>? DeviceStatusChanged;

    private readonly ConcurrentDictionary<string, DeviceStatusEventArgs> _devices = new();
    private CancellationTokenSource? _cts;

    public void Initialize()
    {
        _cts = new CancellationTokenSource();

        HidppManagerContext.Instance.HidppDeviceEvent += OnHidppDeviceEvent;
        
        // Start the native manager
        HidppManagerContext.Instance.Start(_cts.Token);
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        HidppManagerContext.Instance.Stop();
        HidppManagerContext.Instance.HidppDeviceEvent -= OnHidppDeviceEvent;
        _cts?.Dispose();
    }

    public IEnumerable<DeviceStatusEventArgs> GetActiveDevices()
    {
        return _devices.Values.ToList();
    }

    private void OnHidppDeviceEvent(IPCMessageType messageType, IPCMessage message)
    {
        switch (messageType)
        {
            case IPCMessageType.INIT:
                if (message is InitMessage initMsg)
                {
                    bool wasOnline = _devices.TryGetValue(initMsg.deviceId, out var existing) && existing.IsOnline;

                    var newDev = new DeviceStatusEventArgs(
                        deviceId: initMsg.deviceId,
                        displayName: initMsg.deviceName,
                        type: MapDeviceType(initMsg.deviceType),
                        batteryPercentage: (wasOnline && existing != null) ? existing.BatteryPercentage : -1,
                        power: (wasOnline && existing != null) ? existing.Power : PowerStatus.Unknown,
                        isOnline: true
                    );
                    _devices[initMsg.deviceId] = newDev;

                    if (!wasOnline)
                    {
                        DeviceStatusChanged?.Invoke(this, newDev);
                    }
                }
                break;

            case IPCMessageType.UPDATE:
                if (message is UpdateMessage updateMsg)
                {
                    if (_devices.TryGetValue(updateMsg.deviceId, out var existing))
                    {
                        var updated = new DeviceStatusEventArgs(
                            deviceId: existing.DeviceId,
                            displayName: existing.DisplayName,
                            type: existing.Type,
                            batteryPercentage: (int)Math.Clamp(updateMsg.batteryPercentage, 0, 100),
                            power: MapPowerStatus(updateMsg.powerSupplyStatus),
                            isOnline: true
                        );
                        _devices[updateMsg.deviceId] = updated;

                        if (!existing.IsOnline || existing.BatteryPercentage != updated.BatteryPercentage || existing.Power != updated.Power)
                        {
                            DeviceStatusChanged?.Invoke(this, updated);
                        }
                    }
                }
                break;

            case IPCMessageType.OFFLINE:
                if (message is DeviceOfflineMessage offlineMsg)
                {
                    if (_devices.TryGetValue(offlineMsg.deviceId, out var existing))
                    {
                        var offline = new DeviceStatusEventArgs(
                            deviceId: existing.DeviceId,
                            displayName: existing.DisplayName,
                            type: existing.Type,
                            batteryPercentage: existing.BatteryPercentage,
                            power: existing.Power,
                            isOnline: false
                        );
                        _devices[offlineMsg.deviceId] = offline;
                        DeviceStatusChanged?.Invoke(this, offline);
                    }
                }
                break;
        }
    }

    private static DeviceType MapDeviceType(LGSTrayPrimitives.DeviceType type)
    {
        return type switch
        {
            LGSTrayPrimitives.DeviceType.Keyboard => DeviceType.Keyboard,
            LGSTrayPrimitives.DeviceType.Mouse => DeviceType.Mouse,
            LGSTrayPrimitives.DeviceType.Headset => DeviceType.Headset,
            _ => DeviceType.Generic
        };
    }

    private static PowerStatus MapPowerStatus(LGSTrayPrimitives.PowerSupplyStatus status)
    {
        return status switch
        {
            LGSTrayPrimitives.PowerSupplyStatus.POWER_SUPPLY_STATUS_DISCHARGING => PowerStatus.Discharging,
            LGSTrayPrimitives.PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING => PowerStatus.Charging,
            LGSTrayPrimitives.PowerSupplyStatus.POWER_SUPPLY_STATUS_FULL => PowerStatus.Full,
            LGSTrayPrimitives.PowerSupplyStatus.POWER_SUPPLY_STATUS_NOT_CHARGING => PowerStatus.Wired,
            _ => PowerStatus.Unknown
        };
    }
}
