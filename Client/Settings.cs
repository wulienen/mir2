using Client.MirSounds;

using Client.MirObjects;
using Client.Streaming;

namespace Client
{
    public enum AssistSearchMode
    {
        VisibleOnly = 0,
        Nearby = 1,
        CurrentMap = 2
    }

    class Settings
    {
        public const long CleanDelay = 600000;

        public static int ScreenWidth = 1024, ScreenHeight = 768;
        private static InIReader Reader = new InIReader(@".\Mir2Config.ini");
        private static InIReader QuestTrackingReader = new InIReader(Path.Combine(UserDataPath, @".\QuestTracking.ini"));

        private static bool _useTestConfig;
        public static bool UseTestConfig
        {
            get
            {
                return _useTestConfig;
            }
            set
            {
                if (value == true)
                {
                    Reader = new InIReader(@".\Mir2Test.ini");
                }
                _useTestConfig = value;
            }
        }

        public const string DataPath = @".\Data\",
                            MapPath = @".\Map\",
                            SoundPath = @".\Sound\",
                            ExtraDataPath = @".\Data\Extra\",
                            ShadersPath = @".\Data\Shaders\",
                            MonsterPath = @".\Data\Monster\",
                            GatePath = @".\Data\Gate\",
                            FlagPath = @".\Data\Flag\",
                            SiegePath = @".\Data\Siege\",
                            NPCPath = @".\Data\NPC\",
                            CArmourPath = @".\Data\CArmour\",
                            CWeaponPath = @".\Data\CWeapon\",
                            CWeaponEffectPath = @".\Data\CWeaponEffect\",
                            CHairPath = @".\Data\CHair\",
                            AArmourPath = @".\Data\AArmour\",
                            AWeaponPath = @".\Data\AWeapon\",
                            AHairPath = @".\Data\AHair\",
                            ARArmourPath = @".\Data\ARArmour\",
                            ARWeaponPath = @".\Data\ARWeapon\",
                            ARHairPath = @".\Data\ARHair\",
                            CHumEffectPath = @".\Data\CHumEffect\",
                            AHumEffectPath = @".\Data\AHumEffect\",
                            ARHumEffectPath = @".\Data\ARHumEffect\",
                            MountPath = @".\Data\Mount\",
                            FishingPath = @".\Data\Fishing\",
                            PetsPath = @".\Data\Pet\",
                            TransformPath = @".\Data\Transform\",
                            TransformMountsPath = @".\Data\TransformRide2\",
                            TransformEffectPath = @".\Data\TransformEffect\",
                            TransformWeaponEffectPath = @".\Data\TransformWeaponEffect\",
                            MouseCursorPath = @".\Data\Cursors\",
                            ResourcePath = @".\DirectX\",
                            UserDataPath = @".\Data\UserData\",
                            DbLanguageJsonPath = @".\DbLanguage.json";

        //Logs
        public static bool LogErrors = true;
        public static bool LogChat = true;
        public static int RemainingErrorLogs = 100;

        //Graphics
        public static bool FullScreen = true, Borderless = true, TopMost = true, MouseClip = false;
        public const string DefaultFontName = Client.Utils.FontManager.HarmonyOSFontName;
        public static string FontName = DefaultFontName;
        public static System.Drawing.FontFamily FontFamily = System.Drawing.FontFamily.GenericSansSerif;
        public static float FontSize = 8F;
        public static bool UseMouseCursors = true;

        public static bool FPSCap = true;
        public static int MaxFPS = 100;
        public static int Resolution = 1024;
        public static bool DebugMode = false;

        //Network
        public static bool UseConfig = false;
        public static string IPAddress = "127.0.0.1";
        public static int Port = 7000;
        public const int TimeOut = 5000;

        //Sound
        public static int SoundOverLap = 3;
        private static byte _volume = 100;
        public static int SoundCleanMinutes = 5;

        public static byte Volume
        {
            get { return _volume; }
            set
            {
                switch (value)
                {
                    case > 100:
                        _volume = (byte)100;
                        break;
                    case <= 0:
                        _volume = (byte)0;
                        break;
                    default:
                        _volume = value;
                        break;
                }

                SoundManager.Vol = Convert.ToInt32(_volume);
            }
        }

