using System;
using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using System.Linq;
using GearTray.Contracts;

namespace GearTrayUI;

public partial class MainWindow : Window
{
    private readonly System.Collections.Generic.Dictionary<string, DateTime> _lastSliderUpdateTimes = new();
    private readonly System.Collections.Generic.Dictionary<string, System.Windows.Threading.DispatcherTimer> _sliderDeferredTimers = new();

    public ObservableCollection<LogEntry> EventsLog { get; } = [];
    public ObservableCollection<DeviceViewModel> DevicesList { get; } = [];
    private readonly ICollectionView _eventsView;
    public ICollectionView OverriddenDevicesView { get; }

    public MainWindow()
    {
        InitializeComponent();
        TrayContextMenuPlacement.Attach(NotifyIcon);
        this.Loaded += MainWindow_Loaded;
        EventLogger.OnLogEntry += OnApplicationEventLog;
        
        if (App.Coordinator != null)
        {
            App.Coordinator.OnRaiseNotification += RaiseNotification;
            App.Coordinator.DeviceListChanged += OnDeviceListChanged;
        }
        
        _eventsView = CollectionViewSource.GetDefaultView(EventsLog);
        _eventsView.Filter = FilterEvents;

        var overriddenDevicesSource = new CollectionViewSource { Source = DevicesList };
        OverriddenDevicesView = overriddenDevicesSource.View;
        OverriddenDevicesView.Filter = (obj) =>
        {
            if (obj is DeviceViewModel vm)
            {
                return !vm.UseDefaultThreshold || vm.IsPaused;
            }
            return false;
        };
        
        this.DataContext = this;
    }

    private bool FilterEvents(object obj)
    {
        if (obj is LogEntry log)
        {
            if (LogFilterComboBox == null) return true;
            var selected = (LogFilterComboBox.SelectedItem as ComboBoxItem)?.Content.ToString();
            if (string.IsNullOrEmpty(selected) || selected == "All") return true;

            if (selected == "Audio")
            {
                return log.Category == "AUDIO_SWITCH" || log.Message.Contains("Audio") || log.Message.Contains("SetDefaultEndpoint");
            }
            if (selected == "Logitech")
            {
                return log.Message.Contains("Logitech") || log.Message.Contains("Wireless Mouse") || log.Message.Contains("Keyboard") || log.Message.Contains("MX Master") || log.Message.Contains("K850");
            }
            if (selected == "System")
            {
                return log.Category == "SYSTEM" || log.Message.Contains("timer") || log.Message.Contains("Config") || log.Message.Contains("Cache") || log.Message.Contains("Coordinator");
            }
        }
        return false;
    }

