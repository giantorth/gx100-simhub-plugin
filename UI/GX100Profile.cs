using System.Windows.Controls;
using Newtonsoft.Json;
using SimHub.Plugins.ProfilesCommon;

namespace GX100Plugin
{
    public class GX100Profile : ProfileBase<GX100Profile, GX100ProfileStore>, IProfile, IProfile<GX100Profile, GX100ProfileStore>
    {
        [JsonIgnore]
        public override Control ProfileContentControl => null!;

        // Every profile carries a complete configuration; there is no "not included
        // in this profile" value. A profile that could omit settings cannot be
        // applied deterministically — the omitted ones would silently keep whatever
        // the previously selected profile left on the device, so what the shifter
        // did depended on which profile happened to precede this one.
        // Bools are stored as 0/1.
        //
        // Sensitivities are deliberately absent: they tune the shifter hardware, not
        // a game, so they live only in the plugin settings and a profile switch
        // leaves them alone.
        public int ComboSwitch1 { get; set; } = GX100Defaults.ComboSwitch1;
        public int ComboSwitch2 { get; set; } = GX100Defaults.ComboSwitch2;
        public int SequentialMode { get; set; } = GX100Defaults.SequentialMode;
        public int PullButton { get; set; } = GX100Defaults.PullButton;

        public override void CopyProfilePropertiesFrom(GX100Profile p)
        {
            ComboSwitch1 = p.ComboSwitch1;
            ComboSwitch2 = p.ComboSwitch2;
            SequentialMode = p.SequentialMode;
            PullButton = p.PullButton;
        }

        /// <summary>Records the configuration currently in use into this profile.</summary>
        public void CaptureFromCurrent(GX100PluginSettings settings)
        {
            ComboSwitch1 = settings.ComboSwitch1;
            ComboSwitch2 = settings.ComboSwitch2;
            SequentialMode = settings.SequentialMode;
            PullButton = settings.PullButton;
        }

        /// <summary>
        /// The inverse of <see cref="CaptureFromCurrent"/>: makes this profile the
        /// configuration in use. Writes every profile setting, so nothing survives from
        /// whichever profile was selected before.
        /// </summary>
        public void ApplyTo(GX100PluginSettings settings)
        {
            settings.ComboSwitch1 = ComboSwitch1;
            settings.ComboSwitch2 = ComboSwitch2;
            settings.SequentialMode = SequentialMode;
            settings.PullButton = PullButton;
        }

        /// <summary>
        /// Fills in any setting the device would reject, leaving usable ones alone.
        /// Profiles written before every setting became mandatory stored -1 for
        /// "not included"; those load as the configuration that was in use at the
        /// time of the upgrade rather than resetting the user to stock values.
        /// </summary>
        public void Normalize(GX100PluginSettings fallback)
        {
            if (!GX100Defaults.IsFlag(ComboSwitch1)) ComboSwitch1 = fallback.ComboSwitch1;
            if (!GX100Defaults.IsFlag(ComboSwitch2)) ComboSwitch2 = fallback.ComboSwitch2;
            if (!GX100Defaults.IsFlag(SequentialMode)) SequentialMode = fallback.SequentialMode;
            if (!GX100Defaults.IsFlag(PullButton)) PullButton = fallback.PullButton;
        }
    }
}
