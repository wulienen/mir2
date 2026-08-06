using Client.MirControls;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirSounds;
using SlimDX;
using SlimDX.Direct3D9;
using Font = System.Drawing.Font;

namespace Client.MirScenes.Dialogs
{
    public sealed class AssistDialog : MirImageControl
    {
        private const string BackgroundFileName = "AssistDialogBackground-450x200.png";
        private static readonly Size DialogSize = new Size(450, 200);

        private const int BasicPage = 0;
        private const int ClassPage = 1;
        private const int CombatPage = 2;
        private const int ProtectionPage = 3;
        private const int ItemPage = 4;
        private const int FilterColumnCount = 2;
        private const int FilterVisibleRows = 5;
        private const int FilterVisibleCount = FilterColumnCount * FilterVisibleRows;

        private readonly List<MirControl>[] _pageControls =
        {
            new List<MirControl>(),
            new List<MirControl>(),
            new List<MirControl>(),
            new List<MirControl>(),
            new List<MirControl>()
        };

        private readonly List<ToggleBinding> _toggleBindings = new List<ToggleBinding>();
        private readonly MirButton[] _tabs = new MirButton[5];
        private readonly MirCheckBox[] _itemFilterChecks = new MirCheckBox[FilterVisibleCount];
        private readonly string[] _itemFilterNames = new string[FilterVisibleCount];
        private MirDropDownBox _huntModeDropDown;
        private MirDropDownBox _combatSpellDropDown;
        private readonly List<Spell> _combatSpellOptions = new List<Spell>();
        private MirButton _filterScrollUpButton, _filterScrollDownButton;
        private MirControl _filterScrollTrack, _filterScrollThumb;
        private MirLabel _filterPageLabel;
        private int _currentPage;
        private int _filterScrollRow;
        private Texture _backgroundTexture;
        private bool _backgroundLoadAttempted;
        private Rectangle _backgroundSource;

        private sealed class ToggleBinding
        {
            public MirCheckBox Control;
            public Func<bool> Read;
        }

        public AssistDialog()
        {
            Index = -1;
            Library = null;
            AutoSize = false;
            Size = DialogSize;
            DrawImage = false;
            DrawControlTexture = true;
            BackColour = Color.FromArgb(255, 15, 12, 10);
            Movable = true;
            Sort = true;
            Location = Center;
            MouseWheel += ItemFilterMouseWheel;

            CreateTab(BasicPage, 8, ClientTextKeys.AssistBasicTab);
            CreateTab(ClassPage, 86, ClientTextKeys.AssistClassTab);
            CreateTab(CombatPage, 164, ClientTextKeys.AssistCombatTab);
            CreateTab(ProtectionPage, 242, ClientTextKeys.AssistProtectionTab);
            CreateTab(ItemPage, 320, ClientTextKeys.AssistItemTab);

            CreateBasicPage();
            CreateClassPage();
            CreateCombatPage();
            CreateProtectionPage();
            CreateItemPage();

            MirButton closeButton = new MirButton
            {
                HoverIndex = 361,
                Index = 360,
                Location = new Point(428, 1),
                Library = Libraries.Prguse2,
                Parent = this,
                PressedIndex = 362,
                Sound = SoundList.ButtonA
            };
            closeButton.Click += (o, e) => Hide();

            SwitchPage(BasicPage);
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();

            if (!TryLoadBackground())
                return;

            DXManager.DrawOpaque(_backgroundTexture, _backgroundSource,
                new Vector3(DisplayLocation.X, DisplayLocation.Y, 0F), Color.White, Opacity);
        }

        private bool TryLoadBackground()
        {
            if (_backgroundTexture != null && !_backgroundTexture.Disposed)
                return true;

            if (_backgroundLoadAttempted)
                return false;

            _backgroundLoadAttempted = true;
            string path = Path.Combine(Settings.DataPath, BackgroundFileName);
            if (!File.Exists(path))
                return false;

            try
            {
                _backgroundTexture = Texture.FromFile(DXManager.Device, path, 0, 0, 1,
                    Usage.None, Format.A8R8G8B8, Pool.Managed, Filter.None, Filter.None, 0);
                SurfaceDescription description = _backgroundTexture.GetLevelDescription(0);
                _backgroundSource = new Rectangle(0, 0, description.Width, description.Height);
                return true;
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Assist dialog background load failed: {ex}");
                _backgroundTexture?.Dispose();
                _backgroundTexture = null;
                return false;
            }
        }

