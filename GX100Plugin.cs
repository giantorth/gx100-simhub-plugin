using System;
using System.Threading;
using System.Timers;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using GX100Plugin.Protocol;
using GX100Plugin.Telemetry;
using Timer = System.Timers.Timer;

namespace GX100Plugin
{
    [PluginDescription("Configure GX100 USB shifter with per-game profiles")]
    [PluginAuthor("giantorth")]
    [PluginName("GX100 Shifter")]
    public class GX100Plugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        private GX100HidConnection _connection = null!;
        private GX100Data _data = null!;
        private GX100PluginSettings _settings = null!;
        private Timer _reconnectTimer = null!;
        private Timer? _saveDebounceTimer;
        private ModeChime? _modeChime;
        private bool? _lastSentSequential;
        private readonly System.Diagnostics.Stopwatch _sinceInit = new System.Diagnostics.Stopwatch();

        // SimHub rebuilds the plugin on every game switch, and when it re-enumerates
        // joysticks it replays the state of any latching switch bound to a mode
        // action. That replay lands after the game's profile has been applied and
        // would override it. Enumeration took ~4s after Init on a cold SimHub start
        // and ~0.6s on a game switch, so this leaves headroom over the slow case.
        private static readonly TimeSpan ActionStartupGrace = TimeSpan.FromSeconds(10);
        private PluginManager _pluginManager = null!;
        private int _reconnectBusy;

        public PluginManager PluginManager { set => _pluginManager = value; }
        public ImageSource? PictureIcon => NavIcon.Value;
        public string LeftMenuTitle => "GX100";

        // Six-speed H-pattern gate nav icon. Cyan tint for SimHub's dark nav.
        private static readonly Lazy<ImageSource> NavIcon = new Lazy<ImageSource>(BuildNavIcon);

