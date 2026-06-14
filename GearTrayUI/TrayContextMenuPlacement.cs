using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace GearTrayUI;

internal static class TrayContextMenuPlacement
{
    public static void Attach(TaskbarIcon taskbarIcon)
    {
        taskbarIcon.PreviewTrayContextMenuOpen += OnPreviewTrayContextMenuOpen;
        taskbarIcon.TrayLeftMouseUp += OnTrayLeftMouseUp;
        taskbarIcon.TrayRightMouseUp += OnTrayRightMouseUp;
    }

    public static void Detach(TaskbarIcon taskbarIcon)
    {
        taskbarIcon.PreviewTrayContextMenuOpen -= OnPreviewTrayContextMenuOpen;
        taskbarIcon.TrayLeftMouseUp -= OnTrayLeftMouseUp;
        taskbarIcon.TrayRightMouseUp -= OnTrayRightMouseUp;
    }

    private static void OnPreviewTrayContextMenuOpen(object sender, RoutedEventArgs e)
    {
        // Cancel the default right-click context menu opening
        e.Handled = true;
    }

    private static void OnTrayLeftMouseUp(object sender, RoutedEventArgs e)
    {
        ShowMenu(sender, showSettingsExit: false);
    }

    private static void OnTrayRightMouseUp(object sender, RoutedEventArgs e)
    {
        ShowMenu(sender, showSettingsExit: true);
    }

    internal static void ShowMenu(object sender, bool showSettingsExit)
    {
        if (sender is not TaskbarIcon taskbarIcon || taskbarIcon.ContextMenu is not { } menu)
        {
            return;
        }

        // Trigger a rediscover and restart the background search timer when menu is opened
        try
        {
            App.Coordinator?.StartDiscoveryTimer();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to restart discovery timer: {ex.Message}");
        }

        // Configure visibility of settings/exit options
        GearTray.Contracts.EventLogger.Log("SYSTEM", $"ShowMenu: showSettingsExit={showSettingsExit}, itemsCount={menu.Items.Count}", "#888888");
        
        UIElement? openSettings = null;
        UIElement? exit = null;
        UIElement? activePeripherals = null;
        UIElement? separator1 = null;
        UIElement? separator2 = null;

        int separatorCount = 0;
        for (int i = 0; i < menu.Items.Count; i++)
        {
            var item = menu.Items[i] as UIElement;
            if (item is MenuItem mi)
            {
                var headerStr = mi.Header?.ToString();
                if (headerStr == "Open Settings")
                {
                    openSettings = mi;
                }
                else if (headerStr == "Exit")
                {
                    exit = mi;
                }
                else
                {
                    activePeripherals = mi;
                }
            }
            else if (item is Separator sep)
            {
                separatorCount++;
                if (separatorCount == 1)
                {
                    separator1 = sep;
                }
                else if (separatorCount == 2)
                {
                    separator2 = sep;
                }
            }
        }

        GearTray.Contracts.EventLogger.Log("SYSTEM", $"Setting menu visibility: showSettingsExit={showSettingsExit}", "#888888");
        if (showSettingsExit)
        {
            if (openSettings != null) openSettings.Visibility = Visibility.Visible;
            if (exit != null) exit.Visibility = Visibility.Visible;
            if (separator1 != null) separator1.Visibility = Visibility.Visible;
            if (activePeripherals != null) activePeripherals.Visibility = Visibility.Collapsed;
            if (separator2 != null) separator2.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (openSettings != null) openSettings.Visibility = Visibility.Collapsed;
            if (exit != null) exit.Visibility = Visibility.Collapsed;
            if (separator1 != null) separator1.Visibility = Visibility.Collapsed;
            if (activePeripherals != null) activePeripherals.Visibility = Visibility.Visible;
            if (separator2 != null) separator2.Visibility = Visibility.Collapsed;
        }

        if (!taskbarIcon.Dispatcher.CheckAccess())
        {
            _ = taskbarIcon.Dispatcher.BeginInvoke(() => OpenAtCursor(menu));
            return;
        }

        OpenAtCursor(menu);
    }

    private static void OpenAtCursor(ContextMenu menu)
    {
        if (menu.IsOpen)
        {
            menu.IsOpen = false;
        }

        CursorPlacementMetrics metrics = GetCursorPlacementMetrics();

        menu.PlacementTarget = null;
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = metrics.Cursor.X;
        menu.VerticalOffset = metrics.Cursor.Y;
        menu.IsOpen = true;

        SetForegroundWindow(menu);
    }

    private static CursorPlacementMetrics GetCursorPlacementMetrics()
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
        {
            return new CursorPlacementMetrics(new Point(0, 0), SystemParameters.WorkArea);
        }

        double dpiX = 96;
        double dpiY = 96;
        NativeMethods.RECT workArea = default;
        bool hasWorkArea = false;

        try
        {
            IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint x, out uint y) == 0 &&
                    x > 0 &&
                    y > 0)
                {
                    dpiX = x;
                    dpiY = y;
                }

                NativeMethods.MONITORINFO monitorInfo = new()
                {
                    cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
                };
                if (NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
                {
                    workArea = monitorInfo.rcWork;
                    hasWorkArea = true;
                }
            }
        }
        catch
        {
            dpiX = 96;
            dpiY = 96;
            hasWorkArea = false;
        }

        double scaleX = 96.0 / dpiX;
        double scaleY = 96.0 / dpiY;
        Point cursorInDips = new(cursor.X * scaleX, cursor.Y * scaleY);
        Rect workAreaInDips = hasWorkArea
            ? new Rect(
                workArea.Left * scaleX,
                workArea.Top * scaleY,
                (workArea.Right - workArea.Left) * scaleX,
                (workArea.Bottom - workArea.Top) * scaleY)
            : SystemParameters.WorkArea;

        return new CursorPlacementMetrics(cursorInDips, workAreaInDips);
    }

    private static void SetForegroundWindow(ContextMenu menu)
    {
        try
        {
            if (PresentationSource.FromVisual(menu) is HwndSource { Handle: { } handle } &&
                handle != IntPtr.Zero)
            {
                _ = NativeMethods.SetForegroundWindow(handle);
                return;
            }

            IntPtr mainHandle = Process.GetCurrentProcess().MainWindowHandle;
            if (mainHandle != IntPtr.Zero)
            {
                _ = NativeMethods.SetForegroundWindow(mainHandle);
            }
        }
        catch
        {
            // ContextMenu still works without foreground promotion
        }
    }

    private static class NativeMethods
    {
        internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        internal const int MDT_EFFECTIVE_DPI = 0;

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
    }

    private readonly record struct CursorPlacementMetrics(Point Cursor, Rect WorkArea);
}
