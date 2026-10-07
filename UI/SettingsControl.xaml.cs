using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GX100Plugin.Telemetry;

namespace GX100Plugin
{
    public partial class SettingsControl : UserControl
    {
        private readonly GX100Plugin _plugin;
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _sensitivitySendTimer;
        private bool _suppressEvents;

        public SettingsControl(GX100Plugin plugin)
        {
            _plugin = plugin;
            _suppressEvents = true;
            InitializeComponent();
            _suppressEvents = false;

            BindProfileStore();

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _refreshTimer.Tick += RefreshUI;

            // A slider raises ValueChanged for every step of a drag. Waiting for it
            // to settle sends one report per adjustment instead of flooding the
            // device, and covers mouse, keyboard and track clicks alike.
            _sensitivitySendTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _sensitivitySendTimer.Tick += (s, e) =>
            {
                _sensitivitySendTimer.Stop();
                _plugin.SendCurrentConfig();
            };

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            // Attached here and not only from Loaded: the panel has to track the
            // plugin even if the host never raises Loaded for it. Nothing here
            // depends on being in the visual tree.
            Attach();

            LoadSettingsIntoUI();
        }

        private void Attach()
        {
            // Removing first keeps this idempotent, so Loaded firing more than once
            // cannot stack up duplicate handlers.
            _plugin.ConfigurationChanged -= OnConfigurationChanged;
            _plugin.ConfigurationChanged += OnConfigurationChanged;
            _refreshTimer.Start();
        }

        private void Detach()
        {
            _plugin.ConfigurationChanged -= OnConfigurationChanged;
            _refreshTimer.Stop();
        }

        // SimHub shows and hides this panel as the user moves between settings pages.
        // Re-reading on show also covers any profile switch that happened while it
        // was detached.
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Attach();
            LoadSettingsIntoUI();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

        /// <summary>
        /// A profile switch — including the automatic one SimHub performs when a game
        /// loads — replaces the live configuration underneath this panel. Redisplaying
        /// keeps the controls honest, and stops the next edit from capturing the
        /// values of the outgoing profile back into the incoming one.
        /// </summary>
        private void OnConfigurationChanged()
        {
            Dispatcher.BeginInvoke(new Action(LoadSettingsIntoUI));
        }

        private void BindProfileStore()
        {
            ProfileListControl.DataContext = _plugin.Settings.ProfileStore;
        }

        private void LoadSettingsIntoUI()
        {
            _suppressEvents = true;
            try
            {
                var s = _plugin.Settings;
                ConnectionToggle.IsChecked = s.ConnectionEnabled;
                SeqModeToggle.IsChecked = s.SequentialMode != 0;
                HSensSlider.Value = s.HSensitivity;
                HSensValue.Text = s.HSensitivity.ToString();
                SeqSensSlider.Value = s.SeqSensitivity;
                SeqSensValue.Text = s.SeqSensitivity.ToString();
                Combo1Toggle.IsChecked = s.ComboSwitch1 != 0;
                Combo2Toggle.IsChecked = s.ComboSwitch2 != 0;
                PullBtnToggle.IsChecked = s.PullButton != 0;
                AutoApplyToggle.IsChecked = s.AutoApplyProfileOnLaunch;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void RefreshUI(object? sender, EventArgs e)
        {
            var data = _plugin.Data;

            // Connection status
            ConnectionDot.Fill = data.IsConnected
                ? Brushes.LimeGreen
                : Brushes.Gray;
            ConnectionStatus.Text = data.IsConnected
                ? "Connected"
                : "Disconnected";

            // Gear display
            GearDisplay.Text = data.GearName;

            // Sequential indicators
            SeqUpIndicator.Foreground = data.SeqUp ? Brushes.LimeGreen : Brushes.Gray;
            SeqDownIndicator.Foreground = data.SeqDown ? Brushes.LimeGreen : Brushes.Gray;

            // Extra buttons
            Btn1Indicator.Foreground = data.Button1 ? Brushes.LimeGreen : Brushes.Gray;
            Btn2Indicator.Foreground = data.Button2 ? Brushes.LimeGreen : Brushes.Gray;
            Btn3Indicator.Foreground = data.Button3 ? Brushes.LimeGreen : Brushes.Gray;
            Btn4Indicator.Foreground = data.Button4 ? Brushes.LimeGreen : Brushes.Gray;
            Btn5Indicator.Foreground = data.Button5 ? Brushes.LimeGreen : Brushes.Gray;

            // Calibration button labels
            HCalButton.Content = data.IsHCalibrating
                ? "Finish H-Pattern Calibration"
                : "Start H-Pattern Calibration";
            SeqCalButton.Content = data.IsSeqCalibrating
                ? "Finish Sequential Calibration"
                : "Start Sequential Calibration";
            HCalButton.IsEnabled = !data.IsSeqCalibrating;
            SeqCalButton.IsEnabled = !data.IsHCalibrating;
        }

        // --- Event handlers ---

        private void ConnectionToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.SetConnectionEnabled(ConnectionToggle.IsChecked == true);
        }

        private void SeqModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool value = SeqModeToggle.IsChecked == true;
            _plugin.Data.SequentialMode = value;
            _plugin.Settings.SequentialMode = value ? 1 : 0;
            _plugin.SaveSettings();
        }

        private void HSensSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int value = (int)HSensSlider.Value;
            HSensValue.Text = value.ToString();
            _plugin.Data.HSensitivity = value;
            _plugin.Settings.HSensitivity = value;
            _plugin.SaveSettings();
            ScheduleSensitivitySend();
        }

        private void SeqSensSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int value = (int)SeqSensSlider.Value;
            SeqSensValue.Text = value.ToString();
            _plugin.Data.SeqSensitivity = value;
            _plugin.Settings.SeqSensitivity = value;
            _plugin.SaveSettings();
            ScheduleSensitivitySend();
        }

        private void ScheduleSensitivitySend()
        {
            _sensitivitySendTimer.Stop();
            _sensitivitySendTimer.Start();
        }

        private void Combo1Toggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool value = Combo1Toggle.IsChecked == true;
            _plugin.Data.ComboSwitch1 = value;
            _plugin.Settings.ComboSwitch1 = value ? 1 : 0;
            _plugin.SaveSettings();
        }

        private void Combo2Toggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool value = Combo2Toggle.IsChecked == true;
            _plugin.Data.ComboSwitch2 = value;
            _plugin.Settings.ComboSwitch2 = value ? 1 : 0;
            _plugin.SaveSettings();
        }

        private void PullBtnToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool value = PullBtnToggle.IsChecked == true;
            _plugin.Data.PullButton = value;
            _plugin.Settings.PullButton = value ? 1 : 0;
            _plugin.SaveSettings();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.SendCurrentConfig();
        }

        private void HCalButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.ToggleHCalibration();
        }

        private void SeqCalButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.ToggleSeqCalibration();
        }

        private void AutoApplyToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.AutoApplyProfileOnLaunch = AutoApplyToggle.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void ClearSettings_Click(object sender, RoutedEventArgs e)
        {
            _plugin.ClearSettings();
            // Clearing builds a fresh profile store; the list has to be pointed at it
            // or it would keep editing the discarded one.
            BindProfileStore();
            LoadSettingsIntoUI();
        }
    }
}
