namespace Client.MirGraphics
{
    internal static class XiayiUiTheme
    {
        public const int HudBackground = 13;
        public const int HudHealthOrb = 15;
        public const int HudHealthManaOrb = 16;
        public const int HudExperienceFill = 18;

        public const int ChatLarge = 613;
        public const int ChatMedium = 614;
        public const int ChatSmall = 615;
        public const int ChatInput = 616;

        public const int HealthFriendly = 617;
        public const int Mana = 618;
        public const int HealthGroup = 619;
        public const int HealthHostile = 620;
        public const int HealthSingleBackground = 621;
        public const int HealthDualBackground = 622;

        public static bool IsAvailable => Libraries.XiayiUi != null && Libraries.XiayiUi.GetSize(HudBackground).Width == 1024;

        public static bool UseHudTheme => Settings.Resolution != 800 && IsAvailable;
        public static bool UseChatTheme => UseHudTheme;
        public static bool UseOverheadHealthTheme => IsAvailable;
    }
}
