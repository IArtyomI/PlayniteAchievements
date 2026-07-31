namespace PlayniteAchievements
{
    public partial class PlayniteAchievementsPlugin
    {
        static PlayniteAchievementsPlugin()
        {
            // A local GSE source is intentionally evaluated before the normal Steam web
            // provider. Its capability check is narrow: it requires a matching local
            // steam_settings schema and an existing GSE runtime AppID directory. Official
            // Steam games without those files continue to resolve to the normal provider.
            ProviderRefreshOrder = new[]
            {
                "Manual", "FFXIV", "Exophase", "GseLocal", "Steam", "Epic", "GOG",
                "BattleNet", "EA", "Hoyoverse", "RPCS3", "ShadPS4", "PSN", "Xenia",
                "Xbox", "RetroAchievements"
            };

            ProviderDisplayOrder = new[]
            {
                "Steam", "GseLocal", "Epic", "GOG", "BattleNet", "EA", "Ubisoft",
                "PSN", "Xbox", "GooglePlay", "Apple", "FFXIV", "RetroAchievements",
                "RPCS3", "ShadPS4", "Xenia", "Manual", "Exophase", "Hoyoverse"
            };
        }
    }
}