        private static byte _musicVolume = 100;
        public static byte MusicVolume
        {
            get { return _musicVolume; }
            set
            {
                switch (value)
                {
                    case > 100:
                        _musicVolume = (byte)100;
                        break;
                    case <= 0:
                        _musicVolume = (byte)0;
                        break;
                    default:
                        _musicVolume = value;
                        break;
                }

                SoundManager.MusicVol = Convert.ToInt32(_musicVolume);
            }
        }

        //Game
        public static string AccountID = "",
                             Password = "";

        public static bool
            SkillMode = false,
            SkillBar = true,
            //SkillSet = true,
            Effect = true,
            LevelEffect = true,
            DropView = true,
            NameView = true,
            HPView = true,
            TransparentChat = false,
            ModeView = false,
            DuraView = false,
            DisplayDamage = true,
            TargetDead = false,
            HighlightTarget = true,
            ExpandedBuffWindow = true,
            ExpandedHeroBuffWindow = true,
            DisplayBodyName = false,
            NewMove = false,
            SmoothMove = true;

        public static string Language = "Chinese";

        public static int[,] SkillbarLocation = new int[2, 2] { { 0, 0 }, { 216, 0 } };

        //Quests
        public static int[] TrackedQuests = new int[5];

        //Chat
        public static bool
            ShowNormalChat = true,
            ShowYellChat = true,
            ShowWhisperChat = true,
            ShowLoverChat = true,
            ShowMentorChat = true,
            ShowGroupChat = true,
            ShowGuildChat = true;

        //Filters
        public static bool
            FilterNormalChat = false,
            FilterWhisperChat = false,
            FilterShoutChat = false,
            FilterSystemChat = false,
            FilterLoverChat = false,
            FilterMentorChat = false,
            FilterGroupChat = false,
            FilterGuildChat = false;


        //AutoPatcher
        public static bool P_Patcher = true;
        public static string P_Host = @"http://mirfiles.com/mir2/cmir/patch/";
        public static string P_PatchFileName = @"PList.gz";
        public static bool P_NeedLogin = false;
        public static string P_Login = string.Empty;
        public static string P_Password = string.Empty;
        public static string P_ServerName = string.Empty;
        public static string P_BrowserAddress = "https://www.lomcn.org/mir2-patchsite/";
        public static string P_Client = Application.StartupPath + "\\";
        public static bool P_AutoStart = false;
        public static bool P_AutoUpdate = false;
        public static int P_Concurrency = 1;

        //Streaming Assets
        public static bool StreamingEnabled = true;
        public static string AssetBaseUrl = @"http://127.0.0.1:8088/assets/v3/";
        public static bool PreferLocalAssets = true;
        public static int AssetDownloadConcurrency = 16;
        public static int AssetRequestTimeoutSeconds = 30;
        public static string AssetCachePath = @".\Cache\AssetsV3";
        public static int AssetCacheMaxMB = 4096;

        // Assist panel. Defaults preserve the current client behaviour; automatic actions are opt-in.
        public static bool AssistFreeShift = false,
                           AssistShowLevel = false,
                           AssistShowTransform = true,
                           AssistShowGuildName = true,
                           AssistShowGroupInfo = false,
                           AssistShowHealing = true,
                           AssistHideDead = false,
                           AssistShowMonsterNames = true,
                           AssistShowNpcNames = true,
                           AssistShowPing = false,
                           AssistShowHealthValues = false,
                           AssistHideDropNotifications = false,
                           AssistAutoFlamingSword = false,
                           AssistAutoTwinDrakeBlade = false,
                           AssistAutoMagicShield = false,
                           AssistAutoPoisonAmulet = false,
                           AssistAutoElementalBarrier = false,
                           AssistAutoAttack = false,
                           AssistAutoPickup = false,
                           AssistProtectionEnabled = false;

        public static int AssistHealthPotionPercent = 50,
                          AssistManaPotionPercent = 50,
                          AssistEmergencyPercent = 5,
                          AssistUseItemInterval = 3000;

        public static AssistSearchMode AssistHuntMode = AssistSearchMode.Nearby;

        public static Spell AssistWarriorCombatSpell = Spell.None,
                            AssistWizardCombatSpell = Spell.None,
                            AssistTaoistCombatSpell = Spell.None,
                            AssistAssassinCombatSpell = Spell.None,
                            AssistArcherCombatSpell = Spell.None;

        public static string AssistHealthPotionKeyword = "体力恢复药",
                             AssistManaPotionKeyword = "魔力恢复药",
                             AssistEmergencyKeyword = "回城卷";

