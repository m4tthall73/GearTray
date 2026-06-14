# GearTray

GearTray is a unified, lightweight, and pluggable desktop utility designed to monitor peripheral statuses (like battery levels) and control system settings (like audio volume and mute states) directly from the Windows system tray. 

---

## 1. What is GearTray & Why Do We Need It?

### The Problem
Most modern PC peripherals require heavy, proprietary manufacturer software suites (such as Logitech G Hub or Razer Synapse) just to check battery status, adjust basic features, or control microphone mute status. These suites consume significant system resources, require constant internet access, and do not integrate with each other.

### The Solution
GearTray consolidates peripheral management into a single, resource-efficient Windows desktop application. 
- It communicates directly with hardware endpoints via native APIs (like Logitech HID++ and Windows WASAPI).
- It provides a **pluggable architecture** so that support for new peripherals (e.g., Razer microphones, Wolverine controllers, or wireless headsets) can be added without modifying the core application or UI.
- It delivers a clean, unified system tray interface that shows battery status meters and volume/control sliders at a glance.

---

## 2. Combined Legacy Applications

GearTray was constructed by merging and refactoring logic from two separate repositories:

1. **PowerTray** (C# Logitech Battery Monitor):
   - Provided raw Logitech HID++ communication logic to read battery percentages and charging states via USB receiver and Bluetooth endpoints.
   - Refactored from an out-of-process client-server IPC model to an **in-process** plugin model for enhanced reliability.
2. **AudioOS** (C++ Audio Control / Default Endpoint Router):
   - Provided WASAPI and audio endpoint management.
   - Heavy C++ Win32 message loop/GDI+ code was migrated to a clean C# audio plugin utilizing **NAudio** to query and control master volume and mute statuses directly.
   - **SteelSeries Headset Power Status Workaround**: Integrated the C++ headset polling logic for SteelSeries Arctis Nova 7/7X headsets. Because the Windows audio subsystem sees the always-plugged-in USB transmitter as permanently active (keeping the default audio endpoint marked "Online" even when the headset is powered off), the audio plugin runs a background polling thread. It communicates directly with the physical headset using `hidapi.dll` over Vendor ID `0x1038` to query the physical power state and battery levels, enabling accurate auto-switching.

---

## 3. Pluggable Architecture Design

The core architectural philosophy of GearTray is strict decoupling. The UI does not know anything about specific hardware; it only talks to plugins via standard contracts.

### Core Contracts (`GearTray.Contracts`)
- **`IDevicePlugin`**: The standard interface that all plugins must implement.
  ```csharp
  public interface IDevicePlugin
  {
      string PluginId { get; }
      string DisplayName { get; }
      void Initialize();
      void Shutdown();
      event EventHandler<DeviceStatusEventArgs>? DeviceStatusChanged;
      IEnumerable<DeviceStatusEventArgs> GetActiveDevices();
  }
  ```
- **`DeviceStatusEventArgs`**: The payload containing device identification, battery percentage, online status, and a list of bindable controls.
- **`DeviceControl`**: A schema representation of an interactive control (e.g., a Slider for volume or a Toggle for mute status) that the plugin exposes. The UI dynamically builds visual controls for these on the fly.

### Active Projects
- [GearTray.Contracts](file:///d:/Projects/GearTray/GearTray.Contracts): Interface definitions and shared schemas.
- [GearTray.Plugins.Audio](file:///d:/Projects/GearTray/GearTray.Plugins.Audio): Interfaces with Windows WASAPI via NAudio to manage default audio endpoints, master volume, and mute states.
- [GearTray.Plugins.Logitech](file:///d:/Projects/GearTray/GearTray.Plugins.Logitech): Enumerates USB receivers and queries Logitech keyboards, mice, and headsets directly via HID++ protocol commands over `hidapi.dll`.
- [GearTray.Plugins.Razer](file:///d:/Projects/GearTray/GearTray.Plugins.Razer): Interfaces with Razer microphones (like Seiren) using WASAPI/NAudio to query and control mute status and gain level, subscribing to volume notifications for hardware state synchronization.
- [GearTrayUI](file:///d:/Projects/GearTray/GearTrayUI): The WPF user interface. Coordinates registered plugins via `PluginCoordinator`, instantiates the Hardcodet `TaskbarIcon`, and renders the dynamic system tray context menu.
- [GearTray.Tests](file:///d:/Projects/GearTray/GearTray.Tests): The xUnit & Moq testing suite that validates event logs, state transitions, config loading/saving, and battery alerts.

---

## 4. Constructing the UI & Backend

### In-Process HID++ Refactoring
In the original `PowerTray`, the HID scanning ran as a separate `PowerTrayHID.exe` daemon and communicated with the UI via `MessagePipe.Interprocess` IPC. In `GearTray`, we ported the native `hidapi.dll` imports and battery codecs to run **in-process** inside `GearTray.Plugins.Logitech`. This eliminated subprocess spawning, parent-process-exit checking, and heavy serialization layers.

### Dynamic WPF Context Menu
Instead of rendering static menus, `TrayResources.xaml` dynamically binds to `App.Coordinator.ActiveDevices`.
- Custom value converters (`Converters.cs`) handle visibility triggers (e.g. hiding battery bars if a device lacks a battery).
- Data templates automatically render device-specific controls. Sliders bind directly to control actions, allowing users to scroll volume or toggle check boxes with zero lag.
- Close behaviors are overridden in `MainWindow.xaml.cs` to hide the window instead of killing the process, keeping the tray icon active in the background.

---

## 5. Running Unit Tests

To run the automated tests, open a terminal in the root of the workspace and run:

```bash
dotnet test
```

This will run all xUnit test classes validating:
* `EventLogger` reverse chronological buffering, bounds limits (500 entries), and auto-detecting categories/colors.
* `LogitechPlugin` status event deduplication and online-offline state recovery.
* `PluginCoordinator` settings persistence, custom device names, and battery alert threshold checks.
* `RazerPlugin` device detection filtering, controls mapping, and volume/mute deduplication.

---

## 6. Acknowledgements

We want to thank the developers of the original applications and libraries that made this integration possible:
- **`PowerTray`** (by [JumpTwiceShou](https://github.com/JumpTwiceShou/PowerTray)) and the upstream **`LGSTrayBattery`** (by [andyvorld](https://github.com/andyvorld/LGSTrayBattery)), which provided the excellent HID++ implementation.
- **`Solaar`** (by [pwr-Solaar](https://github.com/pwr-Solaar/Solaar)) for reverse-engineering references regarding Logitech's HID++ protocol features.
- **`NAudio`** (by Mark Heath) for providing standard WASAPI wrappers in C#.
- **`Hardcodet.NotifyIcon.Wpf`** (by Philipp Sumi) for the WPF TaskbarIcon implementation.

---

## 7. What is Remaining (Roadmap)

Future developers or agents should focus on the following items to extend the utility:
1. **Additional Device Plugins**: 
   - **Wolverine Controller Plugin**: Monitor connection status and battery profile.
2. **Logitech Key-Press Wake**: Incorporate receiver wake packet tracking to trigger faster battery updates when keyboard keys are pressed (since Windows locks keyboard HID paths by default).
3. **Bluetooth LE Battery Monitoring**: Extend discovery patterns to capture standard Bluetooth LE battery profile reports.

