namespace GearTray.Contracts;

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

public class DeviceControl
{
    public string ControlId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ControlType { get; set; } = "Toggle"; // Toggle, Slider, Action
    public double Value { get; set; }
    public Action<double>? OnControlChanged { get; set; }
}

public class DeviceStatusEventArgs : EventArgs
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
