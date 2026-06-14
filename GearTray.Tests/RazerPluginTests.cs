using System;
using System.Linq;
using GearTray.Contracts;
using GearTray.Plugins.Razer;
using Xunit;

namespace GearTray.Tests
{
    public class RazerPluginTests : IDisposable
    {
        private readonly RazerPlugin _plugin;

        public RazerPluginTests()
        {
            _plugin = new RazerPlugin();
        }

        public void Dispose()
        {
            _plugin.Shutdown();
        }

        [Fact]
        public void PluginMetadata_IsCorrect()
        {
            Assert.Equal("GearTray.Plugins.Razer", _plugin.PluginId);
            Assert.Equal("Razer Microphone Monitor", _plugin.DisplayName);
        }

        [Fact]
        public void Initialize_RunsWithoutCrashing()
        {
            try
            {
                _plugin.Initialize();
                var activeDevices = _plugin.GetActiveDevices();
                Assert.NotNull(activeDevices);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                // Gracefully pass if running in a headless CI environment where Windows Audio service is unavailable
                System.Diagnostics.Debug.WriteLine($"Skipping test assertion due to missing Windows Audio Service: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Other unexpected exceptions should still fail the test
                Assert.Fail($"Unexpected exception: {ex.Message}");
            }
        }
    }
}
