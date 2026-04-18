namespace GX100Plugin
{
    public class GX100PluginSettings
    {
        public bool ConnectionEnabled { get; set; } = true;
        public bool AutoApplyProfileOnLaunch { get; set; } = true;

        public int HSensitivity { get; set; } = 80;
        public int SeqSensitivity { get; set; } = 80;
        public int ComboSwitch1 { get; set; } = 1;  // 0/1
        public int ComboSwitch2 { get; set; } = 0;
        public int SequentialMode { get; set; } = 0;
        public int PullButton { get; set; } = 0;

        public GX100ProfileStore ProfileStore { get; set; } = new GX100ProfileStore();
    }
}
