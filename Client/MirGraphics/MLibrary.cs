using SlimDX;
using SlimDX.Direct3D9;
using System.IO.Compression;
using Frame = Client.MirObjects.Frame;
using Client.MirObjects;
using System.Text.RegularExpressions;

namespace Client.MirGraphics
{
    public static class Libraries
    {
        public static bool Loaded;
        public static int Count, Progress;
        
        // 场景感知加载状态
        private static bool _loginLibrariesInitialized;
        private static bool _gameLibrariesInitialized;
        private static bool _characterLibrariesCreated;
        private static readonly object _initLock = new object();

        // ===== 登录场景库（Login Scene Libraries）=====
        // 这些库在登录场景时初始化
        public static readonly MLibrary
            ChrSel = new MLibrary(Settings.DataPath + "ChrSel"),
            Prguse = new MLibrary(Settings.DataPath + "Prguse"),
            Prguse2 = new MLibrary(Settings.DataPath + "Prguse2"),
            Prguse3 = new MLibrary(Settings.DataPath + "Prguse3"),
            UI_32bit = new MLibrary(Settings.DataPath + "UI_32bit"),
            Title = new MLibrary(Settings.DataPath + "Title");

        // ===== 游戏场景库（Game Scene Libraries）=====
        // 这些库在进入游戏场景时初始化
        public static readonly MLibrary
            BuffIcon = new MLibrary(Settings.DataPath + "BuffIcon"),
            Help = new MLibrary(Settings.DataPath + "Help"),
            MiniMap = new MLibrary(Settings.DataPath + "MMap"),
            MapLinkIcon = new MLibrary(Settings.DataPath + "MapLinkIcon"),
            MagIcon = new MLibrary(Settings.DataPath + "MagIcon"),
            MagIcon2 = new MLibrary(Settings.DataPath + "MagIcon2"),
            Magic = new MLibrary(Settings.DataPath + "Magic"),
            Magic2 = new MLibrary(Settings.DataPath + "Magic2"),
            Magic3 = new MLibrary(Settings.DataPath + "Magic3"),
            Effect = new MLibrary(Settings.DataPath + "Effect"),
            MagicC = new MLibrary(Settings.DataPath + "MagicC"),
            GuildSkill = new MLibrary(Settings.DataPath + "GuildSkill"),
            Weather = new MLibrary(Settings.DataPath + "Weather");

        public static readonly MLibrary
            Background = new MLibrary(Settings.DataPath + "Background");

        public static readonly MLibrary
            Dragon = new MLibrary(Settings.DataPath + "Dragon");

        // ===== 地图库（Map Libraries）- 延迟加载 =====
        // 这些库只在需要时按需初始化
        public static readonly MLibrary[] MapLibs = new MLibrary[400];
        
        // 记录MapLib路径，用于延迟创建
        private static readonly string[] MapLibPaths = new string[400];

        // ===== 物品库（Items Libraries）=====
        public static readonly MLibrary
            Items = new MLibrary(Settings.DataPath + "Items"),
            StateItems = new MLibrary(Settings.DataPath + "StateItem"),
            FloorItems = new MLibrary(Settings.DataPath + "DNItems"),
            Items_Tooltip_32bit = new MLibrary(Settings.DataPath + "Items_Tooltip_32bit");

        // ===== 装饰库（Deco Libraries）=====
        public static readonly MLibrary
            Deco = new MLibrary(Settings.DataPath + "Deco");

        // ===== 角色库（Character Libraries）- 延迟创建 =====
        public static MLibrary[] CArmours,
                                          CWeapons,
										  CWeaponEffect,
										  CHair,
                                          CHumEffect,
                                          AArmours,
                                          AWeaponsL,
                                          AWeaponsR,
                                          AHair,
                                          AHumEffect,
                                          ARArmours,
                                          ARWeapons,
                                          ARWeaponsS,
                                          ARHair,
                                          ARHumEffect,
                                          Monsters,
                                          Gates,
                                          Flags,
                                          Siege,
                                          Mounts,
                                          NPCs,
                                          Fishing,
                                          Pets,
                                          Transform,
                                          TransformMounts,
                                          TransformEffect,
                                          TransformWeaponEffect;

        /// <summary>
        /// 静态构造函数 - 只设置MapLib路径，不进行初始化
        /// 实际初始化由 InitializeForLogin() 和 InitializeForGame() 完成
        /// </summary>
        static Libraries()
        {
            // 设置MapLib路径（不创建MLibrary对象）
            SetupMapLibPaths();
        }

