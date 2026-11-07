using System;
using SimHub.Plugins.ProfilesCommon;

namespace GX100Plugin
{
    /// <summary>
    /// <see cref="ProfileSettingsBase{TProfile,TSettings}"/> plus a reliable
    /// <see cref="ActiveProfileChanged"/> event. Subscribe to that, never to
    /// <c>CurrentProfileChanged</c>: SimHub 9.13.3 stopped raising
    /// <c>CurrentProfileChanged</c> when a profile is selected (only when the
    /// selection is cleared), so manual picks and game-load switches went unseen.
    /// Both SimHub versions still raise <c>PropertyChanged("CurrentProfile")</c>
    /// from the setter; listening to both and deduping on the resolved profile
    /// fires once per real switch on either version.
    /// </summary>
    public abstract class ActiveProfileTrackingStore<TProfile, TSettings>
        : ProfileSettingsBase<TProfile, TSettings>
        where TProfile : class, IProfile, IProfile<TProfile, TSettings>, new()
        where TSettings : class, IProfileSettings<TProfile>
    {
        private TProfile? _lastActiveProfile;

        /// <summary>Raised once each time <c>CurrentProfile</c> resolves to a
        /// different profile, on the thread that changed it.</summary>
        public event EventHandler? ActiveProfileChanged;

        /// <summary>Record the current profile as already handled, so the next
        /// notification only fires if the selection actually moves. Call after
        /// <c>Init()</c>, before subscribing.</summary>
        public void ResetActiveProfileBaseline()
        {
            _lastActiveProfile = CurrentProfile;
        }

        public override void OnCurrentProfileChanged()
        {
            base.OnCurrentProfileChanged();
            RaiseIfActiveProfileMoved();
        }

        protected override void RaisePropertyChanged(object source, string propertyName)
        {
            base.RaisePropertyChanged(source, propertyName);
            if (propertyName == "CurrentProfile")
                RaiseIfActiveProfileMoved();
        }

        private void RaiseIfActiveProfileMoved()
        {
            // The getter resolves against activeProfileId, which may not be read yet.
            if (IsDeserializing) return;
            var current = CurrentProfile;
            if (ReferenceEquals(current, _lastActiveProfile)) return;
            _lastActiveProfile = current;
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
