using System.Windows;
using System.Windows.Controls;
using System.Linq;
using GearTray.Contracts;

namespace GearTrayUI
{
    public partial class TrayResources : ResourceDictionary
    {
        public TrayResources()
        {
            InitializeComponent();
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow != null)
            {
                if (mainWindow.Visibility == Visibility.Visible)
                {
                    mainWindow.Activate();
                }
                else
                {
                    mainWindow.Show();
                }
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void Control_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is DeviceControl ctrl)
            {
                bool isChecked = cb.IsChecked == true;
                double val = isChecked ? 1.0 : 0.0;
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

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is DeviceControl ctrl)
            {
                ctrl.Value = e.NewValue;
                ctrl.OnControlChanged?.Invoke(e.NewValue);
            }
        }
    }
}
