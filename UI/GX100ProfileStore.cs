using SimHub.Plugins.ProfilesCommon;

namespace GX100Plugin
{
    public class GX100ProfileStore : ProfileSettingsBase<GX100Profile, GX100ProfileStore>, IProfileSettings<GX100Profile>, IProfileSettings
    {
        public override string FileFilter => "GX-100 profile (*.shgx100profile)|*.shgx100profile";

        public override void InitProfile(GX100Profile p)
        {
        }
    }
}
