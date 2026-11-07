using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GX100Plugin.Protocol;
using GX100Plugin.Telemetry;

namespace GX100Plugin
{
    public partial class SettingsControl : UserControl
    {
        private readonly GX100Plugin _plugin;
        private readonly DispatcherTimer _sensitivitySendTimer;
        private bool _suppressEvents;

        // H-gate tiles in sensor order: slot N shows gear N and Hall sensor N.
        private readonly StatusTile[] _gearTiles = new StatusTile[GX100Protocol.SensorCount];
        private StatusTile _seqDownTile = null!;
        private StatusTile _seqUpTile = null!;
        private readonly StatusTile[] _buttonTiles = new StatusTile[4];  // B1-B4; B5 has no header on the hardware

        public SettingsControl(GX100Plugin plugin)
        {
            _plugin = plugin;
            _suppressEvents = true;
            InitializeComponent();
            _suppressEvents = false;

            ProfileListControl.DataContext = _plugin.Settings.ProfileStore;

            // SimHub's ProfileList pads its own content (30,15,30,0) for a full-width
            // page; in this column that just pushes the selector away from its
            // neighbours.
            if (ProfileListControl.Content is FrameworkElement profileContent)
                profileContent.Margin = new Thickness(0);
            BuildStatusTiles();

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

            // Refreshed once per rendered frame rather than from a DispatcherTimer:
            // a timer ticks at Background priority, below input and rendering, so
            // a busy SimHub UI thread holds it back and the live display trails
            // the shifter. The device only reports every ~56ms, so most frames
            // find nothing changed and touch nothing.
            CompositionTarget.Rendering -= RefreshUI;
            CompositionTarget.Rendering += RefreshUI;
        }

        private void Detach()
        {
            _plugin.ConfigurationChanged -= OnConfigurationChanged;
            CompositionTarget.Rendering -= RefreshUI;
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

        private void BuildStatusTiles()
        {
            // Odd gears on the top row, even gears on the bottom, as on the gate.
            for (int i = 0; i < _gearTiles.Length; i++)
            {
                var tile = new StatusTile((i + 1).ToString(), showSensor: true, width: 66, height: 74);
                Grid.SetColumn(tile.Root, i / 2);
                Grid.SetRow(tile.Root, i % 2 == 0 ? 0 : 2);
                tile.Root.Margin = new Thickness(i / 2 == 0 ? 0 : 6, 0, 0, 0);
                HGateGrid.Children.Add(tile.Root);
                _gearTiles[i] = tile;
            }

            // The sequential lever moves over the middle slot pair: pushing away
            // (Shift -) passes sensor 3, pulling back (Shift +) sensor 4.
            _seqDownTile = new StatusTile("Shift \u2212", showSensor: true, width: 120, height: 74);
            _seqUpTile = new StatusTile("Shift +", showSensor: true, width: 120, height: 74);
            Grid.SetRow(_seqDownTile.Root, 0);
            Grid.SetRow(_seqUpTile.Root, 2);
            SeqGrid.Children.Add(_seqDownTile.Root);
            SeqGrid.Children.Add(_seqUpTile.Root);

            for (int i = 0; i < _buttonTiles.Length; i++)
            {
                var tile = new StatusTile($"B{i + 1}", showSensor: false, width: 50, height: 48);
                tile.Root.Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0);
                ButtonsPanel.Children.Add(tile.Root);
                _buttonTiles[i] = tile;
            }
        }

