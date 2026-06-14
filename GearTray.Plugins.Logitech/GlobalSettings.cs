using System.Collections.Generic;

namespace LGSTrayHID
{
    internal static class GlobalSettings
    {
        public static readonly NativeDeviceManagerSettings settings = new();
    }

    internal class NativeDeviceManagerSettings
    {
        public List<string> DisabledDevices { get; set; } = [];
        public int PollPeriod { get; set; } = 30; // seconds
        public int RetryTime { get; set; } = 5; // seconds
    }
}