        /// <summary>
        /// 设置MapLib路径，用于延迟加载
        /// </summary>
        private static void SetupMapLibPaths()
        {
            #region Maplibs Paths
            //wemade mir2 (allowed from 0-99)
            MapLibPaths[0] = Settings.DataPath + "Map\\WemadeMir2\\Tiles";
            MapLibPaths[1] = Settings.DataPath + "Map\\WemadeMir2\\Smtiles";
            MapLibPaths[2] = Settings.DataPath + "Map\\WemadeMir2\\Objects";
            for (int i = 2; i < 28; i++)
            {
                MapLibPaths[i + 1] = Settings.DataPath + "Map\\WemadeMir2\\Objects" + i.ToString();
            }
            MapLibPaths[90] = Settings.DataPath + "Map\\WemadeMir2\\Objects_32bit";

            //shanda mir2 (allowed from 100-199)
            MapLibPaths[100] = Settings.DataPath + "Map\\ShandaMir2\\Tiles";
            for (int i = 1; i < 10; i++)
            {
                MapLibPaths[100 + i] = Settings.DataPath + "Map\\ShandaMir2\\Tiles" + (i + 1);
            }
            MapLibPaths[110] = Settings.DataPath + "Map\\ShandaMir2\\SmTiles";
            for (int i = 1; i < 10; i++)
            {
                MapLibPaths[110 + i] = Settings.DataPath + "Map\\ShandaMir2\\SmTiles" + (i + 1);
            }
            MapLibPaths[120] = Settings.DataPath + "Map\\ShandaMir2\\Objects";
            for (int i = 1; i < 31; i++)
            {
                MapLibPaths[120 + i] = Settings.DataPath + "Map\\ShandaMir2\\Objects" + (i + 1);
            }
            MapLibPaths[190] = Settings.DataPath + "Map\\ShandaMir2\\AniTiles1";
            
            //wemade mir3 (allowed from 200-299)
            string[] Mapstate = { "", "wood\\", "sand\\", "snow\\", "forest\\"};
            for (int i = 0; i < Mapstate.Length; i++)
            {
                MapLibPaths[200 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Tilesc";
                MapLibPaths[201 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Tiles30c";
                MapLibPaths[202 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Tiles5c";
                MapLibPaths[203 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Smtilesc";
                MapLibPaths[204 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Housesc";
                MapLibPaths[205 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Cliffsc";
                MapLibPaths[206 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Dungeonsc";
                MapLibPaths[207 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Innersc";
                MapLibPaths[208 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Furnituresc";
                MapLibPaths[209 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Wallsc";
                MapLibPaths[210 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "smObjectsc";
                MapLibPaths[211 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Animationsc";
                MapLibPaths[212 +(i*15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Object1c";
                MapLibPaths[213 + (i * 15)] = Settings.DataPath + "Map\\WemadeMir3\\" + Mapstate[i] + "Object2c";
            }
            Mapstate = new string[] { "", "wood", "sand", "snow", "forest"};
            //shanda mir3 (allowed from 300-399)
            for (int i = 0; i < Mapstate.Length; i++)
            {
                MapLibPaths[300 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Tilesc" + Mapstate[i];
                MapLibPaths[301 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Tiles30c" + Mapstate[i];
                MapLibPaths[302 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Tiles5c" + Mapstate[i];
                MapLibPaths[303 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Smtilesc" + Mapstate[i];
                MapLibPaths[304 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Housesc" + Mapstate[i];
                MapLibPaths[305 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Cliffsc" + Mapstate[i];
                MapLibPaths[306 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Dungeonsc" + Mapstate[i];
                MapLibPaths[307 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Innersc" + Mapstate[i];
                MapLibPaths[308 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Furnituresc" + Mapstate[i];
                MapLibPaths[309 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Wallsc" + Mapstate[i];
                MapLibPaths[310 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "smObjectsc" + Mapstate[i];
                MapLibPaths[311 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Animationsc" + Mapstate[i];
                MapLibPaths[312 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Object1c" + Mapstate[i];
                MapLibPaths[313 + (i * 15)] = Settings.DataPath + "Map\\ShandaMir3\\" + "Object2c" + Mapstate[i];
            }
            #endregion
        }

        /// <summary>
        /// 确保角色相关库数组已创建（不初始化内容）
        /// </summary>
        private static void EnsureCharacterLibrariesCreated()
        {
            if (_characterLibrariesCreated) return;
            
            lock (_initLock)
            {
                if (_characterLibrariesCreated) return;
                
                //Wiz/War/Tao
                InitLibrary(ref CArmours, Settings.CArmourPath, "00");
                InitLibrary(ref CHair, Settings.CHairPath, "00");
                InitLibrary(ref CWeapons, Settings.CWeaponPath, "00");
                InitLibrary(ref CWeaponEffect, Settings.CWeaponEffectPath, "00");
                InitLibrary(ref CHumEffect, Settings.CHumEffectPath, "00");

                //Assassin
                InitLibrary(ref AArmours, Settings.AArmourPath, "00");
                InitLibrary(ref AHair, Settings.AHairPath, "00");
                InitLibrary(ref AWeaponsL, Settings.AWeaponPath, "00", " L");
                InitLibrary(ref AWeaponsR, Settings.AWeaponPath, "00", " R");
                InitLibrary(ref AHumEffect, Settings.AHumEffectPath, "00");

                //Archer
                InitLibrary(ref ARArmours, Settings.ARArmourPath, "00");
                InitLibrary(ref ARHair, Settings.ARHairPath, "00");
                InitLibrary(ref ARWeapons, Settings.ARWeaponPath, "00");
                InitLibrary(ref ARWeaponsS, Settings.ARWeaponPath, "00", " S");
                InitLibrary(ref ARHumEffect, Settings.ARHumEffectPath, "00");

                //Other
                InitLibrary(ref Monsters, Settings.MonsterPath, "000");
                InitLibrary(ref Gates, Settings.GatePath, "00");
                InitLibrary(ref Flags, Settings.FlagPath, "00");
                InitLibrary(ref Siege, Settings.SiegePath, "00");
                InitLibrary(ref NPCs, Settings.NPCPath, "00");
                InitLibrary(ref Mounts, Settings.MountPath, "00");
                InitLibrary(ref Fishing, Settings.FishingPath, "00");
                InitLibrary(ref Pets, Settings.PetsPath, "00");
                InitLibrary(ref Transform, Settings.TransformPath, "00");
                InitLibrary(ref TransformMounts, Settings.TransformMountsPath, "00");
                InitLibrary(ref TransformEffect, Settings.TransformEffectPath, "00");
                InitLibrary(ref TransformWeaponEffect, Settings.TransformWeaponEffectPath, "00");
                
                _characterLibrariesCreated = true;
            }
        }

        /// <summary>
        /// 初始化登录场景所需的库
        /// 只初始化 ChrSel, Prguse, Prguse2, Prguse3, UI_32bit, Title
        /// </summary>
        public static void InitializeForLogin()
        {
            if (_loginLibrariesInitialized) return;
            
            lock (_initLock)
            {
                if (_loginLibrariesInitialized) return;
                
                ChrSel.Initialize();
                Progress++;

                Prguse.Initialize();
                Progress++;

                Prguse2.Initialize();
                Progress++;

                Prguse3.Initialize();
                Progress++;

                UI_32bit.Initialize();
                Progress++;

                Title.Initialize();
                Progress++;
                
                _loginLibrariesInitialized = true;
            }
        }

        /// <summary>
        /// 初始化游戏场景所需的库
        /// 在后台线程中初始化游戏相关库
        /// </summary>
        public static void InitializeForGame()
        {
            if (_gameLibrariesInitialized) return;
            
            lock (_initLock)
            {
                if (_gameLibrariesInitialized) return;
                
                // 确保登录库已初始化
                if (!_loginLibrariesInitialized)
                {
                    InitializeForLogin();
                }
                
                // 确保角色库数组已创建
                EnsureCharacterLibrariesCreated();
                
                _gameLibrariesInitialized = true;
                
                // 在后台线程中加载游戏库
                Thread thread = new Thread(LoadGameLibraries) { IsBackground = true };
                thread.Start();
            }
        }

        /// <summary>
        /// 获取指定索引的MapLib，如果未创建则延迟创建并初始化
        /// </summary>
        /// <param name="index">MapLib索引</param>
        /// <returns>MLibrary对象，如果索引无效则返回null</returns>
        public static MLibrary GetMapLib(int index)
        {
            if (index < 0 || index >= MapLibs.Length)
                return null;
            
            if (MapLibs[index] == null)
            {
                lock (_initLock)
                {
                    if (MapLibs[index] == null)
                    {
                        string path = MapLibPaths[index];
                        if (!string.IsNullOrEmpty(path))
                        {
                            MapLibs[index] = new MLibrary(path);
                            MapLibs[index].Initialize();
                        }
                        else
                        {
                            // 路径为空，创建空库
                            MapLibs[index] = new MLibrary("");
                        }
                    }
                }
            }
            
            return MapLibs[index];
        }

        /// <summary>
        /// 批量初始化指定的MapLib索引
        /// 用于地图加载时预加载所需的MapLib
        /// </summary>
        /// <param name="indices">需要初始化的MapLib索引数组</param>
        public static void InitializeMapLibs(int[] indices)
        {
            if (indices == null) return;
            
            foreach (int index in indices)
            {
                GetMapLib(index);
            }
        }

        static void InitLibrary(ref MLibrary[] library, string path, string toStringValue, string suffix = "")
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            var allFiles = Directory.GetFiles(path, "*" + suffix + MLibrary.Extention, SearchOption.TopDirectoryOnly).OrderBy(x => int.Parse(Regex.Match(x, @"\d+").Value));

            var lastFile = allFiles.Count() > 0 ? Path.GetFileName(allFiles.Last()) : "0";

            var count = int.Parse(Regex.Match(lastFile, @"\d+").Value) + 1;

            library = new MLibrary[count];

            for (int i = 0; i < count; i++)
            {
                library[i] = new MLibrary(path + i.ToString(toStringValue) + suffix);
            }
        }

        /// <summary>
        /// 加载登录场景库（已废弃，使用 InitializeForLogin 代替）
        /// 保留此方法以兼容旧代码
        /// </summary>
        [Obsolete("Use InitializeForLogin() instead")]
        static void LoadLibraries()
        {
            InitializeForLogin();
        }

        private static void LoadGameLibraries()
        {
            // 确保角色库数组已创建
            EnsureCharacterLibrariesCreated();
            
            // 计算总数时不包含MapLibs（延迟加载）
            Count = Monsters.Length + Gates.Length + Flags.Length + Siege.Length + NPCs.Length + CArmours.Length +
                CHair.Length + CWeapons.Length + CWeaponEffect.Length + AArmours.Length + AHair.Length + AWeaponsL.Length + AWeaponsR.Length +
                ARArmours.Length + ARHair.Length + ARWeapons.Length + ARWeaponsS.Length +
                CHumEffect.Length + AHumEffect.Length + ARHumEffect.Length + Mounts.Length + Fishing.Length + Pets.Length +
                Transform.Length + TransformMounts.Length + TransformEffect.Length + TransformWeaponEffect.Length + 19;

            Dragon.Initialize();
            Progress++;

            BuffIcon.Initialize();
            Progress++;

            Help.Initialize();
            Progress++;

            MiniMap.Initialize();
            Progress++;
            MapLinkIcon.Initialize();
            Progress++;

            MagIcon.Initialize();
            Progress++;
            MagIcon2.Initialize();
            Progress++;

            Magic.Initialize();
            Progress++;
            Magic2.Initialize();
            Progress++;
            Magic3.Initialize();
            Progress++;
            MagicC.Initialize();
            Progress++;

            Effect.Initialize();
            Progress++;

            Weather.Initialize();
            Progress++;

            GuildSkill.Initialize();
            Progress++;

            Background.Initialize();
            Progress++;

            Deco.Initialize();
            Progress++;

            Items.Initialize();
            Progress++;
            StateItems.Initialize();
            Progress++;
            FloorItems.Initialize();
            Progress++;
            Items_Tooltip_32bit.Initialize();
            Progress++;

            // MapLibs 不在启动时初始化，改为延迟加载
            // 使用 GetMapLib(index) 或 InitializeMapLibs(indices) 按需加载

            for (int i = 0; i < Monsters.Length; i++)
            {
                Monsters[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < Gates.Length; i++)
            {
                Gates[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < Flags.Length; i++)
            {
                Flags[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < Siege.Length; i++)
            {
                Siege[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < NPCs.Length; i++)
            {
                NPCs[i].Initialize();
                Progress++;
            }


            for (int i = 0; i < CArmours.Length; i++)
            {
                CArmours[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < CHair.Length; i++)
            {
                CHair[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < CWeapons.Length; i++)
            {
                CWeapons[i].Initialize();
                Progress++;
            }

			for (int i = 0; i < CWeaponEffect.Length; i++)
			{
				CWeaponEffect[i].Initialize();
				Progress++;
			}

			for (int i = 0; i < AArmours.Length; i++)
            {
                AArmours[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < AHair.Length; i++)
            {
                AHair[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < AWeaponsL.Length; i++)
            {
                AWeaponsL[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < AWeaponsR.Length; i++)
            {
                AWeaponsR[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < ARArmours.Length; i++)
            {
                ARArmours[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < ARHair.Length; i++)
            {
                ARHair[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < ARWeapons.Length; i++)
            {
                ARWeapons[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < ARWeaponsS.Length; i++)
            {
                ARWeaponsS[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < CHumEffect.Length; i++)
            {
                CHumEffect[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < AHumEffect.Length; i++)
            {
                AHumEffect[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < ARHumEffect.Length; i++)
            {
                ARHumEffect[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < Mounts.Length; i++)
            {
                Mounts[i].Initialize();
                Progress++;
            }


            for (int i = 0; i < Fishing.Length; i++)
            {
                Fishing[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < Pets.Length; i++)
            {
                Pets[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < Transform.Length; i++)
            {
                Transform[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < TransformEffect.Length; i++)
            {
                TransformEffect[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < TransformWeaponEffect.Length; i++)
            {
                TransformWeaponEffect[i].Initialize();
                Progress++;
            }

            for (int i = 0; i < TransformMounts.Length; i++)
            {
                TransformMounts[i].Initialize();
                Progress++;
            }
            
            Loaded = true;
        }

    }

    public sealed class MLibrary
    {
        public const string Extention = ".Lib";
        public const int LibVersion = 3;

        private readonly string _fileName;

        private MImage[] _images;
        private FrameSet _frames;
        private int[] _indexList;
        private int _count;
        private bool _initialized;

        private BinaryReader _reader;
        private FileStream _fStream;

        // 流式加载状态字段
        /// <summary>
        /// 头文件加载状态: 0=未加载, 1=下载中, 2=已加载
        /// </summary>
        private byte _headerStatus;
        
        /// <summary>
        /// 下次重试时间
        /// </summary>
        private DateTime _nextRetryTime;
        
        /// <summary>
        /// 加载锁，防止并发初始化
        /// </summary>
        private readonly object _loadLock = new object();

        public FrameSet Frames
        {
            get { return _frames; }
        }

        public MLibrary(string filename)
        {
            _fileName = Path.ChangeExtension(filename, Extention);
        }

        public void Initialize()
        {
            _initialized = true;

            if (!File.Exists(_fileName))
            {
                // 如果启用流式加载，触发头文件下载（即使服务器当前不可用也标记需要下载）
                if (Settings.MicroClientEnabled)
                {
                    // 如果服务器可用，立即尝试下载
                    if (ResourceHelper.ServerActive)
                    {
                        InitializeFromServer();
                    }
                    else
                    {
                        // 服务器不可用，标记为未初始化，等待后续重试
                        _initialized = false;
                        _headerStatus = 0;
                    }
                }
                return;
            }

            try
            {
                // 使用 FileShare.ReadWrite 允许 ProcessPendingWrites 同时写入文件
                _fStream = new FileStream(_fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _reader = new BinaryReader(_fStream);
                
                // 检查文件是否有足够的数据读取基本头部（至少8字节：version + count）
                if (_fStream.Length < 8)
                {
                    // 文件太小，可能是损坏的或不完整的
                    _fStream.Dispose();
                    _reader.Dispose();
                    _fStream = null;
                    _reader = null;
                    
                    // 如果启用流式加载，删除损坏的文件并重新下载
                    if (Settings.MicroClientEnabled)
                    {
                        try { File.Delete(_fileName); } catch { }
                        if (ResourceHelper.ServerActive)
                        {
                            InitializeFromServer();
                        }
                    }
                    return;
                }
                
                int currentVersion = _reader.ReadInt32();
                if (currentVersion < 2)
                {
                    System.Windows.Forms.MessageBox.Show("Wrong version, expecting lib version: " + LibVersion.ToString() + " found version: " + currentVersion.ToString() + ".", _fileName, System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error, System.Windows.Forms.MessageBoxDefaultButton.Button1);
                    System.Windows.Forms.Application.Exit();
                    return;
                }
                _count = _reader.ReadInt32();

                int frameSeek = 0;
                if (currentVersion >= 3)
                {
                    // 检查是否有足够数据读取 frameSeek
                    if (_fStream.Length < 12)
                    {
                        _fStream.Dispose();
                        _reader.Dispose();
                        _fStream = null;
                        _reader = null;
                        if (Settings.MicroClientEnabled)
                        {
                            try { File.Delete(_fileName); } catch { }
                            if (ResourceHelper.ServerActive)
                            {
                                InitializeFromServer();
                            }
                        }
                        return;
                    }
                    frameSeek = _reader.ReadInt32();
                }

                // 计算头部所需的最小长度
                int headerBaseLength = 4 + 4 + (currentVersion >= 3 ? 4 : 0);
                int indexListLength = 4 * _count;
                int requiredHeaderLength = headerBaseLength + indexListLength;
                
                // 检查文件是否有足够数据读取索引列表
                if (_fStream.Length < requiredHeaderLength)
                {
                    _fStream.Dispose();
                    _reader.Dispose();
                    _fStream = null;
                    _reader = null;
                    
                    // 文件不完整，删除并重新下载
                    if (Settings.MicroClientEnabled)
                    {
                        try { File.Delete(_fileName); } catch { }
                        if (ResourceHelper.ServerActive)
                        {
                            InitializeFromServer();
                        }
                    }
                    return;
                }

                _images = new MImage[_count];
                _indexList = new int[_count];

                for (int i = 0; i < _count; i++)
                    _indexList[i] = _reader.ReadInt32();

                if (currentVersion >= 3 && frameSeek > 0 && frameSeek < _fStream.Length)
                {
                    _fStream.Seek(frameSeek, SeekOrigin.Begin);

                    // 检查是否有足够数据读取 frameCount
                    if (_fStream.Position + 4 <= _fStream.Length)
                    {
                        var frameCount = _reader.ReadInt32();

                        if (frameCount > 0)
                        {
                            _frames = new FrameSet();
                            for (int i = 0; i < frameCount; i++)
                            {
                                // 检查是否有足够数据读取帧数据（至少需要1字节MirAction + Frame数据）
                                if (_fStream.Position >= _fStream.Length)
                                    break;
                                
                                try
                                {
                                    _frames.Add((MirAction)_reader.ReadByte(), new Frame(_reader));
                                }
                                catch (EndOfStreamException)
                                {
                                    // 帧数据不完整，停止读取
                                    break;
                                }
                            }
                        }
                    }
                }
                
                _headerStatus = 2; // 标记头文件已加载
            }
            catch (EndOfStreamException)
            {
                // 文件不完整，清理并尝试重新下载
                CleanupAndRetryDownload();
                _initialized = false;
            }
            catch (IOException)
            {
                // 文件I/O错误，清理并尝试重新下载
                CleanupAndRetryDownload();
                _initialized = false;
            }
            catch (Exception)
            {
                _initialized = false;
                throw;
            }
        }

        /// <summary>
        /// 清理资源并尝试重新下载（用于文件损坏或不完整的情况）
        /// </summary>
        private void CleanupAndRetryDownload()
        {
            // 清理文件流
            if (_fStream != null) 
            { 
                try { _fStream.Dispose(); } catch { }
                _fStream = null; 
            }
            if (_reader != null) 
            { 
                try { _reader.Dispose(); } catch { }
                _reader = null; 
            }
            
            // 清理已创建的图片数组（防止部分损坏的MImage对象）
            _images = null;
            _indexList = null;
            _frames = null;
            _count = 0;
            _headerStatus = 0;
            
            // 如果启用流式加载，删除损坏的文件并重新下载
            if (Settings.MicroClientEnabled)
            {
                try { File.Delete(_fileName); } catch { }
                if (ResourceHelper.ServerActive)
                {
                    InitializeFromServer();
                }
            }
        }

        /// <summary>
        /// 从服务器初始化库文件（异步下载头文件）
        /// </summary>
        private async void InitializeFromServer()
        {
            lock (_loadLock)
            {
                if (_headerStatus == 1) // 正在下载中
                    return;
                
                if (_headerStatus == 2) // 已加载
                    return;
                
                if (DateTime.Now < _nextRetryTime) // 未到重试时间
                    return;
                
                _headerStatus = 1; // 标记为下载中
            }

            try
            {
                bool success = await ResourceHelper.GetHeaderAsync(_fileName);
                
                if (success && File.Exists(_fileName))
                {
                    // 头文件下载成功，重新初始化
                    _initialized = false;
                    _headerStatus = 0;
                    Initialize();
                }
                else
                {
                    // 下载失败，1秒后重试
                    _headerStatus = 0;
                    _nextRetryTime = DateTime.Now.AddSeconds(1);
                }
            }
            catch
            {
                // 异常，1秒后重试
                _headerStatus = 0;
                _nextRetryTime = DateTime.Now.AddSeconds(1);
            }
        }

        private bool CheckImage(int index)
        {
            // 如果未初始化且服务器可用，尝试初始化
            if (!_initialized)
            {
                if (Settings.MicroClientEnabled && ResourceHelper.ServerActive)
                {
                    Initialize();
                }
                else
                {
                    Initialize(); // 尝试本地初始化
                }
            }

            // 如果文件不存在且服务器可用，尝试从服务器下载
            if (!File.Exists(_fileName) && Settings.MicroClientEnabled && ResourceHelper.ServerActive)
            {
                if (_headerStatus == 0)
                {
                    InitializeFromServer();
                }
                return false;
            }

            if (_images == null || index < 0 || index >= _images.Length)
                return false;
            
            // 检查文件流是否可用
            if (_fStream == null || _indexList == null)
                return false;

            if (_images[index] == null)
            {
                int imagePosition = _indexList[index];
                
                // 验证索引位置有效性
                if (imagePosition <= 0)
                    return false;
                
                // 流式加载：检查图片头位置是否超出文件范围
                if (Settings.MicroClientEnabled && imagePosition + 17 > _fStream.Length)
                {
                    // 文件中没有图片头数据，需要从服务器下载
                    if (ResourceHelper.ServerActive)
                    {
                        // 触发下载完整图片数据（包含17字节头）
                        DownloadFullImageAsync(index, imagePosition);
                    }
                    return false;
                }
                
                try
                {
                    _fStream.Position = imagePosition;
                    _images[index] = new MImage(_reader);
                }
                catch (EndOfStreamException)
                {
                    // 文件不完整，无法读取图片头
                    _images[index] = null;
                    if (Settings.MicroClientEnabled && ResourceHelper.ServerActive)
                    {
                        DownloadFullImageAsync(index, imagePosition);
                    }
                    return false;
                }
                catch (IOException)
                {
                    // 文件I/O错误
                    _images[index] = null;
                    return false;
                }
                catch (Exception)
                {
                    // 其他异常，返回false而不是崩溃
                    _images[index] = null;
                    return false;
                }
            }
            MImage mi = _images[index];
            if (mi == null)
                return false;
                
            if (!mi.TextureValid)
            {
                // 流式加载：检查图片头数据是否有效（Width/Height 为 0 表示头数据为空）
                if ((mi.Width == 0) || (mi.Height == 0))
                {
                    // 如果启用流式加载且服务器可用，触发下载
                    if (Settings.MicroClientEnabled && ResourceHelper.ServerActive)
                    {
                        // 检查是否可以重试
                        if (mi.DownloadStatus == 0 && DateTime.Now >= mi.NextRetryTime)
                        {
                            int imagePosition = _indexList[index];
                            // 标记为下载中，防止重复触发
                            mi.DownloadStatus = 1;
                            DownloadFullImageAsync(index, imagePosition);
                        }
                    }
                    return false;
                }
                
                // 流式加载：检查图片数据是否为空
                if (Settings.MicroClientEnabled && ResourceHelper.ServerActive)
                {
                    // 检查是否需要下载
                    if (mi.DownloadStatus == 0 && DateTime.Now >= mi.NextRetryTime && IsImageDataEmpty(index))
                    {
                        // 触发异步下载
                        DownloadImageAsync(index, mi);
                        return false; // 返回false，让调用方使用占位纹理
                    }
                    
                    // 正在下载中，返回false
                    if (mi.DownloadStatus == 1)
                    {
                        return false;
                    }
                }
                
                try
                {
                    // 验证数据位置有效性
                    int dataPosition = _indexList[index] + 17;
                    if (dataPosition + mi.Length > _fStream.Length)
                    {
                        // 数据不完整，触发下载
                        if (Settings.MicroClientEnabled && ResourceHelper.ServerActive)
                        {
                            if (mi.DownloadStatus == 0)
                            {
                                DownloadImageAsync(index, mi);
                            }
                        }
                        return false;
                    }
                    
                    _fStream.Seek(dataPosition, SeekOrigin.Begin);
                    mi.CreateTexture(_reader);
                }
                catch (EndOfStreamException)
                {
                    // 文件不完整，无法读取图片数据
                    if (Settings.MicroClientEnabled && ResourceHelper.ServerActive)
                    {
                        if (mi.DownloadStatus == 0)
                        {
                            DownloadImageAsync(index, mi);
                        }
                    }
                    return false;
                }
                catch (IOException)
                {
                    // 文件I/O错误
                    return false;
                }
                catch (Exception)
                {
                    // 其他异常，返回false而不是崩溃
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 异步下载图片数据
        /// </summary>
        /// <param name="index">图片索引</param>
        /// <param name="mi">图片对象</param>
        private async void DownloadImageAsync(int index, MImage mi)
        {
            // 检查重试时间
            if (DateTime.Now < mi.NextRetryTime)
                return;
            
            mi.DownloadStatus = 1; // 标记为下载中
            
            try
            {
                // position 是索引位置（图片头开始位置），用于写入本地文件
                int position = _indexList[index];
                // length 是压缩数据长度（不含17字节头）
                int length = mi.Length;
                
                // GetImageAsync 返回包含头信息和压缩数据的结果
                ImageDownloadResult downloadResult = await ResourceHelper.GetImageAsync(_fileName, index, position, length);
                
                if (downloadResult != null && downloadResult.CompressedData != null && downloadResult.CompressedData.Length > 0)
                {
                    // 更新 MImage 的头信息（包括偏移量）
                    mi.Width = downloadResult.Width;
                    mi.Height = downloadResult.Height;
                    mi.X = downloadResult.X;
                    mi.Y = downloadResult.Y;
                    mi.ShadowX = downloadResult.ShadowX;
                    mi.ShadowY = downloadResult.ShadowY;
                    mi.Shadow = downloadResult.Shadow;
                    mi.Length = downloadResult.Length;
                    
                    // 下载成功，创建纹理
                    mi.CreateTextureFromData(downloadResult.CompressedData);
                    mi.DownloadStatus = 2; // 标记为完成
                }
                else
                {
                    // 下载失败，1秒后重试
                    mi.DownloadStatus = 0;
                    mi.NextRetryTime = DateTime.Now.AddSeconds(1);
                }
            }
            catch
            {
                // 异常，1秒后重试
                mi.DownloadStatus = 0;
                mi.NextRetryTime = DateTime.Now.AddSeconds(1);
            }
        }

        /// <summary>
        /// 下载完整图片数据（当本地文件没有图片头时使用）
        /// 下载完成后会创建 MImage 对象并创建纹理
        /// </summary>
        /// <param name="index">图片索引</param>
        /// <param name="position">图片在文件中的位置</param>
        private async void DownloadFullImageAsync(int index, int position)
        {
            try
            {
                // 使用 GetImageAsync 下载完整图片数据
                // 传入 0 作为 compressedLength，因为我们不知道实际长度
                // GetImageAsync 会返回包含头信息和压缩数据的结果，同时将完整数据写入待写入队列
                ImageDownloadResult downloadResult = await ResourceHelper.GetImageAsync(_fileName, index, position, 0);
                
                if (downloadResult != null && downloadResult.CompressedData != null && downloadResult.CompressedData.Length > 0)
                {
                    // 创建 MImage 对象并设置头信息
                    if (_images != null && index >= 0 && index < _images.Length)
                    {
                        MImage mi = new MImage(downloadResult);
                        _images[index] = mi;
                        
                        // 创建纹理
                        mi.CreateTextureFromData(downloadResult.CompressedData);
                        mi.DownloadStatus = 2; // 标记为完成
                    }
                }
                else
                {
                    // 下载失败，重置状态以便重试
                    if (_images != null && index >= 0 && index < _images.Length && _images[index] != null)
                    {
                        _images[index].DownloadStatus = 0;
                        _images[index].NextRetryTime = DateTime.Now.AddSeconds(1);
                    }
                }
                // 下载完成后，ProcessPendingWrites 会将数据写入文件
            }
            catch
            {
                // 异常，重置状态以便重试
                if (_images != null && index >= 0 && index < _images.Length && _images[index] != null)
                {
                    _images[index].DownloadStatus = 0;
                    _images[index].NextRetryTime = DateTime.Now.AddSeconds(1);
                }
            }
        }

        /// <summary>
        /// 检查图片数据区域是否为空或无效（需要从服务器下载）
        /// </summary>
        /// <param name="index">图片索引</param>
        /// <returns>如果数据为空或无效则返回true</returns>
        private bool IsImageDataEmpty(int index)
        {
            if (_fStream == null || _images == null || index < 0 || index >= _images.Length)
                return false;
            
            MImage mi = _images[index];
            if (mi == null || mi.Length <= 0)
                return false;
            
            try
            {
                // 定位到图片数据位置（跳过17字节头部）
                int dataPosition = _indexList[index] + 17;
                
                // 检查数据位置是否超出文件范围
                if (dataPosition + mi.Length > _fStream.Length)
                    return true;
                
                _fStream.Seek(dataPosition, SeekOrigin.Begin);
                
                // 读取部分数据检查是否为空（最多检查前256字节）
                int checkLength = Math.Min(mi.Length, 256);
                byte[] buffer = new byte[checkLength];
                int bytesRead = _fStream.Read(buffer, 0, checkLength);
                
                if (bytesRead == 0)
                    return true;
                
                // 检查是否全为零
                for (int i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] != 0)
                        return false;
                }
                
                return true;
            }
            catch
            {
                return true; // 出错时假设需要下载
            }
        }

        public Point GetOffSet(int index)
        {
            if (!_initialized) Initialize();

            if (_images == null || index < 0 || index >= _images.Length)
                return Point.Empty;
            
            // 流式加载：如果文件流不存在，返回空
            if (_fStream == null || _indexList == null)
                return Point.Empty;

            if (_images[index] == null)
            {
                try
                {
                    int imagePosition = _indexList[index];
                    if (imagePosition <= 0 || imagePosition + 17 > _fStream.Length)
                    {
                        // 流式加载：触发下载
                        if (Settings.MicroClientEnabled && ResourceHelper.ServerActive)
                        {
                            // 创建一个临时的空 MImage 对象来跟踪下载状态
                            _images[index] = new MImage();
                            _images[index].DownloadStatus = 1;
                            DownloadFullImageAsync(index, imagePosition);
                        }
                        return Point.Empty;
                    }
                    
                    _fStream.Seek(imagePosition, SeekOrigin.Begin);
                    _images[index] = new MImage(_reader);
                }
                catch
                {
                    // 文件读取失败，返回空
                    return Point.Empty;
                }
            }
            
            if (_images[index] == null)
                return Point.Empty;

            // 流式加载：如果图片头数据为空（Width/Height 为 0），返回空并触发下载
            MImage mi = _images[index];
            if ((mi.Width == 0 || mi.Height == 0) && Settings.MicroClientEnabled && ResourceHelper.ServerActive)
            {
                if (mi.DownloadStatus == 0 && DateTime.Now >= mi.NextRetryTime)
                {
                    int imagePosition = _indexList[index];
                    mi.DownloadStatus = 1;
                    DownloadFullImageAsync(index, imagePosition);
                }
                return Point.Empty;
            }

            return new Point(_images[index].X, _images[index].Y);
        }
        public Size GetSize(int index)
        {
            if (!_initialized) Initialize();
            if (_images == null || index < 0 || index >= _images.Length)
                return Size.Empty;
            
            // 流式加载：如果文件流不存在，返回空
            if (_fStream == null || _indexList == null)
                return Size.Empty;

            if (_images[index] == null)
            {
                try
                {
                    int imagePosition = _indexList[index];
                    if (imagePosition <= 0 || imagePosition + 17 > _fStream.Length)
                    {
                        // 流式加载：触发下载
                        if (Settings.MicroClientEnabled && ResourceHelper.ServerActive)
                        {
                            _images[index] = new MImage();
                            _images[index].DownloadStatus = 1;
                            DownloadFullImageAsync(index, imagePosition);
                        }
                        return Size.Empty;
                    }
                    
                    _fStream.Seek(imagePosition, SeekOrigin.Begin);
                    _images[index] = new MImage(_reader);
                }
                catch
                {
                    // 文件读取失败，返回空
                    return Size.Empty;
                }
            }
            
            if (_images[index] == null)
                return Size.Empty;

            // 流式加载：如果图片头数据为空（Width/Height 为 0），返回空并触发下载
            MImage mi = _images[index];
            if ((mi.Width == 0 || mi.Height == 0) && Settings.MicroClientEnabled && ResourceHelper.ServerActive)
            {
                if (mi.DownloadStatus == 0 && DateTime.Now >= mi.NextRetryTime)
                {
                    int imagePosition = _indexList[index];
                    mi.DownloadStatus = 1;
                    DownloadFullImageAsync(index, imagePosition);
                }
                return Size.Empty;
            }

            return new Size(_images[index].Width, _images[index].Height);
        }
        public Size GetTrueSize(int index)
        {
            if (!_initialized)
                Initialize();

            if (_images == null || index < 0 || index >= _images.Length)
                return Size.Empty;
            
            // 流式加载：如果文件流不存在，返回空
            if (_fStream == null || _indexList == null)
                return Size.Empty;

            if (_images[index] == null)
            {
                try
                {
                    int imagePosition = _indexList[index];
                    if (imagePosition <= 0 || imagePosition + 17 > _fStream.Length)
                    {
                        // 流式加载：触发下载
                        if (Settings.MicroClientEnabled && ResourceHelper.ServerActive)
                        {
                            _images[index] = new MImage();
                            _images[index].DownloadStatus = 1;
                            DownloadFullImageAsync(index, imagePosition);
                        }
                        return Size.Empty;
                    }
                    
                    _fStream.Position = imagePosition;
                    _images[index] = new MImage(_reader);
                }
                catch
                {
                    // 文件读取失败，返回空
                    return Size.Empty;
                }
            }
            MImage mi = _images[index];
            if (mi == null)
                return Size.Empty;
            
            // 流式加载：如果图片头数据为空（Width/Height 为 0），返回空并触发下载
            if ((mi.Width == 0 || mi.Height == 0) && Settings.MicroClientEnabled && ResourceHelper.ServerActive)
            {
                if (mi.DownloadStatus == 0 && DateTime.Now >= mi.NextRetryTime)
                {
                    int imagePosition = _indexList[index];
                    mi.DownloadStatus = 1;
                    DownloadFullImageAsync(index, imagePosition);
                }
                return Size.Empty;
            }
                
            if (mi.TrueSize.IsEmpty)
            {
                if (!mi.TextureValid)
                {
                    if ((mi.Width == 0) || (mi.Height == 0))
                        return Size.Empty;

                    try
                    {
                        int dataPosition = _indexList[index] + 17;
                        if (dataPosition + mi.Length > _fStream.Length)
                        {
                            // 流式加载：数据不完整，触发下载
                            if (Settings.MicroClientEnabled && ResourceHelper.ServerActive)
                            {
                                if (mi.DownloadStatus == 0 && DateTime.Now >= mi.NextRetryTime)
                                {
                                    mi.DownloadStatus = 1;
                                    DownloadImageAsync(index, mi);
                                }
                            }
                            return new Size(mi.Width, mi.Height); // 返回头信息中的大小
                        }
                        
                        _fStream.Seek(dataPosition, SeekOrigin.Begin);
                        mi.CreateTexture(_reader);
                    }
                    catch
                    {
                        // 文件读取失败，返回头信息中的大小
                        return new Size(mi.Width, mi.Height);
                    }
                }
                return mi.GetTrueSize();
            }
            return mi.TrueSize;
        }

        public void Draw(int index, int x, int y)
        {
            if (x >= Settings.ScreenWidth || y >= Settings.ScreenHeight)
                return;

            if (!CheckImage(index))
                return;

            MImage mi = _images[index];

            if (x + mi.Width < 0 || y + mi.Height < 0)
                return;


            DXManager.Draw(mi.Image, new Rectangle(0, 0, mi.Width, mi.Height), new Vector3((float)x, (float)y, 0.0F), Color.White);

            mi.CleanTime = CMain.Time + Settings.CleanDelay;
        }
        public void Draw(int index, Point point, Color colour, bool offSet = false)
        {
            if (!CheckImage(index))
                return;

            MImage mi = _images[index];

            if (offSet) point.Offset(mi.X, mi.Y);

            if (point.X >= Settings.ScreenWidth || point.Y >= Settings.ScreenHeight || point.X + mi.Width < 0 || point.Y + mi.Height < 0)
                return;

            DXManager.Draw(mi.Image, new Rectangle(0, 0, mi.Width, mi.Height), new Vector3((float)point.X, (float)point.Y, 0.0F), colour);

            mi.CleanTime = CMain.Time + Settings.CleanDelay;
        }

        public void Draw(int index, Point point, Color colour, bool offSet, float opacity)
        {
            if (!CheckImage(index))
                return;

            MImage mi = _images[index];

            if (offSet) point.Offset(mi.X, mi.Y);

            if (point.X >= Settings.ScreenWidth || point.Y >= Settings.ScreenHeight || point.X + mi.Width < 0 || point.Y + mi.Height < 0)
                return;

            DXManager.DrawOpaque(mi.Image, new Rectangle(0, 0, mi.Width, mi.Height), new Vector3((float)point.X, (float)point.Y, 0.0F), colour, opacity); 

            mi.CleanTime = CMain.Time + Settings.CleanDelay;
        }

        public void DrawBlend(int index, Point point, Color colour, bool offSet = false, float rate = 1)
        {
            if (!CheckImage(index))
                return;

            MImage mi = _images[index];

            if (offSet) point.Offset(mi.X, mi.Y);

            if (point.X >= Settings.ScreenWidth || point.Y >= Settings.ScreenHeight || point.X + mi.Width < 0 || point.Y + mi.Height < 0)
                return;

            bool oldBlend = DXManager.Blending;
            DXManager.SetBlend(true, rate);

            DXManager.Draw(mi.Image, new Rectangle(0, 0, mi.Width, mi.Height), new Vector3((float)point.X, (float)point.Y, 0.0F), colour);

            DXManager.SetBlend(oldBlend);
            mi.CleanTime = CMain.Time + Settings.CleanDelay;
        }
        public void Draw(int index, Rectangle section, Point point, Color colour, bool offSet)
        {
            if (!CheckImage(index))
                return;

            MImage mi = _images[index];

            if (offSet) point.Offset(mi.X, mi.Y);


            if (point.X >= Settings.ScreenWidth || point.Y >= Settings.ScreenHeight || point.X + mi.Width < 0 || point.Y + mi.Height < 0)
                return;

            if (section.Right > mi.Width)
                section.Width -= section.Right - mi.Width;

            if (section.Bottom > mi.Height)
                section.Height -= section.Bottom - mi.Height;

            DXManager.Draw(mi.Image, section, new Vector3((float)point.X, (float)point.Y, 0.0F), colour);

            mi.CleanTime = CMain.Time + Settings.CleanDelay;
        }
        public void Draw(int index, Rectangle section, Point point, Color colour, float opacity)
        {
            if (!CheckImage(index))
                return;

            MImage mi = _images[index];


            if (point.X >= Settings.ScreenWidth || point.Y >= Settings.ScreenHeight || point.X + mi.Width < 0 || point.Y + mi.Height < 0)
                return;

            if (section.Right > mi.Width)
                section.Width -= section.Right - mi.Width;

            if (section.Bottom > mi.Height)
                section.Height -= section.Bottom - mi.Height;

            DXManager.DrawOpaque(mi.Image, section, new Vector3((float)point.X, (float)point.Y, 0.0F), colour, opacity); 

            mi.CleanTime = CMain.Time + Settings.CleanDelay;
        }
        public void Draw(int index, Point point, Size size, Color colour)
        {
            if (!CheckImage(index))
                return;

            MImage mi = _images[index];

            if (point.X >= Settings.ScreenWidth || point.Y >= Settings.ScreenHeight || point.X + size.Width < 0 || point.Y + size.Height < 0)
                return;

            float scaleX = (float)size.Width / mi.Width;
            float scaleY = (float)size.Height / mi.Height;

            Matrix matrix = Matrix.Scaling(scaleX, scaleY, 0);
            DXManager.Sprite.Transform = matrix;
            DXManager.Draw(mi.Image, new Rectangle(0, 0, mi.Width, mi.Height), new Vector3((float)point.X / scaleX, (float)point.Y / scaleY, 0.0F), Color.White); 

            DXManager.Sprite.Transform = Matrix.Identity;

            mi.CleanTime = CMain.Time + Settings.CleanDelay;
        }

        public void DrawTinted(int index, Point point, Color colour, Color Tint, bool offSet = false)
        {
            if (!CheckImage(index))
                return;

            MImage mi = _images[index];

            if (offSet) point.Offset(mi.X, mi.Y);

            if (point.X >= Settings.ScreenWidth || point.Y >= Settings.ScreenHeight || point.X + mi.Width < 0 || point.Y + mi.Height < 0)
                return;

            DXManager.Draw(mi.Image, new Rectangle(0, 0, mi.Width, mi.Height), new Vector3((float)point.X, (float)point.Y, 0.0F), colour);

            if (mi.HasMask)
            {
                DXManager.Draw(mi.MaskImage, new Rectangle(0, 0, mi.Width, mi.Height), new Vector3((float)point.X, (float)point.Y, 0.0F), Tint);
            }

            mi.CleanTime = CMain.Time + Settings.CleanDelay;
        }

        public void DrawUp(int index, int x, int y)
        {
            if (x >= Settings.ScreenWidth)
                return;

            if (!CheckImage(index))
                return;

            MImage mi = _images[index];
            y -= mi.Height;
            if (y >= Settings.ScreenHeight)
                return;
            if (x + mi.Width < 0 || y + mi.Height < 0)
                return;

            DXManager.Draw(mi.Image, new Rectangle(0, 0, mi.Width, mi.Height), new Vector3(x, y, 0.0F), Color.White);

            mi.CleanTime = CMain.Time + Settings.CleanDelay;
        }
        public void DrawUpBlend(int index, Point point)
        {
            if (!CheckImage(index))
                return;

            MImage mi = _images[index];

            point.Y -= mi.Height;


            if (point.X >= Settings.ScreenWidth || point.Y >= Settings.ScreenHeight || point.X + mi.Width < 0 || point.Y + mi.Height < 0)
                return;

            bool oldBlend = DXManager.Blending;
            DXManager.SetBlend(true, 1);

            DXManager.Draw(mi.Image, new Rectangle(0, 0, mi.Width, mi.Height), new Vector3((float)point.X, (float)point.Y, 0.0F), Color.White);

            DXManager.SetBlend(oldBlend);
            mi.CleanTime = CMain.Time + Settings.CleanDelay;
        }

        public bool VisiblePixel(int index, Point point, bool accuate)
        {
            if (!CheckImage(index))
                return false;

            if (accuate)
                return _images[index].VisiblePixel(point);

            int accuracy = 2;

            for (int x = -accuracy; x <= accuracy; x++)
                for (int y = -accuracy; y <= accuracy; y++)
                    if (_images[index].VisiblePixel(new Point(point.X + x, point.Y + y)))
                        return true;

            return false;
        }
    }

    public sealed class MImage
    {
        public short Width, Height, X, Y, ShadowX, ShadowY;
        public byte Shadow;
        public int Length;

        public bool TextureValid;
        public Texture Image;
        //layer 2:
        public short MaskWidth, MaskHeight, MaskX, MaskY;
        public int MaskLength;

        public Texture MaskImage;
        public Boolean HasMask;

        public long CleanTime;
        public Size TrueSize;

        public unsafe byte* Data;

        // 流式加载状态字段
        /// <summary>
        /// 下载状态: 0=未下载, 1=下载中, 2=完成
        /// </summary>
        private byte _downloadStatus;
        
        /// <summary>
        /// 下次重试时间
        /// </summary>
        private DateTime _nextRetryTime;
        
        /// <summary>
        /// 占位纹理（静态缓存）
        /// </summary>
        private static Texture _placeholderTexture;

        /// <summary>
        /// 获取或设置下载状态
        /// </summary>
        public byte DownloadStatus
        {
            get => _downloadStatus;
            set => _downloadStatus = value;
        }

        /// <summary>
        /// 获取或设置下次重试时间
        /// </summary>
        public DateTime NextRetryTime
        {
            get => _nextRetryTime;
            set => _nextRetryTime = value;
        }

        /// <summary>
        /// 无参构造函数，用于创建空的 MImage 对象（流式加载时使用）
        /// </summary>
        public MImage()
        {
            Width = 0;
            Height = 0;
            X = 0;
            Y = 0;
            ShadowX = 0;
            ShadowY = 0;
            Shadow = 0;
            Length = 0;
            HasMask = false;
        }

        public MImage(BinaryReader reader)
        {
            //read layer 1
            Width = reader.ReadInt16();
            Height = reader.ReadInt16();
            X = reader.ReadInt16();
            Y = reader.ReadInt16();
            ShadowX = reader.ReadInt16();
            ShadowY = reader.ReadInt16();
            Shadow = reader.ReadByte();
            Length = reader.ReadInt32();

            //check if there's a second layer and read it
            HasMask = ((Shadow >> 7) == 1) ? true : false;
            if (HasMask)
            {
                reader.ReadBytes(Length);
                MaskWidth = reader.ReadInt16();
                MaskHeight = reader.ReadInt16();
                MaskX = reader.ReadInt16();
                MaskY = reader.ReadInt16();
                MaskLength = reader.ReadInt32();
            }
        }

        /// <summary>
        /// 从下载结果创建 MImage 对象
        /// </summary>
        /// <param name="downloadResult">下载结果</param>
        public MImage(ImageDownloadResult downloadResult)
        {
            Width = downloadResult.Width;
            Height = downloadResult.Height;
            X = downloadResult.X;
            Y = downloadResult.Y;
            ShadowX = downloadResult.ShadowX;
            ShadowY = downloadResult.ShadowY;
            Shadow = downloadResult.Shadow;
            Length = downloadResult.Length;
            
            // 检查是否有 Mask 层（高位为1）
            HasMask = ((Shadow >> 7) == 1);
            // 注意：流式加载暂不支持 Mask 层的完整处理
        }

        /// <summary>
        /// 获取占位纹理（1x1透明纹理）
        /// 缓存避免重复创建
        /// </summary>
        /// <returns>占位纹理</returns>
        public static Texture GetPlaceholderTexture()
        {
            if (_placeholderTexture == null || _placeholderTexture.Disposed)
            {
                _placeholderTexture = new Texture(DXManager.Device, 1, 1, 1, Usage.None, Format.A8R8G8B8, Pool.Managed);
                DataRectangle rect = _placeholderTexture.LockRectangle(0, LockFlags.Discard);
                
                // 填充透明像素 (ARGB = 0x00000000)
                unsafe
                {
                    byte* data = (byte*)rect.Data.DataPointer;
                    data[0] = 0; // B
                    data[1] = 0; // G
                    data[2] = 0; // R
                    data[3] = 0; // A (透明)
                }
                
                rect.Data.Dispose();
                _placeholderTexture.UnlockRectangle(0);
            }
            return _placeholderTexture;
        }

        public unsafe void CreateTexture(BinaryReader reader)
        {
            int w = Width;// + (4 - Width % 4) % 4;
            int h = Height;// + (4 - Height % 4) % 4;

            Image = new Texture(DXManager.Device, w, h, 1, Usage.None, Format.A8R8G8B8, Pool.Managed);
            DataRectangle stream = Image.LockRectangle(0, LockFlags.Discard);
            Data = (byte*)stream.Data.DataPointer;

            DecompressImage(reader.ReadBytes(Length), stream.Data);

            stream.Data.Dispose();
            Image.UnlockRectangle(0);

            if (HasMask)
            {
                reader.ReadBytes(12);
                w = Width;// + (4 - Width % 4) % 4;
                h = Height;// + (4 - Height % 4) % 4;

                MaskImage = new Texture(DXManager.Device, w, h, 1, Usage.None, Format.A8R8G8B8, Pool.Managed);
                stream = MaskImage.LockRectangle(0, LockFlags.Discard);

                DecompressImage(reader.ReadBytes(Length), stream.Data);

                stream.Data.Dispose();
                MaskImage.UnlockRectangle(0);
            }

            DXManager.TextureList.Add(this);
            TextureValid = true;

            CleanTime = CMain.Time + Settings.CleanDelay;
        }

        /// <summary>
        /// 从下载的字节数组创建纹理
        /// </summary>
        /// <param name="imageData">图片压缩数据</param>
        public unsafe void CreateTextureFromData(byte[] imageData)
        {
            if (imageData == null || imageData.Length == 0)
                return;

            int w = Width;
            int h = Height;

            Image = new Texture(DXManager.Device, w, h, 1, Usage.None, Format.A8R8G8B8, Pool.Managed);
            DataRectangle stream = Image.LockRectangle(0, LockFlags.Discard);
            Data = (byte*)stream.Data.DataPointer;

            // 解压图片数据到纹理
            using (MemoryStream ms = new MemoryStream(imageData))
            {
                DecompressImage(imageData, stream.Data);
            }

            stream.Data.Dispose();
            Image.UnlockRectangle(0);

            // 注意：流式加载暂不支持Mask层，因为需要额外的数据
            // 如果需要支持Mask，需要修改服务端API返回完整的图片数据

            DXManager.TextureList.Add(this);
            TextureValid = true;

            CleanTime = CMain.Time + Settings.CleanDelay;
        }

        public unsafe void DisposeTexture()
        {
            DXManager.TextureList.Remove(this);

            if (Image != null && !Image.Disposed)
            {
                Image.Dispose();
            }

            if (MaskImage != null && !MaskImage.Disposed)
            {
                MaskImage.Dispose();
            }

            TextureValid = false;
            Image = null;
            MaskImage = null;
            Data = null;
        }

        public unsafe bool VisiblePixel(Point p)
        {
            if (p.X < 0 || p.Y < 0 || p.X >= Width || p.Y >= Height)
                return false;

            int w = Width;

            bool result = false;
            if (Data != null)
            {
                int x = p.X;
                int y = p.Y;
                
                int index = (y * (w << 2)) + (x << 2) + 3;
                
                byte col = Data[index];

                if (col == 0) return false;
                else return true;
            }
            return result;
        }

        public Size GetTrueSize()
        {
            if (TrueSize != Size.Empty) return TrueSize;

            int l = 0, t = 0, r = Width, b = Height;

            bool visible = false;
            for (int x = 0; x < r; x++)
            {
                for (int y = 0; y < b; y++)
                {
                    if (!VisiblePixel(new Point(x, y))) continue;

                    visible = true;
                    break;
                }

                if (!visible) continue;

                l = x;
                break;
            }

            visible = false;
            for (int y = 0; y < b; y++)
            {
                for (int x = l; x < r; x++)
                {
                    if (!VisiblePixel(new Point(x, y))) continue;

                    visible = true;
                    break;

                }
                if (!visible) continue;

                t = y;
                break;
            }

            visible = false;
            for (int x = r - 1; x >= l; x--)
            {
                for (int y = 0; y < b; y++)
                {
                    if (!VisiblePixel(new Point(x, y))) continue;

                    visible = true;
                    break;
                }

                if (!visible) continue;

                r = x + 1;
                break;
            }

            visible = false;
            for (int y = b - 1; y >= t; y--)
            {
                for (int x = l; x < r; x++)
                {
                    if (!VisiblePixel(new Point(x, y))) continue;

                    visible = true;
                    break;

                }
                if (!visible) continue;

                b = y + 1;
                break;
            }

            TrueSize = Rectangle.FromLTRB(l, t, r, b).Size;

            return TrueSize;
        }

        private static byte[] DecompressImage(byte[] image)
        {
            using (GZipStream stream = new GZipStream(new MemoryStream(image), CompressionMode.Decompress))
            {
                const int size = 4096;
                byte[] buffer = new byte[size];
                using (MemoryStream memory = new MemoryStream())
                {
                    int count = 0;
                    do
                    {
                        count = stream.Read(buffer, 0, size);
                        if (count > 0)
                        {
                            memory.Write(buffer, 0, count);
                        }
                    }
                    while (count > 0);
                    return memory.ToArray();
                }
            }
        }

        private static void DecompressImage(byte[] data, Stream destination)
        {
            using (var stream = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            {
                stream.CopyTo(destination);
            }
        }
    }
}