    private void LogFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _eventsView?.Refresh();
    }

    private void OnApplicationEventLog(LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (EventsLoggingCheckbox?.IsChecked == true)
            {
                EventsLog.Insert(0, entry);
                if (EventsLog.Count > 500)
                {
                    EventsLog.RemoveAt(EventsLog.Count - 1);
                }
            }
        });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        EventsLog.Clear();
        EventLogger.Clear();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = string.Join(System.Environment.NewLine, EventsLog.Select(x => x.ToString()));
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to copy logs to clipboard: {ex.Message}");
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Replay any buffered startup logs to the UI console
        EventsLog.Clear();
        foreach (var log in EventLogger.GetHistory())
        {
            EventsLog.Insert(0, log);
        }

        PopulateDevices();

        if (App.Coordinator != null)
        {
            ShowOfflineCheckbox.IsChecked = App.Coordinator.ShowOfflineDevices;
            AutoSwitchCheckbox.IsChecked = App.Coordinator.AutoSwitchHeadphones;
            AutoSwitchMicCheckbox.IsChecked = App.Coordinator.AutoSwitchMicrophone;
            PauseFullScreenCheckbox.IsChecked = App.Coordinator.PauseNotificationOnFullScreen;
        }

    }

    private void PopulateDevices()
    {
        DevicesList.Clear();
        if (App.Coordinator != null)
        {
            var cached = App.Coordinator.GetCachedDevices().ToList();
            var orderedCached = cached
                .Select(c => {
                    var dev = App.Coordinator.ActiveDevices.FirstOrDefault(d => d.DeviceId == c.DeviceId);
                    bool isDefault = dev?.IsDefault ?? false;
                    bool isOnline = dev?.IsOnline ?? false;
                    return new { Cache = c, IsDefault = isDefault, IsOnline = isOnline };
                })
                .OrderByDescending(x => x.IsDefault)
                .ThenByDescending(x => x.IsOnline)
                .ThenBy(x => x.Cache.DisplayName)
                .Select(x => x.Cache)
                .ToList();

            EventLogger.Log("SYSTEM", $"Populating Devices tab. Cached devices count: {orderedCached.Count}", "#888888");
            foreach (var cache in orderedCached)
            {
                DevicesList.Add(new DeviceViewModel
                {
                    DeviceId = cache.DeviceId,
                    OriginalName = cache.DisplayName,
                    Type = cache.Type
                });
            }
        }
        else
        {
            EventLogger.Log("SYSTEM", "App.Coordinator is null during PopulateDevices!", "#C62828");
        }
        OverriddenDevicesView.Refresh();
    }

    private void OnDeviceListChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (App.Coordinator != null)
            {
                var cached = App.Coordinator.GetCachedDevices().ToList();
                var orderedCached = cached
                    .Select(c => {
                        var dev = App.Coordinator.ActiveDevices.FirstOrDefault(d => d.DeviceId == c.DeviceId);
                        bool isDefault = dev?.IsDefault ?? false;
                        bool isOnline = dev?.IsOnline ?? false;
                        return new { Cache = c, IsDefault = isDefault, IsOnline = isOnline };
                    })
                    .OrderByDescending(x => x.IsDefault)
                    .ThenByDescending(x => x.IsOnline)
                    .ThenBy(x => x.Cache.DisplayName)
                    .Select(x => x.Cache)
                    .ToList();

                bool needsRepopulate = orderedCached.Count != DevicesList.Count;
                if (!needsRepopulate)
                {
                    for (int i = 0; i < orderedCached.Count; i++)
                    {
                        if (DevicesList[i].DeviceId != orderedCached[i].DeviceId || DevicesList[i].Type != orderedCached[i].Type)
                        {
                            needsRepopulate = true;
                            break;
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"OnDeviceListChanged: cached={orderedCached.Count}, DevicesList={DevicesList.Count}, needsRepopulate={needsRepopulate}");

                if (needsRepopulate)
                {
                    PopulateDevices();
                }
                else
                {
                    foreach (var vm in DevicesList)
                    {
                        vm.RefreshDynamicStatus();
                    }
                    OverriddenDevicesView.Refresh();
                }
            }
        });
    }

    private void RaiseNotification(string title, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            NotifyIcon.ShowBalloonTip(title, message, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
        });
    }

    private void TestAlert_Click(object sender, RoutedEventArgs e)
    {
        App.Coordinator?.RaiseTestAlert();
    }

    private void ShowOffline_Changed(object sender, RoutedEventArgs e)
    {
        if (App.Coordinator != null && ShowOfflineCheckbox.IsChecked.HasValue)
        {
            App.Coordinator.ShowOfflineDevices = ShowOfflineCheckbox.IsChecked.Value;
        }
    }

    private void AutoSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (App.Coordinator != null && AutoSwitchCheckbox.IsChecked.HasValue)
        {
            App.Coordinator.AutoSwitchHeadphones = AutoSwitchCheckbox.IsChecked.Value;
        }
    }

    private void AutoSwitchMic_Changed(object sender, RoutedEventArgs e)
    {
        if (App.Coordinator != null && AutoSwitchMicCheckbox.IsChecked.HasValue)
        {
            App.Coordinator.AutoSwitchMicrophone = AutoSwitchMicCheckbox.IsChecked.Value;
        }
    }

    private void PauseFullScreen_Changed(object sender, RoutedEventArgs e)
    {
        if (App.Coordinator != null && PauseFullScreenCheckbox.IsChecked.HasValue)
        {
            App.Coordinator.PauseNotificationOnFullScreen = PauseFullScreenCheckbox.IsChecked.Value;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Intercept close and hide the window instead
        e.Cancel = true;
        this.Visibility = Visibility.Collapsed;
        base.OnClosing(e);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (this.Visibility == Visibility.Visible)
        {
            this.Activate();
        }
        else
        {
            this.Show();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        // Make sure we dispose the notify icon before shutting down
        NotifyIcon?.Dispose();
        Application.Current.Shutdown();
    }

    private void Control_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is DeviceControl ctrl)
        {
            bool isChecked = cb.IsChecked == true;
            double val = isChecked ? 1.0 : 0.0;
            System.Diagnostics.Debug.WriteLine($"Control_Changed: '{ctrl.DisplayName}' (isChecked={isChecked}, ctrl.Value={ctrl.Value}, IsLoaded={cb.IsLoaded})");
            if (cb.IsLoaded && Math.Abs(ctrl.Value - val) > 0.01)
            {
                ctrl.Value = val;
                ctrl.OnControlChanged?.Invoke(val);

                // Trigger icon updates immediately
                if (ctrl.DisplayName.Equals("Mute", StringComparison.OrdinalIgnoreCase))
                {
                    var coordinator = App.Coordinator;
                    if (coordinator != null)
                    {
                        var dev = coordinator.ActiveDevices.FirstOrDefault(d => d.Controls.Contains(ctrl));
                        if (dev != null)
                        {
                            dev.RaiseMuteChanged(); // Refresh popup icon

                            var mainWindow = Application.Current.MainWindow as MainWindow;
                            var vm = mainWindow?.DevicesList.FirstOrDefault(v => v.DeviceId == dev.DeviceId);
                            vm?.RefreshDynamicStatus(); // Refresh Devices tab icon
                        }
                    }
                }
            }
        }
    }

    private void ThrottleControlChange(DeviceControl ctrl, double value)
    {
        string key = ctrl.ControlId;
        DateTime now = DateTime.UtcNow;

        if (!_lastSliderUpdateTimes.TryGetValue(key, out var lastTime))
        {
            lastTime = DateTime.MinValue;
        }

        // Clean up any existing deferred timer
        if (_sliderDeferredTimers.TryGetValue(key, out var timer))
        {
            timer.Stop();
            _sliderDeferredTimers.Remove(key);
        }

        double elapsed = (now - lastTime).TotalMilliseconds;
        if (elapsed >= 150)
        {
            // Update immediately
            _lastSliderUpdateTimes[key] = now;
            ctrl.OnControlChanged?.Invoke(value);
        }
        else
        {
            // Defer update
            var newTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150 - elapsed)
            };
            newTimer.Tick += (s, ev) =>
            {
                newTimer.Stop();
                if (_sliderDeferredTimers.TryGetValue(key, out var activeTimer) && activeTimer == newTimer)
                {
                    _sliderDeferredTimers.Remove(key);
                }
                _lastSliderUpdateTimes[key] = DateTime.UtcNow;
                ctrl.OnControlChanged?.Invoke(value);
            };
            _sliderDeferredTimers[key] = newTimer;
            newTimer.Start();
        }
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider slider && slider.DataContext is DeviceControl ctrl)
        {
            System.Diagnostics.Debug.WriteLine($"Slider_ValueChanged: '{ctrl.DisplayName}' (e.NewValue={e.NewValue}, ctrl.Value={ctrl.Value}, IsLoaded={slider.IsLoaded})");
            if (slider.IsLoaded && Math.Abs(ctrl.Value - e.NewValue) > 0.01)
            {
                ctrl.Value = e.NewValue;
                ThrottleControlChange(ctrl, e.NewValue);
            }
        }
    }

    private void Slider_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        System.Media.SystemSounds.Beep.Play();
        
        // Force the immediate final value update on mouse up to make sure it's 100% applied
        if (sender is Slider slider && slider.DataContext is DeviceControl ctrl)
        {
            string key = ctrl.ControlId;
            if (_sliderDeferredTimers.TryGetValue(key, out var timer))
            {
                timer.Stop();
                _sliderDeferredTimers.Remove(key);
            }
            _lastSliderUpdateTimes[key] = DateTime.UtcNow;
            ctrl.OnControlChanged?.Invoke(slider.Value);
        }
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is DeviceControl ctrl)
        {
            System.Diagnostics.Debug.WriteLine($"Button_Click: '{ctrl.DisplayName}' (Action control clicked)");
            ctrl.OnControlChanged?.Invoke(1.0);
        }
    }

    private void ListBoxItem_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            if (item.IsSelected)
            {
                // Walk up the visual tree to see if the click was inside the DetailsPanel
                DependencyObject? obj = e.OriginalSource as DependencyObject;
                bool insideDetails = false;
                while (obj != null && obj != item)
                {
                    if (obj is FrameworkElement fe && fe.Name == "DetailsPanel")
                    {
                        insideDetails = true;
                        break;
                    }
                    obj = System.Windows.Media.VisualTreeHelper.GetParent(obj);
                }

                if (!insideDetails)
                {
                    item.IsSelected = false;
                    e.Handled = true;
                }
            }
        }
    }
}

