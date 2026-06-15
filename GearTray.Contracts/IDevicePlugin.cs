using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GearTray.Contracts;

public class DeviceControl : INotifyPropertyChanged
{
    public string ControlId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ControlType { get; set; } = "Toggle"; // Toggle, Slider, Action
    
    private double _value;
    public double Value
    {
        get => _value;
        set
        {
            if (Math.Abs(_value - value) > 0.0001)
            {
                _value = value;
                OnPropertyChanged();
            }
        }
    }
    
    public Action<double>? OnControlChanged { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum DeviceType
{
    Mouse,
    Keyboard,
    Headset,
    AudioOutput,
    Microphone,
    Controller,
    Generic
}

public enum PowerStatus
{
    Unknown,
    Discharging,
    Charging,
    Full,
    BatteryLow,
    Wired,
    PoweredOff
}


public class DeviceStatusEventArgs : EventArgs, INotifyPropertyChanged
{
    public string DeviceId { get; }
    public string DisplayName { get; }
    public DeviceType Type { get; }
    public int BatteryPercentage { get; } // -1 if not applicable
    public PowerStatus Power { get; }
    public bool IsOnline { get; }
    public List<DeviceControl> Controls { get; }
    public bool IsDefault { get; } // Identify active default audio render endpoints

    public string Category => (Type == DeviceType.Keyboard || Type == DeviceType.Mouse || Type == DeviceType.Controller)
        ? "Input Devices"
        : "Sound Devices";

    public DeviceStatusEventArgs(
        string deviceId,
        string displayName,
        DeviceType type,
        int batteryPercentage,
        PowerStatus power,
        bool isOnline,
        List<DeviceControl>? controls = null,
        bool isDefault = false)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        Type = type;
        BatteryPercentage = batteryPercentage;
        Power = power;
        IsOnline = isOnline;
        Controls = controls ?? [];
        IsDefault = isDefault;
    }

    public bool IsMuted
    {
        get
        {
            if (Controls == null) return false;
            for (int i = 0; i < Controls.Count; i++)
            {
                var c = Controls[i];
                if (c.DisplayName != null && c.DisplayName.Equals("Mute", StringComparison.OrdinalIgnoreCase) && c.Value > 0.5)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public void RaiseMuteChanged()
    {
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public interface IDevicePlugin
{
    string PluginId { get; }
    string DisplayName { get; }
    void Initialize();
    void Shutdown();
    event EventHandler<DeviceStatusEventArgs>? DeviceStatusChanged;
    IEnumerable<DeviceStatusEventArgs> GetActiveDevices();
}
