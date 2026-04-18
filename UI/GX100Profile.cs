using System.Windows.Controls;
using GX100Plugin.Telemetry;
using Newtonsoft.Json;
using SimHub.Plugins.ProfilesCommon;

namespace GX100Plugin
{
    public class GX100Profile : ProfileBase<GX100Profile, GX100ProfileStore>, IProfile, IProfile<GX100Profile, GX100ProfileStore>
    {
        [JsonIgnore]
        public override Control ProfileContentControl => null!;

        // All settings use -1 sentinel for "not included in this profile"
        // Bools stored as 0/1/-1
        public int HSensitivity { get; set; } = -1;
        public int SeqSensitivity { get; set; } = -1;
        public int ComboSwitch1 { get; set; } = -1;
        public int ComboSwitch2 { get; set; } = -1;
        public int SequentialMode { get; set; } = -1;
        public int PullButton { get; set; } = -1;

        public override void CopyProfilePropertiesFrom(GX100Profile p)
        {
            HSensitivity = p.HSensitivity;
            SeqSensitivity = p.SeqSensitivity;
            ComboSwitch1 = p.ComboSwitch1;
            ComboSwitch2 = p.ComboSwitch2;
            SequentialMode = p.SequentialMode;
            PullButton = p.PullButton;
        }

        public void CaptureFromCurrent(GX100PluginSettings settings, GX100Data data)
        {
            HSensitivity = settings.HSensitivity;
            SeqSensitivity = settings.SeqSensitivity;
            ComboSwitch1 = settings.ComboSwitch1;
            ComboSwitch2 = settings.ComboSwitch2;
            SequentialMode = settings.SequentialMode;
            PullButton = settings.PullButton;
        }
    }
}