public class DeviceViewModel : INotifyPropertyChanged
{
    public string DeviceId { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public DeviceType Type { get; set; }

    public string CustomName
    {
        get => App.Coordinator?.GetDeviceCustomName(DeviceId) ?? string.Empty;
        set
        {
            if (App.Coordinator != null)
            {
                App.Coordinator.SetDeviceCustomName(DeviceId, value);
                OnPropertyChanged(nameof(CustomName));
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string DisplayName => string.IsNullOrEmpty(CustomName) ? OriginalName : CustomName;
 
    public string Category => (Type == DeviceType.Keyboard || Type == DeviceType.Mouse || Type == DeviceType.Controller)
        ? "Input Devices"
        : "Sound Devices";

    public string ActiveStatusText => (Type == DeviceType.Microphone) ? "Active Recording Device" : "Active Playback Device";
    public Geometry? IconGeometry => new DeviceTypeToIconConverter().Convert(this, typeof(Geometry), null, System.Globalization.CultureInfo.InvariantCulture) as Geometry;

    public bool UseDefaultThreshold
    {
        get => App.Coordinator?.GetDeviceUseDefaultThreshold(DeviceId) ?? true;
        set
        {
            if (App.Coordinator != null)
            {
                App.Coordinator.SetDeviceUseDefaultThreshold(DeviceId, value);
                OnPropertyChanged(nameof(UseDefaultThreshold));
                OnPropertyChanged(nameof(Threshold));
                OnPropertyChanged(nameof(IsSliderEnabled));
                // Signal change to refresh the Alerts tab view
                ((MainWindow)Application.Current.MainWindow)?.OverriddenDevicesView?.Refresh();
            }
        }
    }

    public int Threshold
    {
        get => App.Coordinator?.GetDeviceThreshold(DeviceId) ?? 15;
        set
        {
            if (App.Coordinator != null)
            {
                App.Coordinator.SetDeviceThreshold(DeviceId, value);
                OnPropertyChanged(nameof(Threshold));
            }
        }
    }

    public bool IsSliderEnabled => !UseDefaultThreshold;

    public bool IsOnline
    {
        get
        {
            var dev = App.Coordinator?.ActiveDevices.FirstOrDefault(d => d.DeviceId == DeviceId);
            return dev?.IsOnline ?? false;
        }
    }

    public PowerStatus Power
    {
        get
        {
            var dev = App.Coordinator?.ActiveDevices.FirstOrDefault(d => d.DeviceId == DeviceId);
            return dev?.Power ?? (Type == DeviceType.Headset ? PowerStatus.PoweredOff : PowerStatus.Unknown);
        }
    }

    public int BatteryPercentage
    {
        get
        {
            var dev = App.Coordinator?.ActiveDevices.FirstOrDefault(d => d.DeviceId == DeviceId);
            return dev?.BatteryPercentage ?? -1;
        }
    }

    public bool HasBattery => BatteryPercentage >= 0 || Type == DeviceType.Keyboard || Type == DeviceType.Mouse || Type == DeviceType.Headset;

    public bool IsDefault
    {
        get
        {
            var dev = App.Coordinator?.ActiveDevices.FirstOrDefault(d => d.DeviceId == DeviceId);
            return dev?.IsDefault ?? false;
        }
    }

    public ObservableCollection<DeviceControl> Controls
    {
        get
        {
            var dev = App.Coordinator?.ActiveDevices.FirstOrDefault(d => d.DeviceId == DeviceId);
            return dev != null ? new ObservableCollection<DeviceControl>(dev.Controls) : [];
        }
    }

    public string BatteryPercentageText
    {
        get
        {
            var dev = App.Coordinator?.ActiveDevices.FirstOrDefault(d => d.DeviceId == DeviceId);
            if (dev != null && dev.IsOnline && dev.BatteryPercentage >= 0)
            {
                return $"{dev.BatteryPercentage}%";
            }
            return "Unknown";
        }
    }

    public string LastUpdatedText
    {
        get
        {
            return App.Coordinator?.GetDeviceLastUpdated(DeviceId) ?? "Unknown";
        }
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused != value)
            {
                _isPaused = value;
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(PauseStatusText));
                // Signal change to refresh the Alerts tab view
                ((MainWindow)Application.Current.MainWindow)?.OverriddenDevicesView?.Refresh();
            }
        }
    }

    public string PauseStatusText => IsPaused ? "Paused" : "Active";

    public System.Windows.Input.ICommand Pause1HourCommand => new RelayCommand(() => PauseAlerts(TimeSpan.FromHours(1)));
    public System.Windows.Input.ICommand PauseUntilLaunchCommand => new RelayCommand(() => PauseAlerts(TimeSpan.MaxValue));
    public System.Windows.Input.ICommand ResumeAlertsCommand => new RelayCommand(ResumeAlerts);

    private System.Threading.Timer? _pauseTimer;

    private void PauseAlerts(TimeSpan duration)
    {
        _pauseTimer?.Dispose();
        IsPaused = true;
        if (duration != TimeSpan.MaxValue)
        {
            _pauseTimer = new System.Threading.Timer(_ => 
            {
                App.Current.Dispatcher.Invoke(() => ResumeAlerts());
            }, null, duration, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private void ResumeAlerts()
    {
        _pauseTimer?.Dispose();
        _pauseTimer = null;
        IsPaused = false;
    }

    private bool? _lastIsDefault;

    public void RefreshDynamicStatus()
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(Power));
        OnPropertyChanged(nameof(BatteryPercentage));
        
        bool currentIsDefault = IsDefault;
        if (_lastIsDefault == null || _lastIsDefault.Value != currentIsDefault)
        {
            _lastIsDefault = currentIsDefault;
            OnPropertyChanged(nameof(IsDefault));
            OnPropertyChanged(nameof(Controls));
        }

        OnPropertyChanged(nameof(IconGeometry));
        OnPropertyChanged(nameof(BatteryPercentageText));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(ActiveStatusText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }
}
