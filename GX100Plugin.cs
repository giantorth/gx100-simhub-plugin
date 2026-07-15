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
        private PluginManager _pluginManager = null!;
        private int _reconnectBusy;

        public PluginManager PluginManager { set => _pluginManager = value; }
        public ImageSource? PictureIcon => null;
        public string LeftMenuTitle => "GX100";

        internal GX100Data Data => _data;
        internal GX100PluginSettings Settings => _settings;

        public void Init(PluginManager pluginManager)
        {
            _pluginManager = pluginManager;
            _data = new GX100Data();
            _settings = this.ReadCommonSettings<GX100PluginSettings>("GX100PluginSettings", () => new GX100PluginSettings());

            if (_settings.ProfileStore == null)
                _settings.ProfileStore = new GX100ProfileStore();

            SimHub.Logging.Current.Info("[GX100] Initializing plugin");

            SyncSettingsToData();
            RegisterProperties(pluginManager);

            _connection = new GX100HidConnection();
            _connection.ButtonStateChanged += OnButtonStateChanged;
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
            _reconnectTimer?.Stop();
            _reconnectTimer?.Dispose();
            _connection?.Dispose();
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
            if (!_connection.IsConnected) return;

            bool sent = _connection.SendConfiguration(
                _data.ComboSwitch1,
                _data.ComboSwitch2,
                _data.SequentialMode,
                _data.HSensitivity,
                _data.SeqSensitivity,
                _data.PullButton
            );

            string detail =
                $"H={_data.HSensitivity} Seq={_data.SeqSensitivity} " +
                $"Mode={(_data.SequentialMode ? "SEQ" : "H")} CB1={_data.ComboSwitch1} " +
                $"CB2={_data.ComboSwitch2} Pull={_data.PullButton}";

            if (sent)
                SimHub.Logging.Current.Info($"[GX100] Config sent: {detail}");
            else
                SimHub.Logging.Current.Error($"[GX100] Config send FAILED: {detail}");
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
            _settings.ProfileStore?.CurrentProfile?.CaptureFromCurrent(_settings, _data);
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

        internal void ClearSettings()
        {
            _settings = new GX100PluginSettings();
            this.SaveCommonSettings("GX100PluginSettings", _settings);
            SyncSettingsToData();
            InitProfileSystem();
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

            if (store.Profiles.Count == 0)
            {
                var defaultProfile = new GX100Profile { Name = "Default" };
                store.Profiles.Add(defaultProfile);
            }

            store.Init();
            store.CurrentProfileChanged += OnProfileChanged;

            if (store.CurrentProfile != null)
            {
                SimHub.Logging.Current.Info($"[GX100] Initial profile: {store.CurrentProfile.Name}");
                if (_settings.AutoApplyProfileOnLaunch)
                    ApplyProfile(store.CurrentProfile);
            }
        }

        private void OnProfileChanged(object sender, EventArgs e)
        {
            var profile = _settings.ProfileStore.CurrentProfile;
            if (profile != null)
            {
                SimHub.Logging.Current.Info($"[GX100] Profile changed: {profile.Name}");
                ApplyProfile(profile);
            }
        }

        internal void ApplyProfile(GX100Profile profile)
        {
            SimHub.Logging.Current.Info($"[GX100] Applying profile: {profile.Name}");

            if (profile.HSensitivity >= 0)
            {
                _settings.HSensitivity = profile.HSensitivity;
                _data.HSensitivity = profile.HSensitivity;
            }
            if (profile.SeqSensitivity >= 0)
            {
                _settings.SeqSensitivity = profile.SeqSensitivity;
                _data.SeqSensitivity = profile.SeqSensitivity;
            }
            if (profile.ComboSwitch1 >= 0)
            {
                _settings.ComboSwitch1 = profile.ComboSwitch1;
                _data.ComboSwitch1 = profile.ComboSwitch1 != 0;
            }
            if (profile.ComboSwitch2 >= 0)
            {
                _settings.ComboSwitch2 = profile.ComboSwitch2;
                _data.ComboSwitch2 = profile.ComboSwitch2 != 0;
            }
            if (profile.SequentialMode >= 0)
            {
                _settings.SequentialMode = profile.SequentialMode;
                _data.SequentialMode = profile.SequentialMode != 0;
            }
            if (profile.PullButton >= 0)
            {
                _settings.PullButton = profile.PullButton;
                _data.PullButton = profile.PullButton != 0;
            }

            if (_connection.IsConnected)
                SendCurrentConfig();

            ScheduleSave();
        }
    }
}
