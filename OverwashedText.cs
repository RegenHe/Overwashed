namespace Overcooked2DishwasherBot
{
    internal static class OverwashedText
    {
        private static bool _languageResolved;
        private static bool _simplifiedChinese;

        internal static bool IsSimplifiedChinese
        {
            get
            {
                if (_languageResolved)
                {
                    return _simplifiedChinese;
                }

                try
                {
                    _simplifiedChinese = Localization.GetLanguage() == SupportedLanguages.Chinese;

                    // Before SteamPlayerManager is ready the game may temporarily
                    // report the operating-system language instead of its selected
                    // Steam language, so do not cache that early fallback.
                    _languageResolved = SteamPlayerManager.Initialized;
                }
                catch
                {
                    _simplifiedChinese = false;
                }

                return _simplifiedChinese;
            }
        }

        internal static string Get(string english, string simplifiedChinese)
        {
            return IsSimplifiedChinese ? simplifiedChinese : english;
        }
    }
}