        /// <summary>
        /// Records every streaming image the client touches into <c>Cache/AssetsV3/workset-usage.txt</c>, which
        /// AssetBuilder turns into the first-run working set pack. Off in a shipped client: it is a tool for
        /// producing the recording, not something a player benefits from.
        /// </summary>
        public static bool RecordWorkingSet = false;

        public static void Load()
        {
            Client.Utils.FontManager.Initialize();


            if (!Directory.Exists(DataPath)) Directory.CreateDirectory(DataPath);
            if (!Directory.Exists(MapPath)) Directory.CreateDirectory(MapPath);
            if (!Directory.Exists(SoundPath)) Directory.CreateDirectory(SoundPath);

            //Graphics
            FullScreen = Reader.ReadBoolean("Graphics", "FullScreen", FullScreen);
            Borderless = Reader.ReadBoolean("Graphics", "Borderless", Borderless);
            MouseClip = Reader.ReadBoolean("Graphics", "MouseClip", MouseClip);
            TopMost = Reader.ReadBoolean("Graphics", "AlwaysOnTop", TopMost);
            FPSCap = Reader.ReadBoolean("Graphics", "FPSCap", FPSCap);
            Resolution = Reader.ReadInt32("Graphics", "Resolution", Resolution);
            DebugMode = Reader.ReadBoolean("Graphics", "DebugMode", DebugMode);
            UseMouseCursors = Reader.ReadBoolean("Graphics", "UseMouseCursors", UseMouseCursors);

            //Network
            UseConfig = Reader.ReadBoolean("Network", "UseConfig", UseConfig);
            if (UseConfig)
            {
                IPAddress = Reader.ReadString("Network", "IPAddress", IPAddress);
                Port = Reader.ReadInt32("Network", "Port", Port);
            }

            //Logs
            LogErrors = Reader.ReadBoolean("Logs", "LogErrors", LogErrors);
            LogChat = Reader.ReadBoolean("Logs", "LogChat", LogChat);

            //Game
            AccountID = Reader.ReadString("Game", "AccountID", AccountID);
            Password = Reader.ReadString("Game", "Password", Password);

            SkillMode = Reader.ReadBoolean("Game", "SkillMode", SkillMode);
            SkillBar = Reader.ReadBoolean("Game", "SkillBar", SkillBar);
            //SkillSet = Reader.ReadBoolean("Game", "SkillSet", SkillSet);
            Effect = Reader.ReadBoolean("Game", "Effect", Effect);
            LevelEffect = Reader.ReadBoolean("Game", "LevelEffect", Effect);
            DropView = Reader.ReadBoolean("Game", "DropView", DropView);
            NameView = Reader.ReadBoolean("Game", "NameView", NameView);
            HPView = Reader.ReadBoolean("Game", "HPMPView", HPView);
            ModeView = Reader.ReadBoolean("Game", "ModeView", ModeView);
            string configuredFontName = Reader.ReadString("Game", "FontName", FontName);
            if (string.Equals(configuredFontName, "Arial", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(configuredFontName, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(configuredFontName, "Noto Sans SC", StringComparison.OrdinalIgnoreCase))
                configuredFontName = DefaultFontName;
            FontName = Client.Utils.FontManager.ResolveFontName(configuredFontName);
            FontFamily = Client.Utils.FontManager.ResolveFontFamily(FontName);
            TransparentChat = Reader.ReadBoolean("Game", "TransparentChat", TransparentChat);
            DisplayDamage = Reader.ReadBoolean("Game", "DisplayDamage", DisplayDamage);
            TargetDead = Reader.ReadBoolean("Game", "TargetDead", TargetDead);
            HighlightTarget = Reader.ReadBoolean("Game", "HighlightTarget", HighlightTarget);
            ExpandedBuffWindow = Reader.ReadBoolean("Game", "ExpandedBuffWindow", ExpandedBuffWindow);
            ExpandedHeroBuffWindow = Reader.ReadBoolean("Game", "ExpandedHeroBuffWindow", ExpandedHeroBuffWindow);
            DuraView = Reader.ReadBoolean("Game", "DuraWindow", DuraView);
            DisplayBodyName = Reader.ReadBoolean("Game", "DisplayBodyName", DisplayBodyName);
            NewMove = Reader.ReadBoolean("Game", "NewMove", NewMove);
            SmoothMove = Reader.ReadBoolean("Game", "SmoothMove", SmoothMove);
            Language = Reader.ReadString("Game", "Language", Language);

            for (int i = 0; i < SkillbarLocation.Length / 2; i++)
            {
                SkillbarLocation[i, 0] = Reader.ReadInt32("Game", "Skillbar" + i.ToString() + "X", SkillbarLocation[i, 0]);
                SkillbarLocation[i, 1] = Reader.ReadInt32("Game", "Skillbar" + i.ToString() + "Y", SkillbarLocation[i, 1]);
            }

            //Chat
            ShowNormalChat = Reader.ReadBoolean("Chat", "ShowNormalChat", ShowNormalChat);
            ShowYellChat = Reader.ReadBoolean("Chat", "ShowYellChat", ShowYellChat);
            ShowWhisperChat = Reader.ReadBoolean("Chat", "ShowWhisperChat", ShowWhisperChat);
            ShowLoverChat = Reader.ReadBoolean("Chat", "ShowLoverChat", ShowLoverChat);
            ShowMentorChat = Reader.ReadBoolean("Chat", "ShowMentorChat", ShowMentorChat);
            ShowGroupChat = Reader.ReadBoolean("Chat", "ShowGroupChat", ShowGroupChat);
            ShowGuildChat = Reader.ReadBoolean("Chat", "ShowGuildChat", ShowGuildChat);

            //Filters
            FilterNormalChat = Reader.ReadBoolean("Filter", "FilterNormalChat", FilterNormalChat);
            FilterWhisperChat = Reader.ReadBoolean("Filter", "FilterWhisperChat", FilterWhisperChat);
            FilterShoutChat = Reader.ReadBoolean("Filter", "FilterShoutChat", FilterShoutChat);
            FilterSystemChat = Reader.ReadBoolean("Filter", "FilterSystemChat", FilterSystemChat);
            FilterLoverChat = Reader.ReadBoolean("Filter", "FilterLoverChat", FilterLoverChat);
            FilterMentorChat = Reader.ReadBoolean("Filter", "FilterMentorChat", FilterMentorChat);
            FilterGroupChat = Reader.ReadBoolean("Filter", "FilterGroupChat", FilterGroupChat);
            FilterGuildChat = Reader.ReadBoolean("Filter", "FilterGuildChat", FilterGuildChat);

            //AutoPatcher
            P_Patcher = Reader.ReadBoolean("Launcher", "Enabled", P_Patcher);
            P_Host = Reader.ReadString("Launcher", "Host", P_Host);
            P_PatchFileName = Reader.ReadString("Launcher", "PatchFile", P_PatchFileName);
            P_NeedLogin = Reader.ReadBoolean("Launcher", "NeedLogin", P_NeedLogin);
            P_Login = Reader.ReadString("Launcher", "Login", P_Login);
            P_Password = Reader.ReadString("Launcher", "Password", P_Password);
            P_AutoStart = Reader.ReadBoolean("Launcher", "AutoStart", P_AutoStart);
            P_AutoUpdate = Reader.ReadBoolean("Launcher", "AutoUpdate", P_AutoUpdate);
            P_ServerName = Reader.ReadString("Launcher", "ServerName", P_ServerName);
            P_BrowserAddress = Reader.ReadString("Launcher", "Browser", P_BrowserAddress);
            P_Concurrency = Reader.ReadInt32("Launcher", "ConcurrentDownloads", P_Concurrency);

            //Streaming Assets
            StreamingEnabled = Reader.ReadBoolean("Streaming", "Enabled", StreamingEnabled);
            AssetBaseUrl = Reader.ReadString("Streaming", "AssetBaseUrl", AssetBaseUrl);
            PreferLocalAssets = Reader.ReadBoolean("Streaming", "PreferLocalAssets", PreferLocalAssets);
            AssetDownloadConcurrency = Reader.ReadInt32("Streaming", "ConcurrentDownloads", AssetDownloadConcurrency);
            AssetRequestTimeoutSeconds = Reader.ReadInt32("Streaming", "RequestTimeoutSeconds", AssetRequestTimeoutSeconds);
            AssetCachePath = Reader.ReadString("Streaming", "CachePath", AssetCachePath);
            AssetCacheMaxMB = Reader.ReadInt32("Streaming", "CacheMaxMB", AssetCacheMaxMB);
            RecordWorkingSet = Reader.ReadBoolean("Streaming", "RecordWorkingSet", RecordWorkingSet);
            if (AssetCacheMaxMB < 256) AssetCacheMaxMB = 256;
            // The V3 cache is a directory (sparse library containers plus a blob folder), not a database file.
            if (string.IsNullOrWhiteSpace(AssetCachePath) || File.Exists(AssetCachePath) ||
                AssetCachePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                AssetCachePath = @".\Cache\AssetsV3";

            if (!P_Host.EndsWith("/")) P_Host += "/";
            if (P_Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) P_Host = P_Host.Insert(0, "http://");
            if (P_BrowserAddress.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) P_BrowserAddress = P_BrowserAddress.Insert(0, "http://");
            if (!AssetBaseUrl.EndsWith("/")) AssetBaseUrl += "/";
            if (AssetBaseUrl.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) AssetBaseUrl = AssetBaseUrl.Insert(0, "http://");

            //Temp check to update everyones address
            if (P_Host.ToLower() == "http://mirfiles.co.uk/mir2/cmir/patch/")
            {
                P_Host = "http://mirfiles.com/mir2/cmir/patch/";
            }

            if (P_Concurrency < 1) P_Concurrency = 1;
            if (P_Concurrency > 100) P_Concurrency = 100;
            if (AssetDownloadConcurrency < 1) AssetDownloadConcurrency = 1;
            if (AssetDownloadConcurrency > 32) AssetDownloadConcurrency = 32;

            //Assist
            AssistFreeShift = Reader.ReadBoolean("Assist", "FreeShift", AssistFreeShift);
            AssistShowLevel = Reader.ReadBoolean("Assist", "ShowLevel", AssistShowLevel);
            AssistShowTransform = Reader.ReadBoolean("Assist", "ShowTransform", AssistShowTransform);
            AssistShowGuildName = Reader.ReadBoolean("Assist", "ShowGuildName", AssistShowGuildName);
            AssistShowGroupInfo = Reader.ReadBoolean("Assist", "ShowGroupInfo", AssistShowGroupInfo);
            AssistShowHealing = Reader.ReadBoolean("Assist", "ShowHealing", AssistShowHealing);
            AssistHideDead = Reader.ReadBoolean("Assist", "HideDead", AssistHideDead);
            AssistShowMonsterNames = Reader.ReadBoolean("Assist", "ShowMonsterNames", AssistShowMonsterNames);
            AssistShowNpcNames = Reader.ReadBoolean("Assist", "ShowNpcNames", AssistShowNpcNames);
            AssistShowPing = Reader.ReadBoolean("Assist", "ShowPing", AssistShowPing);
            AssistShowHealthValues = Reader.ReadBoolean("Assist", "ShowHealthValues", AssistShowHealthValues);
            AssistHideDropNotifications = Reader.ReadBoolean("Assist", "HideDropNotifications", AssistHideDropNotifications);
            AssistAutoFlamingSword = Reader.ReadBoolean("Assist", "AutoFlamingSword", AssistAutoFlamingSword);
            AssistAutoTwinDrakeBlade = Reader.ReadBoolean("Assist", "AutoTwinDrakeBlade", AssistAutoTwinDrakeBlade);
            AssistAutoMagicShield = Reader.ReadBoolean("Assist", "AutoMagicShield", AssistAutoMagicShield);
            AssistAutoPoisonAmulet = Reader.ReadBoolean("Assist", "AutoPoisonAmulet", AssistAutoPoisonAmulet);
            AssistAutoElementalBarrier = Reader.ReadBoolean("Assist", "AutoElementalBarrier", AssistAutoElementalBarrier);
            AssistAutoAttack = Reader.ReadBoolean("Assist", "AutoAttack", AssistAutoAttack);
            AssistAutoPickup = Reader.ReadBoolean("Assist", "AutoPickup", AssistAutoPickup);
            int huntMode = Reader.ReadInt32("Assist", "SearchMode", (int)AssistHuntMode);
            AssistHuntMode = Enum.IsDefined(typeof(AssistSearchMode), huntMode)
                ? (AssistSearchMode)huntMode
                : AssistSearchMode.Nearby;
            AssistWarriorCombatSpell = ReadAssistSpell("WarriorCombatSpell", AssistWarriorCombatSpell);
            AssistWizardCombatSpell = ReadAssistSpell("WizardCombatSpell", AssistWizardCombatSpell);
            AssistTaoistCombatSpell = ReadAssistSpell("TaoistCombatSpell", AssistTaoistCombatSpell);
            AssistAssassinCombatSpell = ReadAssistSpell("AssassinCombatSpell", AssistAssassinCombatSpell);
            AssistArcherCombatSpell = ReadAssistSpell("ArcherCombatSpell", AssistArcherCombatSpell);
            AssistProtectionEnabled = Reader.ReadBoolean("Assist", "ProtectionEnabled", AssistProtectionEnabled);
            AssistHealthPotionPercent = Math.Clamp(Reader.ReadInt32("Assist", "HealthPotionPercent", AssistHealthPotionPercent), 0, 100);
            AssistManaPotionPercent = Math.Clamp(Reader.ReadInt32("Assist", "ManaPotionPercent", AssistManaPotionPercent), 0, 100);
            AssistEmergencyPercent = Math.Clamp(Reader.ReadInt32("Assist", "EmergencyPercent", AssistEmergencyPercent), 0, 100);
            AssistUseItemInterval = Math.Clamp(Reader.ReadInt32("Assist", "UseItemInterval", AssistUseItemInterval), 250, 60000);
            AssistHealthPotionKeyword = Reader.ReadString("Assist", "HealthPotionKeyword", AssistHealthPotionKeyword);
            AssistManaPotionKeyword = Reader.ReadString("Assist", "ManaPotionKeyword", AssistManaPotionKeyword);
            AssistEmergencyKeyword = Reader.ReadString("Assist", "EmergencyKeyword", AssistEmergencyKeyword);

            try
            {
                string languageDirectory = @".\Localization\";
                if (!Directory.Exists(languageDirectory))
                {
                    Directory.CreateDirectory(languageDirectory);
                }
                string settingLanguageFile = Path.Combine(languageDirectory, Language + ".json");
                GameLanguage.LoadClientLanguage(settingLanguageFile);
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Load Client Language Error:{ex.Message}");
            }

            AssetManager.Initialize();

            // SoundManager loads the streaming sound list in its static constructor,
            // so the asset cache must be initialized before these setters touch it.
            Volume = Reader.ReadByte("Sound", "Volume", Volume);
            SoundOverLap = Reader.ReadInt32("Sound", "SoundOverLap", SoundOverLap);
            MusicVolume = Reader.ReadByte("Sound", "Music", MusicVolume);
            var n = Reader.ReadInt32("Sound", "CleanMinutes", SoundCleanMinutes);
            if (n < 1 || n > 60 * 3) n = SoundCleanMinutes;
            SoundCleanMinutes = n;
            
        }

        public static void Save()
        {
            //Graphics
            Reader.Write("Graphics", "FullScreen", FullScreen);
            Reader.Write("Graphics", "Borderless", Borderless);
            Reader.Write("Graphics", "MouseClip", MouseClip);
            Reader.Write("Graphics", "AlwaysOnTop", TopMost);
            Reader.Write("Graphics", "FPSCap", FPSCap);
            Reader.Write("Graphics", "Resolution", Resolution);
            Reader.Write("Graphics", "DebugMode", DebugMode);
            Reader.Write("Graphics", "UseMouseCursors", UseMouseCursors);

            //Sound
            Reader.Write("Sound", "Volume", Volume);
            Reader.Write("Sound", "SoundOverLap", SoundOverLap);
            Reader.Write("Sound", "Music", MusicVolume);
            Reader.Write("Sound", "CleanMinutes", SoundCleanMinutes);

            //Game
            Reader.Write("Game", "AccountID", AccountID);
            Reader.Write("Game", "Password", Password);
            Reader.Write("Game", "SkillMode", SkillMode);
            Reader.Write("Game", "SkillBar", SkillBar);
            //Reader.Write("Game", "SkillSet", SkillSet);
            Reader.Write("Game", "Effect", Effect);
            Reader.Write("Game", "LevelEffect", LevelEffect);
            Reader.Write("Game", "DropView", DropView);
            Reader.Write("Game", "NameView", NameView);
            Reader.Write("Game", "HPMPView", HPView);
            Reader.Write("Game", "ModeView", ModeView);
            Reader.Write("Game", "FontName", FontName);
            Reader.Write("Game", "TransparentChat", TransparentChat);
            Reader.Write("Game", "DisplayDamage", DisplayDamage);
            Reader.Write("Game", "TargetDead", TargetDead);
            Reader.Write("Game", "HighlightTarget", HighlightTarget);
            Reader.Write("Game", "ExpandedBuffWindow", ExpandedBuffWindow);
            Reader.Write("Game", "ExpandedHeroBuffWindow", ExpandedBuffWindow);
            Reader.Write("Game", "DuraWindow", DuraView);
            Reader.Write("Game", "DisplayBodyName", DisplayBodyName);
            Reader.Write("Game", "NewMove", NewMove);
            Reader.Write("Game", "SmoothMove", SmoothMove);
            Reader.Write("Game", "Language", Language);

            for (int i = 0; i < SkillbarLocation.Length / 2; i++)
            {

                Reader.Write("Game", "Skillbar" + i.ToString() + "X", SkillbarLocation[i, 0]);
                Reader.Write("Game", "Skillbar" + i.ToString() + "Y", SkillbarLocation[i, 1]);
            }

            //Chat
            Reader.Write("Chat", "ShowNormalChat", ShowNormalChat);
            Reader.Write("Chat", "ShowYellChat", ShowYellChat);
            Reader.Write("Chat", "ShowWhisperChat", ShowWhisperChat);
            Reader.Write("Chat", "ShowLoverChat", ShowLoverChat);
            Reader.Write("Chat", "ShowMentorChat", ShowMentorChat);
            Reader.Write("Chat", "ShowGroupChat", ShowGroupChat);
            Reader.Write("Chat", "ShowGuildChat", ShowGuildChat);

            //Filters
            Reader.Write("Filter", "FilterNormalChat", FilterNormalChat);
            Reader.Write("Filter", "FilterWhisperChat", FilterWhisperChat);
            Reader.Write("Filter", "FilterShoutChat", FilterShoutChat);
            Reader.Write("Filter", "FilterSystemChat", FilterSystemChat);
            Reader.Write("Filter", "FilterLoverChat", FilterLoverChat);
            Reader.Write("Filter", "FilterMentorChat", FilterMentorChat);
            Reader.Write("Filter", "FilterGroupChat", FilterGroupChat);
            Reader.Write("Filter", "FilterGuildChat", FilterGuildChat);

            //AutoPatcher
            Reader.Write("Launcher", "Enabled", P_Patcher);
            Reader.Write("Launcher", "Host", P_Host);
            Reader.Write("Launcher", "PatchFile", P_PatchFileName);
            Reader.Write("Launcher", "NeedLogin", P_NeedLogin);
            Reader.Write("Launcher", "Login", P_Login);
            Reader.Write("Launcher", "Password", P_Password);
            Reader.Write("Launcher", "ServerName", P_ServerName);
            Reader.Write("Launcher", "Browser", P_BrowserAddress);
            Reader.Write("Launcher", "AutoStart", P_AutoStart);
            Reader.Write("Launcher", "AutoUpdate", P_AutoUpdate);
            Reader.Write("Launcher", "ConcurrentDownloads", P_Concurrency);

            //Streaming Assets
            Reader.Write("Streaming", "Enabled", StreamingEnabled);
            Reader.Write("Streaming", "AssetBaseUrl", AssetBaseUrl);
            Reader.Write("Streaming", "PreferLocalAssets", PreferLocalAssets);
            Reader.Write("Streaming", "ConcurrentDownloads", AssetDownloadConcurrency);
            Reader.Write("Streaming", "RequestTimeoutSeconds", AssetRequestTimeoutSeconds);
            Reader.Write("Streaming", "CachePath", AssetCachePath);
            Reader.Write("Streaming", "CacheMaxMB", AssetCacheMaxMB);
            Reader.Write("Streaming", "RecordWorkingSet", RecordWorkingSet);

            //Assist
            Reader.Write("Assist", "FreeShift", AssistFreeShift);
            Reader.Write("Assist", "ShowLevel", AssistShowLevel);
            Reader.Write("Assist", "ShowTransform", AssistShowTransform);
            Reader.Write("Assist", "ShowGuildName", AssistShowGuildName);
            Reader.Write("Assist", "ShowGroupInfo", AssistShowGroupInfo);
            Reader.Write("Assist", "ShowHealing", AssistShowHealing);
            Reader.Write("Assist", "HideDead", AssistHideDead);
            Reader.Write("Assist", "ShowMonsterNames", AssistShowMonsterNames);
            Reader.Write("Assist", "ShowNpcNames", AssistShowNpcNames);
            Reader.Write("Assist", "ShowPing", AssistShowPing);
            Reader.Write("Assist", "ShowHealthValues", AssistShowHealthValues);
            Reader.Write("Assist", "HideDropNotifications", AssistHideDropNotifications);
            Reader.Write("Assist", "AutoFlamingSword", AssistAutoFlamingSword);
            Reader.Write("Assist", "AutoTwinDrakeBlade", AssistAutoTwinDrakeBlade);
            Reader.Write("Assist", "AutoMagicShield", AssistAutoMagicShield);
            Reader.Write("Assist", "AutoPoisonAmulet", AssistAutoPoisonAmulet);
            Reader.Write("Assist", "AutoElementalBarrier", AssistAutoElementalBarrier);
            Reader.Write("Assist", "AutoAttack", AssistAutoAttack);
            Reader.Write("Assist", "AutoPickup", AssistAutoPickup);
            Reader.Write("Assist", "SearchMode", (int)AssistHuntMode);
            Reader.Write("Assist", "WarriorCombatSpell", (int)AssistWarriorCombatSpell);
            Reader.Write("Assist", "WizardCombatSpell", (int)AssistWizardCombatSpell);
            Reader.Write("Assist", "TaoistCombatSpell", (int)AssistTaoistCombatSpell);
            Reader.Write("Assist", "AssassinCombatSpell", (int)AssistAssassinCombatSpell);
            Reader.Write("Assist", "ArcherCombatSpell", (int)AssistArcherCombatSpell);
            Reader.Write("Assist", "ProtectionEnabled", AssistProtectionEnabled);
            Reader.Write("Assist", "HealthPotionPercent", AssistHealthPotionPercent);
            Reader.Write("Assist", "ManaPotionPercent", AssistManaPotionPercent);
            Reader.Write("Assist", "EmergencyPercent", AssistEmergencyPercent);
            Reader.Write("Assist", "UseItemInterval", AssistUseItemInterval);
            Reader.Write("Assist", "HealthPotionKeyword", AssistHealthPotionKeyword);
            Reader.Write("Assist", "ManaPotionKeyword", AssistManaPotionKeyword);
            Reader.Write("Assist", "EmergencyKeyword", AssistEmergencyKeyword);
        }

        public static Spell GetAssistCombatSpell(MirClass mirClass)
        {
            return mirClass switch
            {
                MirClass.Warrior => AssistWarriorCombatSpell,
                MirClass.Wizard => AssistWizardCombatSpell,
                MirClass.Taoist => AssistTaoistCombatSpell,
                MirClass.Assassin => AssistAssassinCombatSpell,
                MirClass.Archer => AssistArcherCombatSpell,
                _ => Spell.None
            };
        }

        public static void SetAssistCombatSpell(MirClass mirClass, Spell spell)
        {
            if (spell != Spell.None && !AssistController.IsAutoCombatSpell(spell))
                spell = Spell.None;

            switch (mirClass)
            {
                case MirClass.Warrior:
                    AssistWarriorCombatSpell = spell;
                    break;
                case MirClass.Wizard:
                    AssistWizardCombatSpell = spell;
                    break;
                case MirClass.Taoist:
                    AssistTaoistCombatSpell = spell;
                    break;
                case MirClass.Assassin:
                    AssistAssassinCombatSpell = spell;
                    break;
                case MirClass.Archer:
                    AssistArcherCombatSpell = spell;
                    break;
            }
        }

        private static Spell ReadAssistSpell(string key, Spell defaultValue)
        {
            int value = Reader.ReadInt32("Assist", key, (int)defaultValue);
            Spell spell = value >= byte.MinValue && value <= byte.MaxValue && Enum.IsDefined(typeof(Spell), (byte)value)
                ? (Spell)(byte)value
                : Spell.None;
            return spell == Spell.None || AssistController.IsAutoCombatSpell(spell) ? spell : Spell.None;
        }

        public static void LoadTrackedQuests(string charName)
        {
            //Quests
            for (int i = 0; i < TrackedQuests.Length; i++)
            {
                TrackedQuests[i] = QuestTrackingReader.ReadInt32(charName, "Quest-" + i.ToString(), -1);
            }
        }

        public static void SaveTrackedQuests(string charName)
        {
            //Quests
            for (int i = 0; i < TrackedQuests.Length; i++)
            {
                QuestTrackingReader.Write(charName, "Quest-" + i.ToString(), TrackedQuests[i]);
            }
        }
    }


}
