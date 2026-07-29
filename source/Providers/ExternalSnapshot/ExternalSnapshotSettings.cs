using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.ExternalSnapshot
{
    public sealed class ExternalSnapshotSettings : ProviderSettingsBase
    {
        public override string ProviderKey => ExternalSnapshotDataProvider.Key;
    }
}
