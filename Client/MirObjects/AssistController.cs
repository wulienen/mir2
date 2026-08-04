using Client.MirNetwork;
using Client.MirScenes;
using System.Text;
using System.Text.RegularExpressions;

using C = ClientPackets;

namespace Client.MirObjects
{
    public sealed class AssistItemFilter
    {
        public string Name { get; set; }
        public bool Pick { get; set; }
    }

    public sealed class AssistController
    {
        private const int AutoTargetRange = 20;
        private static readonly string[] IgnoredMonsterNames =
        {
            "变异骷髅", "大刀", "弓箭手", "神兽", "月灵", "带刀"
        };
        private readonly long[] _nextProtectionUse = new long[3];
        private readonly Dictionary<string, AssistItemFilter> _itemFilters =
            new Dictionary<string, AssistItemFilter>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, long> _pickupRetryAfter = new Dictionary<uint, long>();
        private long _nextProcessTime;
        private long _nextPickupProcess;
        private byte _nextPoisonShape = 1;
        private uint _autoAttackTargetId;
        private uint _pickupTargetId;
        private bool _autoPathOwned;

        public void Process()
        {
            if (GameScene.Scene == null || GameScene.User == null || GameScene.User.Dead)
                return;

            if (CMain.Time < _nextProcessTime)
                return;

            _nextProcessTime = CMain.Time + 100;

            ProcessProtection();
            ProcessAutomaticSpells();

            if (Settings.AssistAutoAttack || Settings.AssistAutoPickup)
                ProcessAutoActions();
            else
                ClearAutomaticTargets();
        }

        public void InitializeItemFilters()
        {
            _itemFilters.Clear();
            _pickupRetryAfter.Clear();
            _pickupTargetId = 0;
            _autoAttackTargetId = 0;

            UserObject user = GameScene.User;
            if (user == null)
                return;

            string path = GetFilterPath(user.Name);
            if (!File.Exists(path))
            {
                path = GetLegacyFilterPath(user.Name);
                if (!File.Exists(path))
                    return;
            }

            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (!TryParseFilter(line, out string name, out bool pick))
                    continue;

                _itemFilters[name] = new AssistItemFilter { Name = name, Pick = pick };
            }
        }

