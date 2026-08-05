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
        private const int NearbyPatrolRange = 20;
        private const int CurrentMapPatrolSegmentRange = 18;
        private const int PatrolCandidateAttempts = 16;
        private static readonly HashSet<byte> NonCombatMonsterAIs = new HashSet<byte>
        {
            6, 56, 57, 58, 64, 68, 70, 80, 81, 82, 113
        };
        private static readonly HashSet<Spell> AutoCombatSpells = new HashSet<Spell>
        {
            // Warrior and shared close-range attacks. Displacement skills are
            // intentionally omitted so combat cannot move the player itself.
            Spell.Entrapment, Spell.LionRoar, Spell.BladeAvalanche, Spell.BattleCry,

            // Wizard attacks. ElectricShock is deliberately excluded because it tames monsters.
            Spell.FireBall, Spell.Repulsion, Spell.GreatFireBall, Spell.HellFire, Spell.ThunderBolt,
            Spell.FireBang, Spell.FireWall, Spell.Lightning, Spell.FrostCrunch, Spell.ThunderStorm,
            Spell.TurnUndead, Spell.Vampirism, Spell.IceStorm, Spell.FlameDisruptor, Spell.FlameField,
            Spell.Blizzard, Spell.MeteorStrike, Spell.IceThrust, Spell.FireBounce, Spell.MeteorShower,

            // Taoist attacks and debuffs.
            Spell.Poisoning, Spell.SoulFireBall, Spell.EnergyRepulsor, Spell.TrapHexagon,
            Spell.Hallucination, Spell.Curse, Spell.Plague, Spell.PoisonCloud,

            // Assassin attacks.
            Spell.FireBurst, Spell.HeavenlySword, Spell.Trap, Spell.CatTongue, Spell.PoisonSword,
            Spell.CrescentSlash, Spell.MoonMist,

            // Archer attacks.
            Spell.StraightShot, Spell.DoubleShot, Spell.ExplosiveTrap, Spell.DelayedExplosion,
            Spell.ElementalShot, Spell.BindingShot, Spell.VampireShot,
            Spell.PoisonShot, Spell.CrippleShot, Spell.NapalmShot, Spell.OneWithNature
        };

        private enum AutomaticPathOwner
        {
            None,
            Pickup,
            Patrol
        }

        private readonly long[] _nextProtectionUse = new long[3];
        private readonly Dictionary<string, AssistItemFilter> _itemFilters =
            new Dictionary<string, AssistItemFilter>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, long> _pickupRetryAfter = new Dictionary<uint, long>();
        private long _nextProcessTime;
        private long _nextPickupProcess;
        private long _nextPatrolProcess;
        private byte _nextPoisonShape = 1;
        private uint _autoAttackTargetId;
        private uint _pickupTargetId;
        private AutomaticPathOwner _pathOwner;
        private Point _patrolAnchor;
        private int _patrolMapIndex = int.MinValue;
        private bool _patrolAnchorSet;
        private bool _resetAnchorAfterManualInput;
        private bool _automaticSpellPending;

        public void Process()
        {
            if (GameScene.Scene == null || GameScene.User == null)
                return;

            if (GameScene.User.Dead)
            {
                ClearAutomaticTargets();
                return;
            }

            if (CMain.Time < _nextProcessTime)
                return;

            _nextProcessTime = CMain.Time + 100;

            if (_automaticSpellPending && (GameScene.User.NextMagic == null ||
                                           !IsAutoCombatSpell(GameScene.User.NextMagic.Spell)))
                _automaticSpellPending = false;

            ProcessProtection();
            if (ProcessAutomaticSpells())
                return;

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
            _pathOwner = AutomaticPathOwner.None;
            _patrolMapIndex = int.MinValue;
            _patrolAnchorSet = false;
            _resetAnchorAfterManualInput = false;
            _automaticSpellPending = false;

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
            CancelOwnedPath(map);
            _pickupTargetId = 0;

            if (_automaticSpellPending && GameScene.User?.NextMagic != null &&
                IsAutoCombatSpell(GameScene.User.NextMagic.Spell))
                GameScene.User.ClearMagic();

            _automaticSpellPending = false;

            if (_autoAttackTargetId != 0 && MapObject.TargetObjectID == _autoAttackTargetId)
                MapObject.TargetObjectID = 0;
            if (_autoAttackTargetId != 0 && MapObject.MagicObjectID == _autoAttackTargetId)
                MapObject.MagicObjectID = 0;

            _autoAttackTargetId = 0;
            _patrolMapIndex = int.MinValue;
            _patrolAnchorSet = false;
            _resetAnchorAfterManualInput = false;
        }

        /// <summary>
        /// Marks input that originated from the player. Automatic paths and a
        /// pending automatic spell must not continue after manual control.
        /// </summary>
        public void NotifyManualInput()
        {
            MapControl map = GameScene.Scene?.MapControl;
            if (_pathOwner == AutomaticPathOwner.Patrol)
                _nextPatrolProcess = Math.Max(_nextPatrolProcess, CMain.Time + 1000);
            else if (_pathOwner == AutomaticPathOwner.Pickup && _pickupTargetId != 0)
                _pickupRetryAfter[_pickupTargetId] = CMain.Time + 1000;

            CancelOwnedPath(map);
            _pickupTargetId = 0;

            if (_automaticSpellPending && GameScene.User?.NextMagic != null &&
                IsAutoCombatSpell(GameScene.User.NextMagic.Spell))
                GameScene.User.ClearMagic();

            _automaticSpellPending = false;
            if (Settings.AssistAutoAttack)
                _resetAnchorAfterManualInput = true;
        }

        /// <summary>
        /// Removes targets that the automatic combat workflow is not allowed to attack.
        /// Manual targeting remains untouched while automatic attack is disabled.
        /// </summary>
        public void ValidateAutomaticTargets()
        {
            if (!Settings.AssistAutoAttack)
                return;

            MapObject target = MapObject.TargetObject;
            if (target != null && !CanAutoAttackTarget(target))
            {
                if (MapObject.MagicObjectID == target.ObjectID)
                    MapObject.MagicObjectID = 0;

                MapObject.TargetObjectID = 0;
                _autoAttackTargetId = 0;
            }

            MapObject magicTarget = MapObject.MagicObject;
            if (magicTarget != null && !CanAutoAttackTarget(magicTarget))
                MapObject.MagicObjectID = 0;

            if (GameScene.User.NextMagic != null && IsAutoCombatSpell(GameScene.User.NextMagic.Spell) &&
                GameScene.User.NextMagicObject != null &&
                !CanAutoAttackTarget(GameScene.User.NextMagicObject))
            {
                GameScene.User.ClearMagic();
                _automaticSpellPending = false;
            }
        }

        private void ProcessAutoActions()
        {
            UserObject user = GameScene.User;
            MapControl map = GameScene.Scene.MapControl;

            if (!map.AutoPath && _pathOwner != AutomaticPathOwner.None)
            {
                if (_pathOwner == AutomaticPathOwner.Patrol)
                    _nextPatrolProcess = Math.Max(_nextPatrolProcess, CMain.Time + 1000);
                else if (_pathOwner == AutomaticPathOwner.Pickup && _pickupTargetId != 0)
                    _pickupRetryAfter[_pickupTargetId] = CMain.Time + 1000;

                _pathOwner = AutomaticPathOwner.None;
            }

            if (Settings.AssistAutoAttack)
            {
                EnsureAutoCombatContext(user, map);

                if (MapControl.MapButtons != MouseButtons.None || (map.AutoPath && _pathOwner == AutomaticPathOwner.None))
                {
                    CancelOwnedPath(map);
                    _resetAnchorAfterManualInput = true;
                    return;
                }

                if (_resetAnchorAfterManualInput)
                {
                    if (user.QueuedAction != null || map.AutoPath)
                        return;

                    _patrolAnchor = user.CurrentLocation;
                    _patrolAnchorSet = true;
                    _resetAnchorAfterManualInput = false;
                }
            }
            else if (MapControl.MapButtons != MouseButtons.None)
                return;

            if (Settings.AssistAutoAttack)
            {
                ValidateAutomaticTargets();
                MonsterObject target = MapObject.TargetObject as MonsterObject;
                if (!CanAutoAttackTarget(target))
                {
                    if (MapObject.TargetObject != null && MapObject.MagicObjectID == MapObject.TargetObject.ObjectID)
                        MapObject.MagicObjectID = 0;

                    MapObject.TargetObjectID = 0;
                    target = FindNearestMonster(user);
                    if (target != null)
                    {
                        _autoAttackTargetId = target.ObjectID;
                        MapObject.TargetObjectID = target.ObjectID;
                        MapObject.MagicObjectID = target.ObjectID;
                    }
                }
                else
                {
                    _autoAttackTargetId = target.ObjectID;
                }

                if (target != null)
                {
                    bool ownedPath = _pathOwner != AutomaticPathOwner.None;
                    CancelOwnedPath(map);
                    _pickupTargetId = 0;

                    // A path action may have been queued before a newly visible
                    // monster was selected. Drop only actions owned by this
                    // controller; manual actions remain intact.
                    if (ownedPath && MapControl.MapButtons == MouseButtons.None)
                        user.QueuedAction = null;

                    if (user.NextMagic == null && user.QueuedAction == null)
                        TryUseAutoCombatSpell(user, target);

                    return;
                }
            }

            if (user.NextMagic != null)
                return;

            if (user.QueuedAction != null)
                return;

            if (Settings.AssistAutoPickup && ProcessAutoPickup(user, map, Settings.AssistAutoAttack))
                return;

            if (Settings.AssistAutoAttack)
                ProcessPatrol(user, map);
        }

        private bool ProcessAutoPickup(UserObject user, MapControl map, bool allowMovement)
        {
            if (CMain.Time < _nextPickupProcess)
                return _pickupTargetId != 0 || (_pathOwner == AutomaticPathOwner.Pickup && map.AutoPath);

            _nextPickupProcess = CMain.Time + 200;
            RemoveExpiredPickupRetries();

            // Auto pickup by itself only collects items already on the player's cell.
            // Any pathing to an item is reserved for the auto-attack workflow.
            if (!allowMovement && _pathOwner == AutomaticPathOwner.Pickup)
                CancelOwnedPath(map);

            ItemObject item = null;
            if (_pickupTargetId != 0 && MapControl.Objects.TryGetValue(_pickupTargetId, out MapObject current))
                item = current as ItemObject;

            if (item == null || IsPickupCoolingDown(item.ObjectID) || !ShouldPickItem(item.Name) ||
                Functions.MaxDistance(user.CurrentLocation, item.CurrentLocation) > AutoTargetRange ||
                (!allowMovement && item.CurrentLocation != user.CurrentLocation))
            {
                if (_pathOwner == AutomaticPathOwner.Pickup)
                    CancelOwnedPath(map);

                item = allowMovement ? FindNearestItem(user) : FindItemAtLocation(user);
                _pickupTargetId = item?.ObjectID ?? 0;
            }

            if (item == null)
                return false;

            if (_pathOwner == AutomaticPathOwner.Patrol)
                CancelOwnedPath(map);

            if (item.CurrentLocation == user.CurrentLocation)
            {
                if (CMain.Time >= GameScene.PickUpTime)
                {
                    GameScene.PickUpTime = CMain.Time + 200;
                    Network.Enqueue(new C.PickUp());
                    _pickupRetryAfter[item.ObjectID] = CMain.Time + 2000;
                    _pickupTargetId = 0;
                }

                if (_pathOwner == AutomaticPathOwner.Pickup)
                    CancelOwnedPath(map);

                return true;
            }

            if (!allowMovement)
            {
                _pickupTargetId = 0;
                return false;
            }

            if (map.AutoPath)
                return _pathOwner == AutomaticPathOwner.Pickup;

            if (map.PathFinder == null)
                return false;

            List<Node> path = map.PathFinder.FindPath(user.CurrentLocation, item.CurrentLocation, AutoTargetRange);
            if (path == null || path.Count == 0)
            {
                _pickupRetryAfter[item.ObjectID] = CMain.Time + 2000;
                _pickupTargetId = 0;
                return false;
            }

            SetOwnedPath(map, path, AutomaticPathOwner.Pickup);
            return true;
        }

        private ItemObject FindItemAtLocation(UserObject user)
        {
            foreach (MapObject mapObject in MapControl.Objects.Values)
            {
                if (mapObject is ItemObject item && item.CurrentLocation == user.CurrentLocation &&
                    !IsPickupCoolingDown(item.ObjectID) && ShouldPickItem(item.Name))
                    return item;
            }

            return null;
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

        private MonsterObject FindNearestMonster(UserObject user)
        {
            MonsterObject result = null;
            int nearest = int.MaxValue;
            foreach (MapObject mapObject in MapControl.Objects.Values)
            {
                if (!(mapObject is MonsterObject monster) || !CanAutoAttackTarget(monster))
                    continue;

                int distance = Functions.MaxDistance(user.CurrentLocation, monster.CurrentLocation);
                if (distance > nearest || (distance == nearest && result != null && monster.ObjectID >= result.ObjectID))
                    continue;

                nearest = distance;
                result = monster;
            }

            return result;
        }

        public static bool IsAutoCombatTarget(MapObject mapObject)
        {
            return mapObject is MonsterObject monster && monster.MasterObjectId == 0 &&
                   !monster.Dead && !monster.Hidden && !NonCombatMonsterAIs.Contains(monster.AI);
        }

        public bool CanAutoAttackTarget(MapObject mapObject)
        {
            if (!IsAutoCombatTarget(mapObject))
                return false;

            MonsterObject monster = (MonsterObject)mapObject;
            if (!MapControl.Objects.TryGetValue(monster.ObjectID, out MapObject loaded) ||
                !ReferenceEquals(loaded, monster))
                return false;

            if (Settings.AssistHuntMode != AssistSearchMode.Nearby || !_patrolAnchorSet ||
                _resetAnchorAfterManualInput)
                return true;

            return Functions.MaxDistance(_patrolAnchor, mapObject.CurrentLocation) <= NearbyPatrolRange;
        }

        public static bool IsAutoCombatSpell(Spell spell)
        {
            return AutoCombatSpells.Contains(spell);
        }

        private void EnsureAutoCombatContext(UserObject user, MapControl map)
        {
            if (_patrolMapIndex == map.Index && _patrolAnchorSet)
                return;

            CancelOwnedPath(map);
            _pickupTargetId = 0;
            _autoAttackTargetId = 0;
            _patrolMapIndex = map.Index;
            _patrolAnchor = user.CurrentLocation;
            _patrolAnchorSet = true;
            _resetAnchorAfterManualInput = false;
        }

        private void ProcessPatrol(UserObject user, MapControl map)
        {
            if (Settings.AssistHuntMode == AssistSearchMode.VisibleOnly)
            {
                if (_pathOwner == AutomaticPathOwner.Patrol)
                    CancelOwnedPath(map);
                return;
            }

            if (_pathOwner == AutomaticPathOwner.Patrol && map.AutoPath)
                return;

            if (map.AutoPath || map.PathFinder == null || CMain.Time < _nextPatrolProcess)
                return;

            _nextPatrolProcess = CMain.Time + 1000;
            Point center = Settings.AssistHuntMode == AssistSearchMode.Nearby
                ? _patrolAnchor
                : user.CurrentLocation;
            int range = Settings.AssistHuntMode == AssistSearchMode.Nearby
                ? NearbyPatrolRange
                : CurrentMapPatrolSegmentRange;
            int maximumPathLength = Settings.AssistHuntMode == AssistSearchMode.Nearby
                ? NearbyPatrolRange * 2 + 2
                : CurrentMapPatrolSegmentRange + 2;

            for (int attempt = 0; attempt < PatrolCandidateAttempts; attempt++)
            {
                Point candidate = new Point(
                    center.X + CMain.Random.Next(-range, range + 1),
                    center.Y + CMain.Random.Next(-range, range + 1));
                int candidateDistance = Functions.MaxDistance(center, candidate);
                if (candidateDistance < Math.Max(4, range / 3) || candidateDistance > range ||
                    !map.EmptyCell(candidate))
                    continue;

                List<Node> path = map.PathFinder.FindPath(user.CurrentLocation, candidate, maximumPathLength);
                if (path == null || path.Count <= 1)
                    continue;

                if (Settings.AssistHuntMode == AssistSearchMode.Nearby &&
                    path.Any(node => Functions.MaxDistance(_patrolAnchor, node.Location) > NearbyPatrolRange))
                    continue;

                if (Settings.AssistHuntMode == AssistSearchMode.CurrentMap &&
                    path.Any(node => IsTransferPoint(map, node.Location)))
                    continue;

                SetOwnedPath(map, path, AutomaticPathOwner.Patrol);
                return;
            }

            _nextPatrolProcess = CMain.Time + 2000;
        }

        private static bool IsTransferPoint(MapControl map, Point location)
        {
            if (!GameScene.MapInfoList.TryGetValue(map.Index, out var record) || record.MapInfo == null)
                return false;

            return record.MapInfo.Movements.Any(x => x.Location == location);
        }

        internal bool TryUseAutoCombatSpell(UserObject user, MonsterObject target)
        {
            Spell spell = Settings.GetAssistCombatSpell(user.Class);
            if (spell == Spell.None || !IsAutoCombatSpell(spell) || !CanAutoAttackTarget(target))
                return false;

            if (spell == Spell.ElementalShot && !user.HasElements)
                return false;

            if (!HasRequiredCombatItems(user, spell))
                return false;

            if (user.NextMagic != null || user.QueuedAction != null || user.RidingMount || user.Fishing ||
                CMain.Time < GameScene.SpellTime || CMain.Time < user.BlizzardStopTime ||
                CMain.Time < user.ReincarnationStopTime ||
                user.Poison.HasFlag(PoisonType.Stun) || user.Poison.HasFlag(PoisonType.Paralysis) ||
                user.Poison.HasFlag(PoisonType.LRParalysis) || user.Poison.HasFlag(PoisonType.Frozen) ||
                user.Poison.HasFlag(PoisonType.Dazed))
                return false;

            if ((!user.HasClassWeapon && user.Weapon >= 0) ||
                (user.Class == MirClass.Archer && !user.HasClassWeapon))
                return false;

            ClientMagic magic = user.Magics.FirstOrDefault(x => x.Spell == spell);
            if (magic == null || CMain.Time <= magic.CastTime + magic.Delay)
                return false;

            int cost = magic.Level * magic.LevelCost + magic.BaseCost;
            if (user.Stats[Stat.ManaPenaltyPercent] > 0)
                cost += cost * user.Stats[Stat.ManaPenaltyPercent] / 100;
            if (cost > user.MP)
                return false;

            int range = magic.Range == 0 ? 1 : magic.Range;
            if (!Functions.InRange(user.CurrentLocation, target.CurrentLocation, range))
                return false;

            user.NextMagic = magic;
            user.NextMagicLocation = target.CurrentLocation;
            user.NextMagicObject = target;
            user.NextMagicDirection = Functions.DirectionFromPoint(user.CurrentLocation, target.CurrentLocation);
            _automaticSpellPending = true;
            return true;
        }

        private static bool HasRequiredCombatItems(UserObject user, Spell spell)
        {
            switch (spell)
            {
                case Spell.Poisoning:
                case Spell.PoisonSword:
                    return HasPoison(user, 1);
                case Spell.SoulFireBall:
                case Spell.TrapHexagon:
                    return HasAmulet(user, 1, 0);
                case Spell.Curse:
                case Spell.Plague:
                case Spell.Hallucination:
                    return HasAmulet(user, 1, 0);
                case Spell.PoisonCloud:
                    return HasAmulet(user, 5, 0) && HasPoison(user, 5, 1);
                default:
                    return true;
            }
        }

        private static bool HasAmulet(UserObject user, int count, int shape)
        {
            return user.Equipment.Any(item => item?.Info != null && item.Info.Type == ItemType.Amulet &&
                                               item.Info.Shape == shape && item.Count >= count);
        }

        private static bool HasPoison(UserObject user, int count, int shape = 0)
        {
            return user.Equipment.Any(item => item?.Info != null && item.Info.Type == ItemType.Amulet &&
                                               item.Count >= count &&
                                               (shape == 0 ? item.Info.Shape == 1 || item.Info.Shape == 2 :
                                                             item.Info.Shape == shape));
        }

        private void CancelOwnedPath(MapControl map)
        {
            if (_pathOwner != AutomaticPathOwner.None && map != null && map.AutoPath)
                map.AutoPath = false;

            _pathOwner = AutomaticPathOwner.None;
        }

        private void SetOwnedPath(MapControl map, List<Node> path, AutomaticPathOwner owner)
        {
            map.CurrentPath = path;
            map.AutoPath = true;
            _pathOwner = owner;
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

        private bool ProcessAutomaticSpells()
        {
            UserObject user = GameScene.User;
            GameScene scene = GameScene.Scene;

            if (Settings.AssistAutoFlamingSword && !user.FlamingSword && scene.TryUseAssistSpell(Spell.FlamingSword))
                return true;

            if (Settings.AssistAutoTwinDrakeBlade && !user.TwinDrakeBlade && scene.TryUseAssistSpell(Spell.TwinDrakeBlade))
                return true;

            if (Settings.AssistAutoMagicShield && !user.MagicShield && scene.TryUseAssistSpell(Spell.MagicShield))
                return true;

            if (Settings.AssistAutoElementalBarrier && user.HasElements && !user.ElementalBarrier)
                return scene.TryUseAssistSpell(Spell.ElementalBarrier);

            return false;
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