        private void CreateBasicPage()
        {
            CreateToggle(BasicPage, 26, 70, ClientTextKeys.AssistFreeShift,
                () => Settings.AssistFreeShift, value => Settings.AssistFreeShift = value);
            CreateToggle(BasicPage, 26, 95, ClientTextKeys.AssistShowLevel,
                () => Settings.AssistShowLevel, value => Settings.AssistShowLevel = value);
            CreateToggle(BasicPage, 26, 120, ClientTextKeys.AssistShowTransform,
                () => Settings.AssistShowTransform, value => Settings.AssistShowTransform = value, RefreshPlayerAppearances);
            CreateToggle(BasicPage, 26, 145, ClientTextKeys.AssistShowPing,
                () => Settings.AssistShowPing, value => Settings.AssistShowPing = value);
            CreateToggle(BasicPage, 26, 170, ClientTextKeys.AssistShowHealthValues,
                () => Settings.AssistShowHealthValues, value => Settings.AssistShowHealthValues = value);

            CreateToggle(BasicPage, 160, 70, ClientTextKeys.AssistShowGuildName,
                () => Settings.AssistShowGuildName, value => Settings.AssistShowGuildName = value);
            CreateToggle(BasicPage, 160, 95, ClientTextKeys.AssistShowGroupInfo,
                () => Settings.AssistShowGroupInfo, value => Settings.AssistShowGroupInfo = value);
            CreateToggle(BasicPage, 160, 120, ClientTextKeys.AssistShowDamage,
                () => Settings.DisplayDamage, value => Settings.DisplayDamage = value);
            CreateToggle(BasicPage, 160, 145, ClientTextKeys.AssistShowHealing,
                () => Settings.AssistShowHealing, value => Settings.AssistShowHealing = value);
            CreateToggle(BasicPage, 160, 170, ClientTextKeys.AssistHideDead,
                () => Settings.AssistHideDead, value => Settings.AssistHideDead = value);

            CreateToggle(BasicPage, 300, 70, ClientTextKeys.AssistShowMonsterNames,
                () => Settings.AssistShowMonsterNames, value => Settings.AssistShowMonsterNames = value);
            CreateToggle(BasicPage, 300, 95, ClientTextKeys.AssistHideDropNotifications,
                () => Settings.AssistHideDropNotifications, value => Settings.AssistHideDropNotifications = value);
            CreateToggle(BasicPage, 300, 120, ClientTextKeys.AssistShowNpcNames,
                () => Settings.AssistShowNpcNames, value => Settings.AssistShowNpcNames = value);
        }

        private void CreateClassPage()
        {
            CreateToggle(ClassPage, 26, 70, ClientTextKeys.AssistAutoFlamingSword,
                () => Settings.AssistAutoFlamingSword, value => Settings.AssistAutoFlamingSword = value);
            CreateToggle(ClassPage, 26, 95, ClientTextKeys.AssistAutoTwinDrakeBlade,
                () => Settings.AssistAutoTwinDrakeBlade, value => Settings.AssistAutoTwinDrakeBlade = value);
            CreateToggle(ClassPage, 160, 70, ClientTextKeys.AssistAutoMagicShield,
                () => Settings.AssistAutoMagicShield, value => Settings.AssistAutoMagicShield = value);
            CreateToggle(ClassPage, 300, 70, ClientTextKeys.AssistAutoPoisonAmulet,
                () => Settings.AssistAutoPoisonAmulet, value => Settings.AssistAutoPoisonAmulet = value);
            CreateToggle(ClassPage, 300, 95, ClientTextKeys.AssistAutoElementalBarrier,
                () => Settings.AssistAutoElementalBarrier, value => Settings.AssistAutoElementalBarrier = value);
        }