        public void SaveItemFilters()
        {
            UserObject user = GameScene.User;
            if (user == null)
                return;

            string path = GetFilterPath(user.Name);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            List<string> lines = _itemFilters.Values
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"{x.Name.Replace("\t", " ")}\t{x.Pick}")
                .ToList();
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        public IReadOnlyList<AssistItemFilter> GetItemFilters()
        {
            return _itemFilters.Values
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void SetItemFilter(string name, bool pick)
        {
            name = NormalizeItemName(name);
            if (string.IsNullOrEmpty(name))
                return;

            if (!_itemFilters.TryGetValue(name, out AssistItemFilter filter))
            {
                filter = new AssistItemFilter { Name = name };
                _itemFilters[name] = filter;
            }

            filter.Pick = pick;
        }

        public void ClearAutomaticTargets()
        {
            MapControl map = GameScene.Scene?.MapControl;
            if (_autoPathOwned && map != null)
                map.AutoPath = false;

            _autoPathOwned = false;
            _pickupTargetId = 0;

            if (_autoAttackTargetId != 0 && MapObject.TargetObjectID == _autoAttackTargetId)
                MapObject.TargetObjectID = 0;

            _autoAttackTargetId = 0;
        }

        private void ProcessAutoActions()
        {
            UserObject user = GameScene.User;
            MapControl map = GameScene.Scene.MapControl;

            if (user.NextMagic != null || user.QueuedAction != null || MapControl.MapButtons != MouseButtons.None)
                return;

            if (!map.AutoPath)
                _autoPathOwned = false;

            MapObject selectedTarget = MapObject.TargetObject;
            if (selectedTarget != null && !selectedTarget.Dead && selectedTarget.ObjectID != _autoAttackTargetId)
            {
                if (_autoPathOwned)
                {
                    map.AutoPath = false;
                    _autoPathOwned = false;
                }

                _pickupTargetId = 0;
                _autoAttackTargetId = 0;
                return;
            }

            if (Settings.AssistAutoAttack)
            {
                MonsterObject target = selectedTarget as MonsterObject;
                if (!CanAttack(target))
                {
                    MapObject.TargetObjectID = 0;
                    target = FindNearestMonster(user);
                    if (target != null)
                    {
                        _autoAttackTargetId = target.ObjectID;
                        MapObject.TargetObjectID = target.ObjectID;
                    }
                }
                else if (_autoAttackTargetId == 0)
                {
                    _autoAttackTargetId = target.ObjectID;
                }

                if (target != null)
                {
                    if (_autoPathOwned)
                    {
                        map.AutoPath = false;
                        _autoPathOwned = false;
                    }

                    return;
                }
            }

            if (Settings.AssistAutoPickup)
                ProcessAutoPickup(user, map);
        }

        private void ProcessAutoPickup(UserObject user, MapControl map)
        {
            if (CMain.Time < _nextPickupProcess)
                return;

            _nextPickupProcess = CMain.Time + 200;
            RemoveExpiredPickupRetries();
            ItemObject item = null;
            if (_pickupTargetId != 0 && MapControl.Objects.TryGetValue(_pickupTargetId, out MapObject current))
                item = current as ItemObject;

            if (item == null || IsPickupCoolingDown(item.ObjectID) || !ShouldPickItem(item.Name) ||
                Functions.MaxDistance(user.CurrentLocation, item.CurrentLocation) > AutoTargetRange)
            {
                if (_autoPathOwned)
                {
                    map.AutoPath = false;
                    _autoPathOwned = false;
                }

                item = FindNearestItem(user);
                _pickupTargetId = item?.ObjectID ?? 0;
            }

            if (item == null)
                return;

            if (item.CurrentLocation == user.CurrentLocation)
            {
                if (CMain.Time >= GameScene.PickUpTime)
                {
                    GameScene.PickUpTime = CMain.Time + 200;
                    Network.Enqueue(new C.PickUp());
                    _pickupRetryAfter[item.ObjectID] = CMain.Time + 2000;
                    _pickupTargetId = 0;
                }

                if (_autoPathOwned)
                {
                    map.AutoPath = false;
                    _autoPathOwned = false;
                }

                return;
            }

            if (map.AutoPath)
                return;

            if (map.PathFinder == null)
                return;

            List<Node> path = map.PathFinder.FindPath(user.CurrentLocation, item.CurrentLocation, AutoTargetRange);
            if (path == null || path.Count == 0)
            {
                _pickupRetryAfter[item.ObjectID] = CMain.Time + 2000;
                _pickupTargetId = 0;
                return;
            }

            map.CurrentPath = path;
            map.AutoPath = true;
            _autoPathOwned = true;
        }

        private ItemObject FindNearestItem(UserObject user)
        {
            ItemObject result = null;
            int nearest = int.MaxValue;
            foreach (MapObject mapObject in MapControl.Objects.Values)
            {
                if (!(mapObject is ItemObject item) || IsPickupCoolingDown(item.ObjectID) ||
                    !ShouldPickItem(item.Name))
                    continue;

                int distance = Functions.MaxDistance(user.CurrentLocation, item.CurrentLocation);
                if (distance > AutoTargetRange || distance >= nearest)
                    continue;

                nearest = distance;
                result = item;
            }

            return result;
        }

        private bool IsPickupCoolingDown(uint objectId)
        {
            return _pickupRetryAfter.TryGetValue(objectId, out long retryTime) && CMain.Time < retryTime;
        }

        private void RemoveExpiredPickupRetries()
        {
            foreach (uint objectId in _pickupRetryAfter
                         .Where(pair => pair.Value <= CMain.Time || !MapControl.Objects.ContainsKey(pair.Key))
                         .Select(pair => pair.Key)
                         .ToList())
                _pickupRetryAfter.Remove(objectId);
        }

        private static MonsterObject FindNearestMonster(UserObject user)
        {
            MonsterObject result = null;
            int nearest = int.MaxValue;
            foreach (MapObject mapObject in MapControl.Objects.Values)
            {
                if (!(mapObject is MonsterObject monster) || !CanAttack(monster))
                    continue;

                int distance = Functions.MaxDistance(user.CurrentLocation, monster.CurrentLocation);
                if (distance > AutoTargetRange || distance >= nearest)
                    continue;

                nearest = distance;
                result = monster;
            }

            return result;
        }

        private static bool CanAttack(MonsterObject monster)
        {
            return monster != null && monster.MasterObjectId == 0 && !monster.Dead &&
                   !monster.Hidden && monster.AI != 64 && monster.AI != 70 &&
                   !IgnoredMonsterNames.Any(name => monster.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        private bool ShouldPickItem(string name)
        {
            name = NormalizeItemName(name);
            if (string.IsNullOrEmpty(name))
                return false;

            if (!_itemFilters.TryGetValue(name, out AssistItemFilter filter))
            {
                filter = new AssistItemFilter { Name = name, Pick = true };
                _itemFilters[name] = filter;
            }

            return filter.Pick;
        }

        private static string NormalizeItemName(string name)
        {
            return Regex.Replace(name ?? string.Empty, @"\s*\([\d,]+\)\s*$", string.Empty).Trim();
        }

        private static string GetFilterPath(string characterName)
        {
            return Path.Combine("Configs", GetSafeCharacterName(characterName) + "_assist_filter.txt");
        }

        private static string GetLegacyFilterPath(string characterName)
        {
            return Path.Combine("Configs", GetSafeCharacterName(characterName) + "_filter.txt");
        }

        private static string GetSafeCharacterName(string characterName)
        {
            return string.Concat((characterName ?? "character").Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        }

        private static bool TryParseFilter(string line, out string name, out bool pick)
        {
            name = string.Empty;
            pick = true;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            int tab = line.IndexOf('\t');
            if (tab > 0 && bool.TryParse(line.Substring(tab + 1).Trim(), out pick))
            {
                name = NormalizeItemName(line.Substring(0, tab));
                return !string.IsNullOrEmpty(name);
            }

            string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !bool.TryParse(parts[^1], out bool lastValue))
                return false;

            int nameEnd = parts.Length - 1;
            pick = lastValue;
            if (parts.Length >= 3 && bool.TryParse(parts[^2], out bool pickValue))
            {
                pick = pickValue;
                nameEnd--;
            }

            name = NormalizeItemName(string.Join(" ", parts, 0, nameEnd));
            return !string.IsNullOrEmpty(name);
        }

        private void ProcessAutomaticSpells()
        {
            UserObject user = GameScene.User;
            GameScene scene = GameScene.Scene;

            if (Settings.AssistAutoFlamingSword && !user.FlamingSword && scene.TryUseAssistSpell(Spell.FlamingSword))
                return;

            if (Settings.AssistAutoTwinDrakeBlade && !user.TwinDrakeBlade && scene.TryUseAssistSpell(Spell.TwinDrakeBlade))
                return;

            if (Settings.AssistAutoMagicShield && !user.MagicShield && scene.TryUseAssistSpell(Spell.MagicShield))
                return;

            if (Settings.AssistAutoElementalBarrier && user.HasElements && !user.ElementalBarrier)
                scene.TryUseAssistSpell(Spell.ElementalBarrier);
        }

        private void ProcessProtection()
        {
            if (!Settings.AssistProtectionEnabled)
                return;

            UserObject user = GameScene.User;
            int maxHealth = Math.Max(1, user.Stats[Stat.HP]);
            int maxMana = Math.Max(1, user.Stats[Stat.MP]);
            int healthPercent = Math.Clamp(user.HP * 100 / maxHealth, 0, 100);
            int manaPercent = Math.Clamp(user.MP * 100 / maxMana, 0, 100);

            if (TryUseProtectionItem(2, healthPercent, Settings.AssistEmergencyPercent, Settings.AssistEmergencyKeyword))
                return;

            if (TryUseProtectionItem(0, healthPercent, Settings.AssistHealthPotionPercent, Settings.AssistHealthPotionKeyword))
                return;

            TryUseProtectionItem(1, manaPercent, Settings.AssistManaPotionPercent, Settings.AssistManaPotionKeyword);
        }

        private bool TryUseProtectionItem(int index, int currentPercent, int triggerPercent, string keyword)
        {
            if (currentPercent > triggerPercent || CMain.Time < _nextProtectionUse[index] ||
                CMain.Time < GameScene.UseItemTime || string.IsNullOrWhiteSpace(keyword))
                return false;

            UserItem item = FindInventoryItem(keyword);
            if (item == null)
            {
                _nextProtectionUse[index] = CMain.Time + 1000;
                return false;
            }

            Network.Enqueue(new C.UseItem { UniqueID = item.UniqueID, Grid = MirGridType.Inventory });
            _nextProtectionUse[index] = CMain.Time + Settings.AssistUseItemInterval;
            GameScene.UseItemTime = CMain.Time + 300;
            return true;
        }

        private static UserItem FindInventoryItem(string keyword)
        {
            foreach (UserItem item in GameScene.User.Inventory)
            {
                if (item?.Info == null || !item.Info.IsConsumable)
                    continue;

                if ((item.Info.Name ?? string.Empty).Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    (item.Info.FriendlyName ?? string.Empty).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return item;
            }

            return null;
        }

        public void PrepareConsumables(Spell spell, UserObject actor)
        {
            if (!Settings.AssistAutoPoisonAmulet || actor != GameScene.User)
                return;

            switch (spell)
            {
                case Spell.Poisoning:
                    if (EnsureEquipped(actor, _nextPoisonShape, 1))
                        _nextPoisonShape = _nextPoisonShape == 1 ? (byte)2 : (byte)1;
                    break;
                case Spell.PoisonCloud:
                    EnsureEquipped(actor, 0, 5);
                    EnsureEquipped(actor, 1, 5);
                    break;
                case Spell.SoulFireBall:
                case Spell.SummonSkeleton:
                case Spell.Hiding:
                case Spell.MassHiding:
                case Spell.SoulShield:
                case Spell.TrapHexagon:
                case Spell.Curse:
                case Spell.Plague:
                case Spell.UltimateEnhancer:
                case Spell.BlessedArmour:
                    EnsureEquipped(actor, 0, 1);
                    break;
                case Spell.SummonHolyDeva:
                    EnsureEquipped(actor, 0, 2);
                    break;
                case Spell.SummonShinsu:
                    EnsureEquipped(actor, 0, 5);
                    break;
            }
        }

        private static bool EnsureEquipped(UserObject actor, short shape, ushort count)
        {
            foreach (UserItem item in actor.Equipment)
            {
                if (MatchesAmulet(item, shape, count))
                    return true;
            }

            UserItem inventoryItem = null;
            foreach (UserItem item in actor.Inventory)
            {
                if (!MatchesAmulet(item, shape, count))
                    continue;

                inventoryItem = item;
                break;
            }

            if (inventoryItem == null)
                return false;

            EquipmentSlot destination = shape == 0 ? EquipmentSlot.Amulet : EquipmentSlot.BraceletR;
            Network.Enqueue(new C.EquipItem
            {
                Grid = MirGridType.Inventory,
                UniqueID = inventoryItem.UniqueID,
                To = (int)destination
            });
            return true;
        }

        private static bool MatchesAmulet(UserItem item, short shape, ushort count)
        {
            return item?.Info != null &&
                   item.Info.Type == ItemType.Amulet &&
                   item.Info.Shape == shape &&
                   item.Count >= count;
        }
    }
}
