namespace Client.MirGraphics
{
    internal static class XiayiUiTheme
    {
        public const int HudAssassinBackground = 564;
        public const int HudArcherBackground = 508;
        public const int HudWarriorBackground = 567;
        public const int HudWizardBackground = 566;
        public const int HudTaoistBackground = 568;
        public const int HudHealthFill = 509;
        public const int HudManaFill = 510;
        public const int HudExperienceFill = 511;

        public const int SkillBarBackground = 26;

        public const int HudCharacter = 535;
        public const int HudCharacterActive = 534;
        public const int HudInventory = 543;
        public const int HudInventoryActive = 542;
        public const int HudShop = 541;
        public const int HudShopActive = 540;
        public const int HudSkill = 561;
        public const int HudSkillActive = 560;
        public const int HudQuest = 555;
        public const int HudQuestActive = 554;
        public const int HudMenu = 557;
        public const int HudMenuActive = 556;
        public const int HudOptions = 559;
        public const int HudOptionsActive = 558;
        public const int HudHero = 550;
        public const int HudHeroActive = 549;
        public const int HudPet = 552;
        public const int HudPetActive = 551;

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

        public const int HudWidth = 354;
        public const int HudHeight = 65;
        public const int HudBarY = 9;
        public const int ChatWidth = 300;

        public static bool IsAvailable => Libraries.XiayiUi != null && Libraries.XiayiUi.GetSize(HudWarriorBackground).Width == HudWidth;

        public static bool UseHudTheme => IsAvailable;
        public static bool UseChatTheme => UseHudTheme;
        public static bool UseOverheadHealthTheme => IsAvailable;

        public static int GetHudBackground(MirClass playerClass)
        {
            return playerClass switch
            {
                MirClass.Wizard => HudWizardBackground,
                MirClass.Taoist => HudTaoistBackground,
                MirClass.Assassin => HudAssassinBackground,
                MirClass.Archer => HudArcherBackground,
                _ => HudWarriorBackground,
            };
        }
    }
}
