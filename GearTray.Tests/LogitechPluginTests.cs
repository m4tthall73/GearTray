using System;
using System.Collections.Generic;
using GearTray.Contracts;
using GearTray.Plugins.Logitech;
using LGSTrayHID;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using Xunit;

namespace GearTray.Tests
{
    public class LogitechPluginTests : IDisposable
    {
        private readonly LogitechPlugin _plugin;
        private readonly List<DeviceStatusEventArgs> _events = [];

        public LogitechPluginTests()
        {
            _plugin = new LogitechPlugin();
            _plugin.DeviceStatusChanged += OnDeviceStatusChanged;
            _plugin.Initialize();
        }

        public void Dispose()
        {
            _plugin.DeviceStatusChanged -= OnDeviceStatusChanged;
            _plugin.Shutdown();
        }

        private void OnDeviceStatusChanged(object? sender, DeviceStatusEventArgs e)
        {
            _events.Add(e);
        }

        [Fact]
        public void Init_FiresEvent_OnlyOnFirstCall()
        {
            // Arrange
            var deviceId = "test-device-123";
            var initMsg = new InitMessage(deviceId, "Test Mouse", true, LGSTrayPrimitives.DeviceType.Mouse);

            // Act: Fire Init first time
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.INIT, initMsg);

            // Assert first call
            Assert.Single(_events);
            Assert.Equal(deviceId, _events[0].DeviceId);
            Assert.Equal("Test Mouse", _events[0].DisplayName);
            Assert.Equal(GearTray.Contracts.DeviceType.Mouse, _events[0].Type);
            Assert.True(_events[0].IsOnline);
            Assert.Equal(-1, _events[0].BatteryPercentage);

            // Act: Fire Init second time (identical)
            _events.Clear();
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.INIT, initMsg);

            // Assert: No new event fired because wasOnline is true
            Assert.Empty(_events);
        }

        [Fact]
        public void Update_FiltersRedundantEvents()
        {
            // Arrange
            var deviceId = "test-device-456";
            var initMsg = new InitMessage(deviceId, "Test Keyboard", true, LGSTrayPrimitives.DeviceType.Keyboard);
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.INIT, initMsg);
            _events.Clear();

            // Act: Fire first Update (50% discharging)
            var update1 = new UpdateMessage(deviceId, 50.0f, PowerSupplyStatus.POWER_SUPPLY_STATUS_DISCHARGING, 4000, DateTimeOffset.Now, 0);
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.UPDATE, update1);

            // Assert first update
            Assert.Single(_events);
            Assert.Equal(50, _events[0].BatteryPercentage);
            Assert.Equal(PowerStatus.Discharging, _events[0].Power);
            Assert.True(_events[0].IsOnline);

            // Act: Fire redundant second Update (identical state)
            _events.Clear();
            var update2 = new UpdateMessage(deviceId, 50.0f, PowerSupplyStatus.POWER_SUPPLY_STATUS_DISCHARGING, 4000, DateTimeOffset.Now, 0);
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.UPDATE, update2);

            // Assert: Filtered out
            Assert.Empty(_events);

            // Act: Fire third Update with different battery percentage (45% discharging)
            var update3 = new UpdateMessage(deviceId, 45.0f, PowerSupplyStatus.POWER_SUPPLY_STATUS_DISCHARGING, 3950, DateTimeOffset.Now, 0);
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.UPDATE, update3);

            // Assert: Different battery fires event
            Assert.Single(_events);
            Assert.Equal(45, _events[0].BatteryPercentage);
            Assert.Equal(PowerStatus.Discharging, _events[0].Power);

            // Act: Fire fourth Update with different power status (45% charging)
            _events.Clear();
            var update4 = new UpdateMessage(deviceId, 45.0f, PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING, 4100, DateTimeOffset.Now, 0);
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.UPDATE, update4);

            // Assert: Different power status fires event
            Assert.Single(_events);
            Assert.Equal(45, _events[0].BatteryPercentage);
            Assert.Equal(PowerStatus.Charging, _events[0].Power);
        }

        [Fact]
        public void Offline_TransitionsDevice_AndRecoveryWorks()
        {
            // Arrange
            var deviceId = "test-device-789";
            var initMsg = new InitMessage(deviceId, "Test Headset", true, LGSTrayPrimitives.DeviceType.Headset);
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.INIT, initMsg);
            var update1 = new UpdateMessage(deviceId, 80.0f, PowerSupplyStatus.POWER_SUPPLY_STATUS_DISCHARGING, 4000, DateTimeOffset.Now, 0);
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.UPDATE, update1);
            _events.Clear();

            // Act: Transition to Offline
            var offlineMsg = new DeviceOfflineMessage(deviceId);
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.OFFLINE, offlineMsg);

            // Assert offline event
            Assert.Single(_events);
            Assert.Equal(deviceId, _events[0].DeviceId);
            Assert.False(_events[0].IsOnline);
            Assert.Equal(80, _events[0].BatteryPercentage); // retain last known battery

            // Act: Recover device (receive INIT again)
            _events.Clear();
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.INIT, initMsg);

            // Assert recovery INIT fires event
            Assert.Single(_events);
            Assert.True(_events[0].IsOnline);
            Assert.Equal(-1, _events[0].BatteryPercentage); // resets to -1 on INIT since wasOnline = false

            // Act: Receive Update
            _events.Clear();
            var update2 = new UpdateMessage(deviceId, 75.0f, PowerSupplyStatus.POWER_SUPPLY_STATUS_DISCHARGING, 3900, DateTimeOffset.Now, 0);
            HidppManagerContext.Instance.SignalDeviceEvent(IPCMessageType.UPDATE, update2);

            // Assert update works
            Assert.Single(_events);
            Assert.Equal(75, _events[0].BatteryPercentage);
            Assert.True(_events[0].IsOnline);
        }
    }
}
