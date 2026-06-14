using System.Windows;
using System.Windows.Controls;
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
            if (sender is FrameworkElement fe && fe.DataContext is DeviceControl ctrl)
            {
                bool isChecked = ((CheckBox)sender).IsChecked == true;
                double val = isChecked ? 1.0 : 0.0;
                ctrl.Value = val;
                ctrl.OnControlChanged?.Invoke(val);
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