        private void CreateProtectionPage()
        {
            CreateToggle(ProtectionPage, 26, 70, ClientTextKeys.AssistEnableProtection,
                () => Settings.AssistProtectionEnabled, value => Settings.AssistProtectionEnabled = value);

            CreateProtectionRow(100, ClientTextKeys.AssistHealthBelow,
                () => Settings.AssistHealthPotionPercent,
                value => Settings.AssistHealthPotionPercent = value,
                () => Settings.AssistHealthPotionKeyword,
                value => Settings.AssistHealthPotionKeyword = value);

            CreateProtectionRow(125, ClientTextKeys.AssistManaBelow,
                () => Settings.AssistManaPotionPercent,
                value => Settings.AssistManaPotionPercent = value,
                () => Settings.AssistManaPotionKeyword,
                value => Settings.AssistManaPotionKeyword = value);

            CreateProtectionRow(150, ClientTextKeys.AssistHealthBelow,
                () => Settings.AssistEmergencyPercent,
                value => Settings.AssistEmergencyPercent = value,
                () => Settings.AssistEmergencyKeyword,
                value => Settings.AssistEmergencyKeyword = value);
        }

        private void CreateCombatPage()
        {
            CreateToggle(CombatPage, 26, 70, ClientTextKeys.AssistAutoAttack,
                () => Settings.AssistAutoAttack, value => Settings.AssistAutoAttack = value,
                () => GameScene.Scene?.AssistController?.ClearAutomaticTargets());

            MirLabel searchModeLabel = new MirLabel
            {
                AutoSize = true,
                Parent = this,
                Location = new Point(26, 103),
                Text = Text(ClientTextKeys.AssistSearchMode)
            };
            _huntModeDropDown = new MirDropDownBox
            {
                Parent = this,
                Location = new Point(125, 99),
                Size = new Size(180, 18),
                Enabled = true
            };
            _huntModeDropDown.ValueChanged += (o, e) =>
            {
                int index = _huntModeDropDown._WantedIndex;
                if (index < 0 || index > (int)AssistSearchMode.CurrentMap)
                    return;

                _huntModeDropDown.SelectedIndex = index;
                Settings.AssistHuntMode = (AssistSearchMode)index;
                GameScene.Scene?.AssistController?.ClearAutomaticTargets();
            };
            _pageControls[CombatPage].Add(searchModeLabel);
            _pageControls[CombatPage].Add(_huntModeDropDown);

            MirLabel combatSpellLabel = new MirLabel
            {
                AutoSize = true,
                Parent = this,
                Location = new Point(26, 165),
                Text = Text(ClientTextKeys.AssistCombatSkill)
            };
            _combatSpellDropDown = new MirDropDownBox
            {
                Parent = this,
                Location = new Point(125, 161),
                Size = new Size(180, 18),
                Enabled = true
            };
            _combatSpellDropDown.ValueChanged += (o, e) =>
            {
                int index = _combatSpellDropDown._WantedIndex;
                if (index < 0 || index >= _combatSpellOptions.Count || GameScene.User == null)
                    return;

                _combatSpellDropDown.SelectedIndex = index;
                Settings.SetAssistCombatSpell(GameScene.User.Class, _combatSpellOptions[index]);
                GameScene.Scene?.AssistController?.NotifyManualInput();
            };
            _pageControls[CombatPage].Add(combatSpellLabel);
            _pageControls[CombatPage].Add(_combatSpellDropDown);
        }