        private void LoadSettingsIntoUI()
        {
            _suppressEvents = true;
            try
            {
                var s = _plugin.Settings;
                ConnectionToggle.IsChecked = s.ConnectionEnabled;
                ShowSensorsToggle.IsChecked = s.ShowSensorReadings;
                ApplySensorVisibility(s.ShowSensorReadings);
                ModeChimeToggle.IsChecked = s.ModeChimeEnabled;
                SeqModeToggle.IsChecked = _plugin.Data.SequentialMode;
                HSensSlider.Value = s.HSensitivity;
                HSensValue.Text = s.HSensitivity.ToString();
                SeqSensSlider.Value = s.SeqSensitivity;
                SeqSensValue.Text = s.SeqSensitivity.ToString();
                Combo1Toggle.IsChecked = s.ComboSwitch1 != 0;
                Combo2Toggle.IsChecked = s.ComboSwitch2 != 0;
                PullBtnToggle.IsChecked = s.PullButton != 0;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void RefreshUI(object? sender, EventArgs e)
        {
            if (!IsVisible) return;

            var data = _plugin.Data;

            // Connection status
            ConnectionDot.Fill = data.IsConnected
                ? Brushes.LimeGreen
                : Brushes.Gray;
            SetText(ConnectionStatus, data.IsConnected ? "Connected" : "Disconnected");
            SetSeqModeChecked(data.SequentialMode);
            SetText(GearDisplay, data.IsConnected ? data.GearName : "\u2014");

            bool live = data.IsConnected;

            // Gate slots double up: with the pull ring lifted, slot 1 reports
            // reverse and slots 5/6 report gears 7/8 as buttons of their own.
            for (int i = 0; i < _gearTiles.Length; i++)
            {
                bool active = live && data.IsPressed(GX100Protocol.ButtonGear1 + i);
                string label = (i + 1).ToString();

                if (i == 0 && live && data.IsPressed(GX100Protocol.ButtonReverse))
                {
                    active = true;
                    label = "R";
                }
                else if (i >= 4 && live && data.IsPressed(GX100Protocol.ButtonGear7 + (i - 4)))
                {
                    active = true;
                    label = (i + 3).ToString();
                }

                var tile = _gearTiles[i];
                tile.SetLabel(label);
                tile.SetActive(active);
                tile.SetSensor(live ? data.GetSensor(i) : (int?)null);
            }

            // The sequential panel shows readings only while it is in use (see the
            // greying below); otherwise sensors 3/4 belong to the H-gate and are
            // already shown there.
            bool seqLive = live && (data.SequentialMode || data.IsSeqCalibrating);
            _seqDownTile.SetActive(live && data.SeqDown);
            _seqDownTile.SetSensor(seqLive ? data.GetSensor(2) : (int?)null);
            _seqUpTile.SetActive(live && data.SeqUp);
            _seqUpTile.SetSensor(seqLive ? data.GetSensor(3) : (int?)null);

            _buttonTiles[0].SetActive(live && data.Button1);
            _buttonTiles[1].SetActive(live && data.Button2);
            _buttonTiles[2].SetActive(live && data.Button3);
            _buttonTiles[3].SetActive(live && data.Button4);

            // Calibration: the panel being calibrated is outlined, and the other
            // one's button is locked until it finishes.
            SetContent(HCalButton, data.IsHCalibrating ? "Finish Calibration" : "Calibrate");
            SetContent(SeqCalButton, data.IsSeqCalibrating ? "Finish Calibration" : "Calibrate");
            HCalButton.IsEnabled = !data.IsSeqCalibrating;
            SetCalibrating(HPanel, data.IsHCalibrating);
            SetCalibrating(SeqPanel, data.IsSeqCalibrating);

            // With sequential mode off the panel is greyed and can't start a
            // calibration — but one already running stays live so it can finish.
            bool seqInUse = data.SequentialMode || data.IsSeqCalibrating;
            SeqCalButton.IsEnabled = seqInUse && !data.IsHCalibrating;
            double seqOpacity = seqInUse ? 1.0 : 0.4;
            if (SeqPanel.Opacity != seqOpacity) SeqPanel.Opacity = seqOpacity;
        }

        private static readonly Brush CalibratingBrush = CreateFrozen(Color.FromRgb(0xFF, 0xB3, 0x00));

        private static Brush CreateFrozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static void SetCalibrating(Border panel, bool calibrating)
        {
            bool shown = ReferenceEquals(panel.ReadLocalValue(Border.BorderBrushProperty), CalibratingBrush);
            if (calibrating == shown) return;

            if (calibrating)
                panel.BorderBrush = CalibratingBrush;
            else
                panel.ClearValue(Border.BorderBrushProperty);   // back to the PanelBox style
        }

        // Runs every frame, so only write what actually changed: a fresh string
        // with equal text would otherwise still re-measure the element.
        private static void SetText(TextBlock block, string text)
        {
            if (block.Text != text) block.Text = text;
        }

        // The header checkbox shows the live mode, which a SimHub action can
        // change without touching the settings, so it is kept in step here.
        private void SetSeqModeChecked(bool sequential)
        {
            if (SeqModeToggle.IsChecked == sequential) return;
            _suppressEvents = true;
            try { SeqModeToggle.IsChecked = sequential; }
            finally { _suppressEvents = false; }
        }

        private static void SetContent(ContentControl control, string text)
        {
            if (!(control.Content is string current) || current != text) control.Content = text;
        }

        // --- Event handlers ---

        private void ConnectionToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.SetConnectionEnabled(ConnectionToggle.IsChecked == true);
        }

        private void ShowSensorsToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool show = ShowSensorsToggle.IsChecked == true;
            _plugin.Settings.ShowSensorReadings = show;
            _plugin.SaveSettings();
            ApplySensorVisibility(show);
        }

        private void ModeChimeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.ModeChimeEnabled = ModeChimeToggle.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void ApplySensorVisibility(bool show)
        {
            foreach (var tile in _gearTiles) tile.SetSensorVisible(show);
            _seqDownTile.SetSensorVisible(show);
            _seqUpTile.SetSensorVisible(show);
        }

        private void SeqModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool value = SeqModeToggle.IsChecked == true;
            _plugin.Data.SequentialMode = value;
            _plugin.Settings.SequentialMode = value ? 1 : 0;
            _plugin.SaveSettings();
            _plugin.SendCurrentConfig();
        }

        private void SeqModeLabel_Click(object sender, MouseButtonEventArgs e)
        {
            SeqModeToggle.IsChecked = SeqModeToggle.IsChecked != true;
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
            _plugin.SendCurrentConfig();
        }

        private void Combo2Toggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool value = Combo2Toggle.IsChecked == true;
            _plugin.Data.ComboSwitch2 = value;
            _plugin.Settings.ComboSwitch2 = value ? 1 : 0;
            _plugin.SaveSettings();
            _plugin.SendCurrentConfig();
        }

        private void PullBtnToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool value = PullBtnToggle.IsChecked == true;
            _plugin.Data.PullButton = value;
            _plugin.Settings.PullButton = value ? 1 : 0;
            _plugin.SaveSettings();
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
    }
}
