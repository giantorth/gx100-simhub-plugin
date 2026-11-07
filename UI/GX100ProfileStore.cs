using Newtonsoft.Json;
using SimHub.Plugins.ProfilesCommon;

namespace GX100Plugin
{
    public class GX100ProfileStore : ActiveProfileTrackingStore<GX100Profile, GX100ProfileStore>, IProfileSettings<GX100Profile>, IProfileSettings
    {
        public override string FileFilter => "GX100 profile (*.shgx100profile)|*.shgx100profile";

        /// <summary>
        /// The live plugin configuration, used to seed new profiles and to complete
        /// old ones. Set by the plugin before Init(). Never serialized — it is the
        /// object that owns this store.
        /// </summary>
        [JsonIgnore]
        internal GX100PluginSettings? Owner { get; set; }

        /// <summary>
        /// SimHub routes every profile it takes ownership of through here — restored
        /// from settings on startup, newly created, cloned, imported from a file, or
        /// loaded as a built-in. It is therefore the one place that can guarantee no
        /// profile reaches the rest of the plugin holding a value that cannot be
        /// applied to the device.
        /// </summary>
        public override void InitProfile(GX100Profile p)
        {
            p.Normalize(Owner ?? new GX100PluginSettings());
        }

        /// <summary>
        /// Called by SimHub for a brand new profile, before its properties dialog
        /// opens. Seeding from the live configuration means creating a profile never
        /// reconfigures the shifter behind the user's back.
        /// </summary>
        public override void SetProfileDefaults(GX100Profile p)
        {
            if (Owner != null)
                p.CaptureFromCurrent(Owner);
        }
    }
}