        private void CreateItemPage()
        {
            CreateToggle(ItemPage, 26, 50, ClientTextKeys.AssistAutoPickup,
                () => Settings.AssistAutoPickup, value => Settings.AssistAutoPickup = value,
                () => GameScene.Scene.AssistController.ClearAutomaticTargets());

            MirLabel exclusionHint = new MirLabel
            {
                Parent = this,
                Location = new Point(145, 50),
                Size = new Size(250, 18),
                DrawFormat = TextFormatFlags.VerticalCenter,
                Font = new Font(Settings.FontFamily, 8F),
                ForeColour = Color.FromArgb(255, 220, 180, 95),
                Text = Text(ClientTextKeys.AssistExcludedItemsHint)
            };
            _pageControls[ItemPage].Add(exclusionHint);

            for (int i = 0; i < FilterVisibleCount; i++)
            {
                int column = i % FilterColumnCount;
                int row = i / FilterColumnCount;
                MirCheckBox filter = new MirCheckBox
                {
                    Index = 2086,
                    UnTickedIndex = 2086,
                    TickedIndex = 2087,
                    Parent = this,
                    Location = new Point(column == 0 ? 32 : 225, 72 + row * 20),
                    Library = Libraries.Prguse,
                    Visible = false
                };
                int filterIndex = i;
                filter.Click += (o, e) => FilterClick(filterIndex);
                filter.MouseWheel += ItemFilterMouseWheel;
                _itemFilterChecks[i] = filter;
                _pageControls[ItemPage].Add(filter);
            }

            _filterPageLabel = new MirLabel
            {
                Parent = this,
                Location = new Point(125, 172),
                Size = new Size(200, 17),
                DrawFormat = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter,
                Font = new Font(Settings.FontFamily, 8F)
            };
            _pageControls[ItemPage].Add(_filterPageLabel);

            _filterScrollUpButton = CreateFilterScrollButton(new Point(406, 68), "▲", -1);
            _filterScrollDownButton = CreateFilterScrollButton(new Point(406, 154), "▼", 1);

            _filterScrollTrack = new MirControl
            {
                BackColour = Color.FromArgb(190, 20, 17, 14),
                Border = true,
                BorderColour = Color.FromArgb(170, 135, 96, 55),
                DrawControlTexture = true,
                Location = new Point(412, 88),
                Parent = this,
                Size = new Size(8, 64),
                Visible = false,
                NotControl = true
            };
            _pageControls[ItemPage].Add(_filterScrollTrack);

            _filterScrollThumb = new MirControl
            {
                BackColour = Color.FromArgb(255, 180, 125, 45),
                Border = true,
                BorderColour = Color.FromArgb(255, 230, 190, 95),
                DrawControlTexture = true,
                Location = new Point(413, 89),
                Parent = this,
                Size = new Size(6, 18),
                Visible = false,
                NotControl = true
            };
            _pageControls[ItemPage].Add(_filterScrollThumb);
        }

        private MirButton CreateFilterScrollButton(Point location, string text, int rowDelta)
        {
            MirButton button = new MirButton
            {
                AutoSize = false,
                BackColour = Color.FromArgb(220, 28, 25, 22),
                Border = true,
                BorderColour = Color.FromArgb(170, 135, 96, 55),
                CenterText = true,
                DrawControlTexture = true,
                DrawImage = false,
                FontColour = Color.Gainsboro,
                Location = location,
                Parent = this,
                Size = new Size(20, 18),
                Sound = SoundList.ButtonA,
                Text = text
            };
            button.Click += (o, e) => ScrollItemFilters(rowDelta);
            button.Hint = rowDelta < 0 ? Text(ClientTextKeys.AssistFilterPrevious) : Text(ClientTextKeys.AssistFilterNext);
            _pageControls[ItemPage].Add(button);
            return button;
        }

        private void CreateTab(int page, int x, ClientTextKeys textKey)
        {
            MirButton tab = new MirButton
            {
                Parent = this,
                Location = new Point(x, 12),
                Size = new Size(74, 24),
                AutoSize = false,
                DrawImage = false,
                DrawControlTexture = true,
                Border = true,
                BorderColour = Color.FromArgb(170, 135, 96, 55),
                BackColour = Color.FromArgb(220, 28, 25, 22),
                FontColour = Color.Gainsboro,
                CenterText = true,
                Text = Text(textKey)
            };
            tab.Click += (o, e) => SwitchPage(page);
            _tabs[page] = tab;
        }

        private MirCheckBox CreateToggle(int page, int x, int y, ClientTextKeys textKey,
            Func<bool> read, Action<bool> write, Action changed = null)
        {
            MirCheckBox checkBox = new MirCheckBox
            {
                Index = 2086,
                UnTickedIndex = 2086,
                TickedIndex = 2087,
                Parent = this,
                Location = new Point(x, y),
                Library = Libraries.Prguse,
                Checked = read()
            };
            checkBox.LabelText = Text(textKey);
            checkBox.Click += (o, e) =>
            {
                write(checkBox.Checked);
                changed?.Invoke();
            };

            _pageControls[page].Add(checkBox);
            _toggleBindings.Add(new ToggleBinding { Control = checkBox, Read = read });
            return checkBox;
        }

        private void FilterClick(int index)
        {
            string name = _itemFilterNames[index];
            if (string.IsNullOrEmpty(name))
                return;

            GameScene.Scene.AssistController.SetItemExcluded(name, _itemFilterChecks[index].Checked);
            UpdateItemFilters();
        }

        public void RefreshItemFilters()
        {
            UpdateItemFilters();
        }

        private void ScrollItemFilters(int rowDelta)
        {
            if (_currentPage != ItemPage || rowDelta == 0)
                return;

            _filterScrollRow += rowDelta;
            UpdateItemFilters();
        }

