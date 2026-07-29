namespace PlayniteAchievements.ViewModels
{
    internal static class ProviderRegistry
    {
        public static string GetLocalizedName(string providerKey)
        {
            return PlayniteAchievements.Services.ProviderRegistry.GetLocalizedName(providerKey);
        }
    }
}
