using System;

namespace LGSTrayPrimitives
{
    public enum DeviceType : byte
    {
        Keyboard = 0,
        Mouse = 3,
        Headset = 8,
    }

    public enum PowerSupplyStatus : byte
    {
        POWER_SUPPLY_STATUS_DISCHARGING = 0,
        POWER_SUPPLY_STATUS_CHARGING,
        POWER_SUPPLY_STATUS_FULL,
        POWER_SUPPLY_STATUS_NOT_CHARGING,
        POWER_SUPPLY_STATUS_UNKNOWN
    }
}

namespace LGSTrayPrimitives.MessageStructs
{
    public enum IPCMessageType : byte
    {
        HEARTBEAT = 0,
        INIT,
        UPDATE,
        OFFLINE,
        NATIVE_DIAGNOSTICS_RESPONSE
    }

    public abstract class IPCMessage(string deviceId)
    {
        public string deviceId = deviceId;
    }

    public class InitMessage(string deviceId, string deviceName, bool hasBattery, DeviceType deviceType) : IPCMessage(deviceId)
    {
        public string deviceName = deviceName;
        public bool hasBattery = hasBattery;
        public DeviceType deviceType = deviceType;
    }

    public class UpdateMessage(
        string deviceId,
        double batteryPercentage,
        PowerSupplyStatus powerSupplyStatus,
        int batteryMVolt,
        DateTimeOffset updateTime,
        double mileage = -1
    ) : IPCMessage(deviceId)
    {
        public double batteryPercentage = batteryPercentage;
        public PowerSupplyStatus powerSupplyStatus = powerSupplyStatus;
        public int batteryMVolt = batteryMVolt;
        public DateTimeOffset updateTime = updateTime;
        public double Mileage = mileage;
    }

    public class DeviceOfflineMessage(string deviceId) : IPCMessage(deviceId)
    {
    }

    public class NativeDiagnosticsResponseMessage(
        string requestId,
        string diagnosticsJson,
        string summaryText,
        string? error = null
    ) : IPCMessage("native-diagnostics")
    {
        public const string LatestSnapshotRequestId = "latest";
        public string requestId = requestId;
        public string diagnosticsJson = diagnosticsJson;
        public string summaryText = summaryText;
        public string? error = error;
    }
}
