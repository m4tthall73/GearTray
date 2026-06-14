using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GearTray.Contracts;
using GearTrayUI;
using Moq;
using Xunit;

namespace GearTray.Tests
{
    public class PluginCoordinatorTests : IDisposable
    {
        private readonly string _tempConfigFile;
        private readonly Mock<IDevicePlugin> _mockPlugin;
        private readonly List<IDevicePlugin> _plugins;

        public PluginCoordinatorTests()
        {
            // Set up a unique temp file for configuration
            _tempConfigFile = Path.Combine(Path.GetTempPath(), $"geartray_test_config_{Guid.NewGuid()}.json");
            PluginCoordinator.ConfigPathOverride = _tempConfigFile;

            _mockPlugin = new Mock<IDevicePlugin>();
            _mockPlugin.Setup(p => p.PluginId).Returns("TestPlugin");
            _mockPlugin.Setup(p => p.DisplayName).Returns("Mocked Plugin");
            _mockPlugin.Setup(p => p.GetActiveDevices()).Returns(Enumerable.Empty<DeviceStatusEventArgs>());

            _plugins = new List<IDevicePlugin> { _mockPlugin.Object };
        }

        public void Dispose()
        {
            PluginCoordinator.ConfigPathOverride = null;
            if (File.Exists(_tempConfigFile))
            {
                try { File.Delete(_tempConfigFile); } catch { }
            }
        }

        [Fact]
        public void Initialize_LoadsAndSavesConfig()
        {
            // Arrange
            var coordinator = new PluginCoordinator(_plugins);

            // Act - Load initial (should be default)
            coordinator.Initialize();
            Assert.True(coordinator.ShowOfflineDevices);
            Assert.Equal(15, coordinator.GlobalBatteryThreshold);

            // Change values and save
            coordinator.ShowOfflineDevices = false;
            coordinator.GlobalBatteryThreshold = 25;
            coordinator.Shutdown();

            // Assert file exists
            Assert.True(File.Exists(_tempConfigFile));

            // Reload and verify
            var newCoordinator = new PluginCoordinator(_plugins);
            newCoordinator.Initialize();
            Assert.False(newCoordinator.ShowOfflineDevices);
            Assert.Equal(25, newCoordinator.GlobalBatteryThreshold);
            newCoordinator.Shutdown();
        }

        [Fact]
        public void DeviceCustomName_SavesAndRetrieves()
        {
            var coordinator = new PluginCoordinator(_plugins);
            coordinator.Initialize();

            // Act
            coordinator.SetDeviceCustomName("dev-mouse", "Super Mouse");

            // Assert
            Assert.Equal("Super Mouse", coordinator.GetDeviceCustomName("dev-mouse"));

            // Re-load coordinator to verify persistence
            coordinator.Shutdown();
            var newCoordinator = new PluginCoordinator(_plugins);
            newCoordinator.Initialize();

            Assert.Equal("Super Mouse", newCoordinator.GetDeviceCustomName("dev-mouse"));
            newCoordinator.Shutdown();
        }

        [Fact]
        public void BatteryThresholdOverrides_AndAlerts()
        {
            // Arrange
            var notifications = new List<(string Title, string Message)>();
            var coordinator = new PluginCoordinator(_plugins);
            coordinator.OnRaiseNotification += (title, msg) => notifications.Add((title, msg));

            coordinator.Initialize();
            coordinator.GlobalBatteryThreshold = 20;

            // Set custom threshold override for dev-override
            coordinator.SetDeviceUseDefaultThreshold("dev-override", false);
            coordinator.SetDeviceThreshold("dev-override", 12);

            // Verify thresholds
            Assert.Equal(12, coordinator.GetDeviceThreshold("dev-override"));
            Assert.Equal(20, coordinator.GetDeviceThreshold("dev-default"));

            // Raise status change for dev-override at 15% (which is above 12% but below global 20%)
            var devOverride15 = new DeviceStatusEventArgs("dev-override", "Test Device Override", DeviceType.Mouse, 15, PowerStatus.Discharging, true);
            _mockPlugin.Raise(p => p.DeviceStatusChanged += null, _mockPlugin.Object, devOverride15);

            // Assert: No notification since 15 > 12
            Assert.Empty(notifications);

            // Raise status change for dev-override at 10% (which is below 12%)
            var devOverride10 = new DeviceStatusEventArgs("dev-override", "Test Device Override", DeviceType.Mouse, 10, PowerStatus.Discharging, true);
            _mockPlugin.Raise(p => p.DeviceStatusChanged += null, _mockPlugin.Object, devOverride10);

            // Assert: Notification raised
            Assert.Single(notifications);
            Assert.Equal("Low Battery Alert", notifications[0].Title);
            Assert.Contains("Test Device Override", notifications[0].Message);
            Assert.Contains("10%", notifications[0].Message);

            notifications.Clear();

            // Raise status change for dev-default at 18% (which is below global 20%)
            var devDefault18 = new DeviceStatusEventArgs("dev-default", "Test Device Default", DeviceType.Keyboard, 18, PowerStatus.Discharging, true);
            _mockPlugin.Raise(p => p.DeviceStatusChanged += null, _mockPlugin.Object, devDefault18);

            // Assert: Notification raised since using default threshold (20)
            Assert.Single(notifications);
            Assert.Equal("Low Battery Alert", notifications[0].Title);
            Assert.Contains("Test Device Default", notifications[0].Message);
            Assert.Contains("18%", notifications[0].Message);

            coordinator.Shutdown();
        }
    }
}