        private void ItemFilterMouseWheel(object sender, MouseEventArgs e)
        {
            if (_currentPage != ItemPage || e.Delta == 0)
                return;

            ScrollItemFilters(e.Delta > 0 ? -1 : 1);
        }

        private void UpdateItemFilters()
        {
            IReadOnlyList<string> items = GameScene.Scene?.AssistController?.GetExcludedItems();
            int itemCount = items?.Count ?? 0;
            int rowCount = (itemCount + FilterColumnCount - 1) / FilterColumnCount;
            int maxScrollRow = Math.Max(0, rowCount - FilterVisibleRows);
            bool showFilterControls = _currentPage == ItemPage;
            _filterScrollRow = Math.Clamp(_filterScrollRow, 0, maxScrollRow);

            for (int i = 0; i < FilterVisibleCount; i++)
            {
                int row = _filterScrollRow + i / FilterColumnCount;
                int column = i % FilterColumnCount;
                int itemIndex = row * FilterColumnCount + column;
                if (items != null && itemIndex < itemCount)
                {
                    _itemFilterNames[i] = items[itemIndex];
                    _itemFilterChecks[i].Location = new Point(column == 0 ? 32 : 225, 72 + (i / FilterColumnCount) * 20);
                    _itemFilterChecks[i].LabelText = items[itemIndex];
                    _itemFilterChecks[i].Checked = true;
                    _itemFilterChecks[i].Visible = showFilterControls;
                }
                else
                {
                    _itemFilterNames[i] = null;
                    _itemFilterChecks[i].LabelText = string.Empty;
                    _itemFilterChecks[i].Checked = false;
                    _itemFilterChecks[i].Visible = false;
                }
            }

            int firstItem = itemCount == 0 ? 0 : _filterScrollRow * FilterColumnCount + 1;
            int lastItem = itemCount == 0 ? 0 : Math.Min(itemCount, (_filterScrollRow + FilterVisibleRows) * FilterColumnCount);
            _filterPageLabel.Text = itemCount == 0 ? "0 / 0" : $"{firstItem}-{lastItem} / {itemCount}";
            _filterScrollUpButton.Enabled = showFilterControls && _filterScrollRow > 0;
            _filterScrollDownButton.Enabled = showFilterControls && _filterScrollRow < maxScrollRow;

            bool scrollable = showFilterControls && maxScrollRow > 0;
            _filterScrollTrack.Visible = scrollable;
            _filterScrollThumb.Visible = scrollable;
            if (scrollable)
            {
                const int trackTop = 89;
                const int trackHeight = 62;
                int thumbHeight = Math.Max(14, trackHeight * FilterVisibleRows / rowCount);
                int thumbTravel = trackHeight - thumbHeight;
                int thumbY = trackTop + (int)Math.Round(thumbTravel * (_filterScrollRow / (double)maxScrollRow));
                _filterScrollThumb.Location = new Point(413, thumbY);
                _filterScrollThumb.Size = new Size(6, thumbHeight);
            }
        }

        private void CreateProtectionRow(int y, ClientTextKeys conditionKey,
            Func<int> readPercent, Action<int> writePercent,
            Func<string> readKeyword, Action<string> writeKeyword)
        {
            MirLabel condition = new MirLabel
            {
                AutoSize = true,
                Parent = this,
                Location = new Point(26, y),
                Text = Text(conditionKey)
            };

            MirTextBox percent = CreateTextBox(new Point(125, y - 1), new Size(35, 18), true);
            percent.Text = readPercent().ToString();
            percent.TextBox.TextChanged += (o, e) =>
            {
                if (int.TryParse(percent.Text, out int value))
                {
                    int clampedValue = Math.Clamp(value, 0, 100);
                    writePercent(clampedValue);
                    if (value != clampedValue)
                        percent.Text = clampedValue.ToString();
                }
            };

            MirLabel percentLabel = new MirLabel
            {
                AutoSize = true,
                Parent = this,
                Location = new Point(164, y),
                Text = "%"
            };

            MirLabel useLabel = new MirLabel
            {
                AutoSize = true,
                Parent = this,
                Location = new Point(184, y),
                Text = Text(ClientTextKeys.AssistUseItem)
            };

            MirTextBox keyword = CreateTextBox(new Point(220, y - 1), new Size(105, 18), false);
            keyword.Text = readKeyword();
            keyword.TextBox.TextChanged += (o, e) => writeKeyword(keyword.Text.Trim());

            MirLabel hint = new MirLabel
            {
                AutoSize = true,
                Parent = this,
                Location = new Point(330, y),
                ForeColour = Color.Gray,
                Text = Text(ClientTextKeys.AssistKeywordHint)
            };

            _pageControls[ProtectionPage].Add(condition);
            _pageControls[ProtectionPage].Add(percent);
            _pageControls[ProtectionPage].Add(percentLabel);
            _pageControls[ProtectionPage].Add(useLabel);
            _pageControls[ProtectionPage].Add(keyword);
            _pageControls[ProtectionPage].Add(hint);
        }

