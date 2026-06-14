using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using GearTray.Contracts;
using GearTray.Plugins.Audio;
using GearTray.Plugins.Logitech;

namespace GearTrayUI;

public partial class App : Application
{
    private static IHost? _host;
    public static PluginCoordinator? Coordinator { get; private set; }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var builder = Host.CreateDefaultBuilder(e.Args);

        builder.ConfigureServices((context, services) =>
        {
            // Register UI components
            services.AddSingleton<MainWindow>();

            // Register device plugins
            services.AddSingleton<IDevicePlugin, AudioPlugin>();
            services.AddSingleton<IDevicePlugin, LogitechPlugin>();
            
            // Register a coordinator service that manages loaded plugins
            services.AddSingleton<PluginCoordinator>();
        });

        _host = builder.Build();
        _host.Start();

        // Initialize the coordinator to start scanning/monitoring devices
        Coordinator = _host.Services.GetRequiredService<PluginCoordinator>();
        Coordinator.Initialize();

        // Apply Windows Personalization colors and light/dark theme
        ApplyWindowsTheme();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Instantiate MainWindow to initialize the tray icon, but do not show it on startup
        _ = _host.Services.GetRequiredService<MainWindow>();
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General || 
            e.Category == Microsoft.Win32.UserPreferenceCategory.Color)
        {
            Dispatcher.Invoke(() => ApplyWindowsTheme());
        }
    }

    private static System.Windows.Media.Color GetWindowsAccentColor()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key != null)
            {
                var val = key.GetValue("AccentColor");
                if (val is int accentColorDword)
                {
                    // The color is in ABGR format: 0xAABBGGRR
                    byte r = (byte)(accentColorDword & 0xFF);
                    byte g = (byte)((accentColorDword >> 8) & 0xFF);
                    byte b = (byte)((accentColorDword >> 16) & 0xFF);
                    byte a = 255; // Force opaque for accent UI elements
                    return System.Windows.Media.Color.FromArgb(a, r, g, b);
                }
            }
        }
        catch
        {
            // fallback
        }
        return SystemParameters.WindowGlassColor;
    }

    public static void ApplyWindowsTheme()
    {
        bool isLightTheme = false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Personalize");
            if (key == null)
            {
                // Try alternate path
                using var keyAlt = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (keyAlt != null)
                {
                    var val = keyAlt.GetValue("AppsUseLightTheme");
                    if (val is int i && i == 1)
                    {
                        isLightTheme = true;
                    }
                }
            }
            else
            {
                var val = key.GetValue("AppsUseLightTheme");
                if (val is int i && i == 1)
                {
                    isLightTheme = true;
                }
            }
        }
        catch
        {
            // fallback
        }

        var accentColor = GetWindowsAccentColor();
        var resources = Application.Current.Resources;
        resources["AccentBrush"] = new System.Windows.Media.SolidColorBrush(accentColor);

        if (isLightTheme)
        {
            resources["WindowBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3));
            resources["SurfaceBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
            resources["TextBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x00, 0x00));
            resources["MutedTextBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6E, 0x6E, 0x6E));
            resources["BorderBrushSoft"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD5, 0xD5, 0xD5));
        }
        else
        {
            resources["WindowBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1C));
            resources["SurfaceBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0x28, 0x28));
            resources["TextBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
            resources["MutedTextBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA0, 0xA0, 0xA0));
            resources["BorderBrushSoft"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3C, 0x3C, 0x3C));
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        if (_host != null)
        {
            var coordinator = _host.Services.GetService<PluginCoordinator>();
            coordinator?.Shutdown();

            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