        private static ImageSource BuildNavIcon()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));

            // Gate slots: three columns (1-2, 3-4, 5-6) joined by the neutral row.
            var slots = Geometry.Parse("M60 56 L60 184 M120 56 L120 184 M180 56 L180 184 M60 120 L180 120");
            var pen = new Pen(brush, 22)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };

            // A knob at each gear position, so it reads as a gate rather than a grid.
            var knobs = new GeometryGroup();
            foreach (double x in new[] { 60.0, 120.0, 180.0 })
            {
                knobs.Children.Add(new EllipseGeometry(new System.Windows.Point(x, 56), 20, 20));
                knobs.Children.Add(new EllipseGeometry(new System.Windows.Point(x, 184), 20, 20));
            }

            // Transparent backing rect gives square bounds so SimHub scales the gate
            // uniformly instead of stretching the bare geometry box.
            var canvas = new GeometryDrawing(
                Brushes.Transparent, null,
                new RectangleGeometry(new System.Windows.Rect(16, 16, 208, 208)));

            var root = new DrawingGroup();
            root.Children.Add(canvas);
            root.Children.Add(new GeometryDrawing(null, pen, slots));
            root.Children.Add(new GeometryDrawing(brush, null, knobs));

            var image = new DrawingImage(root);
            image.Freeze();
            return image;
        }

        internal GX100Data Data => _data;
        internal GX100PluginSettings Settings => _settings;

        /// <summary>
        /// Raised after something other than the settings panel has changed the live
        /// configuration — applying a profile, or clearing settings — so an open
        /// panel can redisplay it.
        /// </summary>
        internal event Action? ConfigurationChanged;

        public void Init(PluginManager pluginManager)
        {
            _pluginManager = pluginManager;
            _sinceInit.Restart();
            _data = new GX100Data();
            _settings = this.ReadCommonSettings<GX100PluginSettings>("GX100PluginSettings", () => new GX100PluginSettings());

            if (_settings.ProfileStore == null)
                _settings.ProfileStore = new GX100ProfileStore();

            _settings.Normalize();

            SimHub.Logging.Current.Info("[GX100] Initializing plugin");

            SyncSettingsToData();
            RegisterProperties(pluginManager);
            _modeChime = new ModeChime();
            RegisterActions();

            _connection = new GX100HidConnection();
            _connection.ButtonStateChanged += OnButtonStateChanged;
            _connection.SensorsUpdated += _data.UpdateSensors;
            _connection.DeviceDisconnected += OnDeviceDisconnected;

            if (_settings.ConnectionEnabled)
                TryConnect();

            _reconnectTimer = new Timer(5000);
            _reconnectTimer.Elapsed += (s, e) =>
            {
                // Elapsed fires on a pool thread whether or not the previous tick
                // finished, so a slow Connect() would let ticks stack up.
                if (Interlocked.Exchange(ref _reconnectBusy, 1) == 1) return;
                try
                {
                    if (!_connection.IsConnected)
                        TryConnect();
                }
                finally
                {
                    Volatile.Write(ref _reconnectBusy, 0);
                }
            };
            _reconnectTimer.AutoReset = true;
            if (_settings.ConnectionEnabled)
                _reconnectTimer.Start();

            InitProfileSystem();
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
        }

        public void End(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[GX100] Shutting down plugin");
            _saveDebounceTimer?.Stop();
            _saveDebounceTimer?.Dispose();
            _saveDebounceTimer = null;
            this.SaveCommonSettings("GX100PluginSettings", _settings);
            // A profile switch arriving mid-teardown must not reach a disposed connection.
            if (_settings?.ProfileStore != null)
                _settings.ProfileStore.ActiveProfileChanged -= OnProfileChanged;
            _reconnectTimer?.Stop();
            _reconnectTimer?.Dispose();
            _connection?.Dispose();
            _modeChime?.Dispose();
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        // --- Properties ---

        private void RegisterProperties(PluginManager pluginManager)
        {
            this.AttachDelegate("GX100.Connected", () => _data.IsConnected);
            this.AttachDelegate("GX100.CurrentGear", () => _data.CurrentGear);
            this.AttachDelegate("GX100.GearName", () => _data.GearName);
            this.AttachDelegate("GX100.SequentialMode", () => _data.SequentialMode);
            this.AttachDelegate("GX100.SeqUp", () => _data.SeqUp);
            this.AttachDelegate("GX100.SeqDown", () => _data.SeqDown);
            this.AttachDelegate("GX100.Button1", () => _data.Button1);
            this.AttachDelegate("GX100.Button2", () => _data.Button2);
            this.AttachDelegate("GX100.Button3", () => _data.Button3);
            this.AttachDelegate("GX100.Button4", () => _data.Button4);
            this.AttachDelegate("GX100.Button5", () => _data.Button5);
            for (int i = 0; i < Protocol.GX100Protocol.SensorCount; i++)
            {
                int index = i;
                this.AttachDelegate($"GX100.Sensor{index + 1}", () => _data.GetSensor(index));
            }
        }

        // --- Actions ---

        private void RegisterActions()
        {
            // Bindable from SimHub's controls page, so the mode can be flipped from a
            // wheel button or switch mid-session. They push to the device as they
            // fire: a button press is expected to change how the shifter behaves
            // there and then.
            this.AddAction("GX100.SequentialModeToggle", (a, b) => SetSequentialModeFromAction(!_data.SequentialMode));
            this.AddAction("GX100.SequentialModeOn", (a, b) => SetSequentialModeFromAction(true));
            this.AddAction("GX100.SequentialModeOff", (a, b) => SetSequentialModeFromAction(false));
        }

        /// <summary>
        /// Switches the shifter between H-pattern and sequential mode and sends the
        /// change to the device immediately. This is a live override only: neither
        /// the plugin settings nor the active profile change, so the next profile
        /// applied (e.g. on game switch) sets the mode again.
        /// </summary>
        private void SetSequentialModeFromAction(bool sequential)
        {
            // Already there: skip the redundant report rather than make the device
            // re-read a configuration it is already running.
            if (_data.SequentialMode == sequential) return;

            if (_sinceInit.Elapsed < ActionStartupGrace)
            {
                SimHub.Logging.Current.Info(
                    $"[GX100] Sequential mode {(sequential ? "on" : "off")} action ignored — " +
                    "within startup grace period, profile setting kept");
                return;
            }

            // Only the live state changes. _settings keeps the profile's value, so a
            // later SaveSettings() from the panel can't capture the override into
            // the profile.
            _data.SequentialMode = sequential;
            SendCurrentConfig();

            SimHub.Logging.Current.Info(
                $"[GX100] Sequential mode {(sequential ? "on" : "off")} via action");
        }

        // --- Connection ---

        private void TryConnect()
        {
            if (_connection.Connect())
            {
                _data.IsConnected = true;
                SimHub.Logging.Current.Info("[GX100] Connected — applying settings");
                SendCurrentConfig();
            }
        }

        private void OnDeviceDisconnected()
        {
            _data.IsConnected = false;
        }

        private void OnButtonStateChanged(ushort buttons)
        {
            _data.UpdateFromButtons(buttons);
        }

        internal void SetConnectionEnabled(bool enabled)
        {
            _settings.ConnectionEnabled = enabled;
            SaveSettings();

            if (enabled)
            {
                _reconnectTimer.Start();
                if (!_connection.IsConnected)
                    TryConnect();
                SimHub.Logging.Current.Info("[GX100] Connection enabled");
            }
            else
            {
                _reconnectTimer.Stop();
                _connection.Disconnect();
                _data.IsConnected = false;
                SimHub.Logging.Current.Info("[GX100] Connection disabled");
            }
        }

        // --- Config send ---

        internal void SendCurrentConfig()
        {
            string detail =
                $"H={_data.HSensitivity} Seq={_data.SeqSensitivity} " +
                $"Mode={(_data.SequentialMode ? "SEQ" : "H")} CB1={_data.ComboSwitch1} " +
                $"CB2={_data.ComboSwitch2} Pull={_data.PullButton}";

            // Say so rather than returning quietly: "my settings did not take effect"
            // and "the shifter is not plugged in" look identical from the UI. The
            // config is not lost — TryConnect sends it as soon as the device appears.
            if (!_connection.IsConnected)
            {
                SimHub.Logging.Current.Info($"[GX100] Not connected, config deferred: {detail}");
                return;
            }

            bool sent = _connection.SendConfiguration(
                _data.ComboSwitch1,
                _data.ComboSwitch2,
                _data.SequentialMode,
                _data.HSensitivity,
                _data.SeqSensitivity,
                _data.PullButton
            );

            if (!sent)
            {
                SimHub.Logging.Current.Error($"[GX100] Config send FAILED: {detail}");
                return;
            }

            SimHub.Logging.Current.Info($"[GX100] Config sent: {detail}");

            // Chime whenever the device actually switches mode, whatever asked for
            // it — a bound button, a profile change on game switch, or a toggle in the
            // settings panel. The first send after startup only establishes the
            // mode, so it stays silent. The last-sent mode is tracked even while the
            // chime is disabled, so re-enabling it doesn't chime for a stale change.
            bool sequential = _data.SequentialMode;
            if (_settings.ModeChimeEnabled && _lastSentSequential.HasValue && _lastSentSequential != sequential)
                _modeChime?.Play(sequential);
            _lastSentSequential = sequential;
        }

        // --- Calibration ---

        internal void ToggleHCalibration()
        {
            if (!_connection.IsConnected) return;

            // Only advance the UI state once the device has actually been told, so
            // a failed write can't leave the button showing a calibration mode the
            // device isn't in.
            bool starting = !_data.IsHCalibrating;
            if (!_connection.SendCalibration(starting
                    ? GX100Protocol.CalHPatternStart
                    : GX100Protocol.CalHPatternFinish))
            {
                SimHub.Logging.Current.Error("[GX100] H-pattern calibration command failed — state unchanged");
                return;
            }

            _data.IsHCalibrating = starting;
            SimHub.Logging.Current.Info($"[GX100] H-pattern calibration: {(starting ? "started" : "finished")}");
        }

        internal void ToggleSeqCalibration()
        {
            if (!_connection.IsConnected) return;

            bool starting = !_data.IsSeqCalibrating;

            // Sequential calibration only means something with the lever in
            // sequential mode. Finishing is always allowed, so a calibration that
            // was running when the mode changed can still be closed out.
            if (starting && !_data.SequentialMode)
            {
                SimHub.Logging.Current.Info("[GX100] Sequential calibration refused — sequential mode is off");
                return;
            }

            if (!_connection.SendCalibration(starting
                    ? GX100Protocol.CalSequentialStart
                    : GX100Protocol.CalSequentialFinish))
            {
                SimHub.Logging.Current.Error("[GX100] Sequential calibration command failed — state unchanged");
                return;
            }

            _data.IsSeqCalibrating = starting;
            SimHub.Logging.Current.Info($"[GX100] Sequential calibration: {(starting ? "started" : "finished")}");
        }

        // --- Settings persistence ---

        internal void SaveSettings()
        {
            _settings.ProfileStore?.CurrentProfile?.CaptureFromCurrent(_settings);
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            if (_saveDebounceTimer == null)
            {
                _saveDebounceTimer = new Timer(500) { AutoReset = false };
                _saveDebounceTimer.Elapsed += (s, e) =>
                    this.SaveCommonSettings("GX100PluginSettings", _settings);
            }
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        }

        private void SyncSettingsToData()
        {
            _data.HSensitivity = _settings.HSensitivity;
            _data.SeqSensitivity = _settings.SeqSensitivity;
            _data.ComboSwitch1 = _settings.ComboSwitch1 != 0;
            _data.ComboSwitch2 = _settings.ComboSwitch2 != 0;
            _data.SequentialMode = _settings.SequentialMode != 0;
            _data.PullButton = _settings.PullButton != 0;
        }

        // --- Profile system ---

        private void InitProfileSystem()
        {
            var store = _settings.ProfileStore;

            // Seeds new profiles and completes ones saved by a version that allowed
            // settings to be omitted. Must be set before Init(), which is where the
            // stored profiles get completed.
            store.Owner = _settings;

            if (store.Profiles.Count == 0)
            {
                var defaultProfile = new GX100Profile { Name = "Default" };
                defaultProfile.CaptureFromCurrent(_settings);
                store.Profiles.Add(defaultProfile);
            }

            store.Init();

            // Not CurrentProfileChanged: SimHub 9.13.3 stopped raising it when a
            // profile is picked, so dropdown selections went unapplied. The baseline
            // keeps the profile applied just below from firing a second time.
            store.ResetActiveProfileBaseline();
            store.ActiveProfileChanged += OnProfileChanged;

            var current = store.CurrentProfile;
            if (current != null)
                ApplyProfile(current);
        }

        private void OnProfileChanged(object sender, EventArgs e)
        {
            var profile = _settings.ProfileStore.CurrentProfile;
            if (profile != null)
                ApplyProfile(profile);
        }

        /// <summary>
        /// Makes <paramref name="profile"/> the live configuration. Every setting the
        /// profile holds is applied — none is optional — so the device ends up in
        /// exactly the state the profile describes, whatever preceded it.
        /// </summary>
        internal void ApplyProfile(GX100Profile profile)
        {
            SimHub.Logging.Current.Info($"[GX100] Applying profile: {profile.Name}");

            profile.Normalize(_settings);
            profile.ApplyTo(_settings);
            SyncSettingsToData();
            SendCurrentConfig();
            ScheduleSave();

            // An open settings panel is still showing the outgoing profile. Leaving
            // it stale would also let the next control the user touches capture those
            // stale values straight back over the profile that was just applied.
            ConfigurationChanged?.Invoke();
        }
    }
}