        private MirTextBox CreateTextBox(Point location, Size size, bool onlyNumber)
        {
            MirTextBox textBox = new MirTextBox
            {
                Parent = this,
                Location = location,
                Size = size,
                MaxLength = onlyNumber ? 3 : 24,
                CanLoseFocus = true,
                Font = new Font(Settings.FontFamily, 8F),
                BackColour = Color.White,
                ForeColour = Color.Black
            };
            if (onlyNumber)
                textBox.TextBox.KeyPress += NumberTextBox_KeyPress;

            textBox.TextBox.KeyDown += ForwardFunctionKeyDown;
            textBox.TextBox.KeyUp += ForwardFunctionKeyUp;
            return textBox;
        }

        private static void NumberTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
                e.Handled = true;
        }

        private static void ForwardFunctionKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.F12)
                return;

            CMain.CMain_KeyDown(sender, e);
            e.SuppressKeyPress = true;
        }

        private static void ForwardFunctionKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F12)
                CMain.CMain_KeyUp(sender, e);
        }

        private void SwitchPage(int page)
        {
            _currentPage = page;
            for (int i = 0; i < _pageControls.Length; i++)
                foreach (MirControl control in _pageControls[i])
                    control.Visible = i == page;

            for (int i = 0; i < _tabs.Length; i++)
            {
                bool selected = i == page;
                _tabs[i].BackColour = selected
                    ? Color.FromArgb(235, 91, 61, 31)
                    : Color.FromArgb(220, 28, 25, 22);
                _tabs[i].FontColour = selected ? Color.Gold : Color.Gainsboro;
            }

            if (page == ItemPage)
                UpdateItemFilters();
        }

        private void RefreshControls()
        {
            foreach (ToggleBinding binding in _toggleBindings)
                binding.Control.Checked = binding.Read();

            RefreshCombatControls();
        }

        private void RefreshCombatControls()
        {
            if (_huntModeDropDown == null || _combatSpellDropDown == null)
                return;

            _huntModeDropDown.Items = new List<string>
            {
                Text(ClientTextKeys.AssistSearchVisible),
                Text(ClientTextKeys.AssistSearchNearby),
                Text(ClientTextKeys.AssistSearchCurrentMap)
            };
            int huntMode = (int)Settings.AssistHuntMode;
            if (huntMode < 0 || huntMode > (int)AssistSearchMode.CurrentMap)
            {
                Settings.AssistHuntMode = AssistSearchMode.Nearby;
                huntMode = (int)Settings.AssistHuntMode;
            }
            _huntModeDropDown.SelectedIndex = huntMode;

            _combatSpellOptions.Clear();
            _combatSpellOptions.Add(Spell.None);

            UserObject user = GameScene.User;
            if (user != null)
            {
                foreach (ClientMagic magic in user.Magics
                             .Where(x => x != null && AssistController.IsAutoCombatSpell(x.Spell))
                             .GroupBy(x => x.Spell)
                             .Select(x => x.First())
                             .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    _combatSpellOptions.Add(magic.Spell);
            }

            _combatSpellDropDown.Items = new List<string>
            {
                Text(ClientTextKeys.AssistNoCombatSkill)
            };
            if (user != null)
            {
                foreach (Spell spell in _combatSpellOptions.Skip(1))
                {
                    ClientMagic magic = user.Magics.FirstOrDefault(x => x != null && x.Spell == spell);
                    _combatSpellDropDown.Items.Add(magic?.Name ?? spell.ToString());
                }
            }

            Spell selectedSpell = user == null ? Spell.None : Settings.GetAssistCombatSpell(user.Class);
            int selectedIndex = _combatSpellOptions.IndexOf(selectedSpell);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
                if (user != null && selectedSpell != Spell.None)
                    Settings.SetAssistCombatSpell(user.Class, Spell.None);
            }
            _combatSpellDropDown.SelectedIndex = selectedIndex;
        }

        private static void RefreshPlayerAppearances()
        {
            foreach (PlayerObject player in MapControl.Objects.Values.OfType<PlayerObject>())
                player.SetLibraries();
        }

        private static string Text(ClientTextKeys key)
        {
            return GameLanguage.ClientTextMap.GetLocalization(key);
        }

        public override void Hide()
        {
            if (!Visible)
                return;

            Visible = false;
            GameScene.Scene?.AssistController?.SaveItemFilters();
            Settings.Save();
        }

        public override void Show()
        {
            if (Visible)
                return;

            GameScene.Scene?.AssistController?.ReloadItemExclusions();
            RefreshControls();
            SwitchPage(_currentPage);
            UpdateItemFilters();
            Visible = true;
            BringToFront();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _backgroundTexture?.Dispose();
                _backgroundTexture = null;
            }

            base.Dispose(disposing);
        }
    }

    public sealed class AssistOverlayDialog : MirControl
    {
        private readonly MirLabel _pingLabel;
        private readonly MirLabel[] _groupLabels = new MirLabel[Globals.MaxGroup];
        private long _nextUpdate;

        public AssistOverlayDialog()
        {
            Size = new Size(Settings.ScreenWidth, Settings.ScreenHeight);
            NotControl = true;

            _pingLabel = new MirLabel
            {
                AutoSize = true,
                Parent = this,
                NotControl = true,
                OutLine = true,
                OutLineColour = Color.Black,
                ForeColour = Color.Gainsboro
            };

            for (int i = 0; i < _groupLabels.Length; i++)
            {
                _groupLabels[i] = new MirLabel
                {
                    AutoSize = true,
                    Parent = this,
                    NotControl = true,
                    Location = new Point(10, 50 + i * 18),
                    OutLine = true,
                    OutLineColour = Color.Black,
                    Visible = false
                };
            }
        }

        public void Process()
        {
            if (CMain.Time < _nextUpdate)
                return;

            _nextUpdate = CMain.Time + 250;
            Size = new Size(Settings.ScreenWidth, Settings.ScreenHeight);

            _pingLabel.Visible = Settings.AssistShowPing;
            if (_pingLabel.Visible)
            {
                _pingLabel.Text = $"Ping: {CMain.PingTime} ms  FPS: {CMain.FPS}";
                _pingLabel.Location = new Point(Math.Max(5, Settings.ScreenWidth - _pingLabel.Size.Width - 10), 6);
            }

            UpdateGroupStatus();
        }

        private void UpdateGroupStatus()
        {
            foreach (MirLabel label in _groupLabels)
                label.Visible = false;

            if (!Settings.AssistShowGroupInfo || GameScene.User == null)
                return;

            List<string> names = new List<string> { GameScene.User.Name };
            foreach (string name in GroupDialog.GroupList)
                if (!names.Contains(name))
                    names.Add(name);

            if (names.Count <= 1)
                return;

            for (int i = 0; i < names.Count && i < _groupLabels.Length; i++)
            {
                string name = names[i];
                PlayerObject player = name == GameScene.User.Name
                    ? GameScene.User
                    : MapControl.Objects.Values.OfType<PlayerObject>().FirstOrDefault(x => x.Name == name);

                MirLabel label = _groupLabels[i];
                label.Visible = true;
                label.ForeColour = player == null ? Color.Gray : Color.White;

                string health = "--";
                if (player is UserObject user && user.Stats != null && user.Stats[Stat.HP] > 0)
                    health = $"{Math.Max(0, user.HP):#,##0}/{Math.Max(1, user.Stats[Stat.HP]):#,##0}";
                else if (player?.ExactHealthKnown == true && player.ExactMaxHealth > 0 && CMain.Time < player.ExactHealthTime)
                    health = $"{Math.Max(0, player.ExactHealth):#,##0}/{Math.Max(1, player.ExactMaxHealth):#,##0}";
                else if (player?.HealthKnown == true)
                    health = $"{player.PercentHealth}%";

                label.Text = $"{name}  {health}";
            }
        }
    }
}
