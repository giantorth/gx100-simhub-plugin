namespace GX100Plugin
{
    public class GX100PluginSettings
    {
        public bool ConnectionEnabled { get; set; } = true;
        public bool AutoApplyProfileOnLaunch { get; set; } = true;

        public int HSensitivity { get; set; } = GX100Defaults.HSensitivity;
        public int SeqSensitivity { get; set; } = GX100Defaults.SeqSensitivity;
        public int ComboSwitch1 { get; set; } = GX100Defaults.ComboSwitch1;  // 0/1
        public int ComboSwitch2 { get; set; } = GX100Defaults.ComboSwitch2;
        public int SequentialMode { get; set; } = GX100Defaults.SequentialMode;
        public int PullButton { get; set; } = GX100Defaults.PullButton;

        public GX100ProfileStore ProfileStore { get; set; } = new GX100ProfileStore();

        /// <summary>
        /// Replaces anything the device would reject with its default. Runs once on
        /// load, so the rest of the plugin — and every profile that falls back to
        /// these values — can treat the configuration as known-good.
        /// </summary>
        public void Normalize()
        {
            if (!GX100Defaults.IsSensitivity(HSensitivity)) HSensitivity = GX100Defaults.HSensitivity;
            if (!GX100Defaults.IsSensitivity(SeqSensitivity)) SeqSensitivity = GX100Defaults.SeqSensitivity;
            if (!GX100Defaults.IsFlag(ComboSwitch1)) ComboSwitch1 = GX100Defaults.ComboSwitch1;
            if (!GX100Defaults.IsFlag(ComboSwitch2)) ComboSwitch2 = GX100Defaults.ComboSwitch2;
            if (!GX100Defaults.IsFlag(SequentialMode)) SequentialMode = GX100Defaults.SequentialMode;
            if (!GX100Defaults.IsFlag(PullButton)) PullButton = GX100Defaults.PullButton;
        }
    }
}
