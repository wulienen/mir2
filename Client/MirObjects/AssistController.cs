using Client.MirNetwork;
using Client.MirScenes;
using System.Text;
using System.Text.RegularExpressions;

using C = ClientPackets;

namespace Client.MirObjects
{
    public sealed class AssistController
    {
        private const string AutoPickupExcludeFileName = "AutoPickupExclude.txt";
        private const int AutoTargetRange = 9;

        // Slightly wider than the engage radius so a monster that steps one
        // cell out of range is not dropped and re-acquired every tick.
        private const int AutoTargetRetainRange = 12;
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

        // Pets the Taoist keeps alive while automatic combat is running. Each is
        // only cast when its skill is bound on the F11 skill page.
        private static readonly Spell[] AutoSummonSpells =
        {
            Spell.SummonSkeleton, Spell.SummonShinsu, Spell.SummonHolyDeva
        };

        // Skills that only reach the cells around the caster. Casting them from
        // the full server side range would simply waste the mana.
        private static readonly Dictionary<Spell, int> SelfCentredSpellRange = new Dictionary<Spell, int>
        {
            { Spell.Repulsion, 2 },
            { Spell.ThunderStorm, 2 },
            { Spell.FlameField, 2 },
            { Spell.LionRoar, 2 },
            { Spell.BattleCry, 2 },
            { Spell.BladeAvalanche, 1 },
            { Spell.EnergyRepulsor, 2 },
            { Spell.FireBurst, 2 },
            { Spell.CrescentSlash, 1 },
            { Spell.HeavenlySword, 2 },
            { Spell.PoisonSword, 1 },
            { Spell.CatTongue, 1 },
            { Spell.OneWithNature, 2 },
            { Spell.Lightning, 5 },
            { Spell.HellFire, 4 }
        };

        /// <summary>Minimum distance a ranged class tries to keep from a monster.</summary>
        private const int RangedKeepAwayDistance = 3;

        private enum AutomaticPathOwner
        {
            None,
            Pickup,
            Patrol
        }

        private readonly long[] _nextProtectionUse = new long[3];
        private readonly AssistCombatAI _combatAI = new AssistCombatAI();
        private readonly HashSet<string> _excludedItems =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _excludeListLoaded;
        private readonly Dictionary<uint, long> _pickupRetryAfter = new Dictionary<uint, long>();
        private long _nextProcessTime;
        private long _nextPickupProcess;
        private long _nextPatrolProcess;
        private long _nextRepositionProcess;
        private long _nextSummonProcess;
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
                                           !IsAutomaticSpell(GameScene.User.NextMagic.Spell)))
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
            _excludedItems.Clear();
            _pickupRetryAfter.Clear();
            _pickupTargetId = 0;
            _autoAttackTargetId = 0;
            _pathOwner = AutomaticPathOwner.None;
            _patrolMapIndex = int.MinValue;
            _patrolAnchorSet = false;
            _resetAnchorAfterManualInput = false;
            _automaticSpellPending = false;
            _combatAI.Reset();

            LoadExcludedItems();
        }

        public void ReloadItemExclusions()
        {
            _excludedItems.Clear();
            LoadExcludedItems();
        }

        private void LoadExcludedItems()
        {
            _excludeListLoaded = false;
            string path = GetExcludeFilePath();
            if (!File.Exists(path))
            {
                _excludeListLoaded = true;
                return;
            }

            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string name = ParseExcludedItem(line);
                    if (!string.IsNullOrEmpty(name))
                        _excludedItems.Add(name);
                }
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Automatic pickup exclude list load failed: {ex}");
            }

            _excludeListLoaded = true;
        }

        public void SaveItemFilters()
        {
            if (!_excludeListLoaded)
                return;

            string path = GetExcludeFilePath();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            List<string> lines = new List<string>
            {
                "# Automatic pickup exclusion list. One item name per line.",
                "# Items listed here will not be picked up automatically.",
                "# Lines beginning with # or ; are comments."
            };
            lines.AddRange(_excludedItems
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Replace("\t", " ")));

            try
            {
                File.WriteAllLines(path, lines, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Automatic pickup exclude list save failed: {ex}");
            }
        }

        public IReadOnlyList<string> GetExcludedItems()
        {
            return _excludedItems
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void SetItemExcluded(string name, bool excluded)
        {
            name = NormalizeItemName(name);
            if (string.IsNullOrEmpty(name))
                return;

            if (excluded)
                _excludedItems.Add(name);
            else
                _excludedItems.Remove(name);
        }

        public void ClearAutomaticTargets()
        {
            MapControl map = GameScene.Scene?.MapControl;
            CancelOwnedPath(map);
            _pickupTargetId = 0;

            if (_automaticSpellPending && GameScene.User?.NextMagic != null &&
                IsAutomaticSpell(GameScene.User.NextMagic.Spell))
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
                IsAutomaticSpell(GameScene.User.NextMagic.Spell))
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
                    target = null;
                }
                else
                {
                    _autoAttackTargetId = target.ObjectID;
                }

                if (target == null)
                    target = FindNearestMonster(user);

                ItemObject priorityItem = Settings.AssistAutoPickup
                    ? FindNearestItem(user, GetVisiblePickupPriorityRange())
                    : null;
                if (priorityItem != null &&
                    (target == null || Functions.MaxDistance(user.CurrentLocation, priorityItem.CurrentLocation) <
                     Functions.MaxDistance(user.CurrentLocation, target.CurrentLocation)) &&
                    user.NextMagic == null &&
                    (user.QueuedAction == null || _pathOwner != AutomaticPathOwner.None) &&
                    ProcessAutoPickup(user, map, true, GetVisiblePickupPriorityRange()))
                {
                    if (target != null)
                    {
                        if (MapObject.TargetObjectID == target.ObjectID)
                            MapObject.TargetObjectID = 0;
                        if (MapObject.MagicObjectID == target.ObjectID)
                            MapObject.MagicObjectID = 0;
                        _autoAttackTargetId = 0;
                    }

                    return;
                }

                if (target == null)
                {
                    target = FindNearestMonster(user);
                    if (target != null)
                    {
                        _autoAttackTargetId = target.ObjectID;
                        MapObject.TargetObjectID = target.ObjectID;
                        MapObject.MagicObjectID = target.ObjectID;
                    }
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

                    bool acted = false;
                    if (user.NextMagic == null && user.QueuedAction == null)
                        acted = TryUseAutoCombatSpell(user, target);

                    // Standing still in melee range is what the ranged classes must
                    // avoid. Kite only while the skill is unavailable, the same way
                    // ArcherHero steps away while its attack is on cooldown.
                    if (!acted)
                        ProcessRangedPositioning(user, map, target);

                    return;
                }
            }

            if (user.NextMagic != null)
                return;

            // A patrol step may already be queued by MapControl before this
            // controller runs. Give a newly visible pickup target priority over
            // that automatic patrol action when no legal monster is present.
            if (Settings.AssistAutoPickup && _pathOwner == AutomaticPathOwner.Patrol &&
                ProcessAutoPickup(user, map, true))
                return;

            if (user.QueuedAction != null)
                return;

            if (Settings.AssistAutoPickup && ProcessAutoPickup(user, map, Settings.AssistAutoAttack))
                return;

            // No monster in sight is the best moment to replace a missing pet.
            if (Settings.AssistAutoAttack && TryResummonPets(user))
                return;

            if (Settings.AssistAutoAttack)
                ProcessPatrol(user, map);
        }

        private bool ProcessAutoPickup(UserObject user, MapControl map, bool allowMovement,
            int maximumDistance = AutoTargetRange)
        {
            if (maximumDistance >= AutoTargetRange && CMain.Time < _nextPickupProcess)
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

            if (item == null || !item.CanPickUp || IsPickupCoolingDown(item.ObjectID) || !ShouldPickItem(item.Name) ||
                Functions.MaxDistance(user.CurrentLocation, item.CurrentLocation) > maximumDistance ||
                (!allowMovement && item.CurrentLocation != user.CurrentLocation))
            {
                if (_pathOwner == AutomaticPathOwner.Pickup)
                    CancelOwnedPath(map);

                item = allowMovement ? FindNearestItem(user, maximumDistance) : FindItemAtLocation(user);
                _pickupTargetId = item?.ObjectID ?? 0;
            }

            if (item == null)
                return false;

            if (_pathOwner == AutomaticPathOwner.Patrol)
            {
                CancelOwnedPath(map);
                // The queued movement was created by the patrol path just
                // cancelled. Do not let it run over the newly selected item.
                if (user.QueuedAction != null &&
                    (user.QueuedAction.Action == MirAction.Standing ||
                     user.QueuedAction.Action == MirAction.Walking ||
                     user.QueuedAction.Action == MirAction.Running ||
                     user.QueuedAction.Action == MirAction.MountStanding ||
                     user.QueuedAction.Action == MirAction.MountWalking ||
                     user.QueuedAction.Action == MirAction.MountRunning))
                    user.QueuedAction = null;
            }

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

            List<Node> path = map.PathFinder.FindPath(user.CurrentLocation, item.CurrentLocation, maximumDistance);
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
                if (mapObject is ItemObject item && item.CanPickUp && item.CurrentLocation == user.CurrentLocation &&
                    !IsPickupCoolingDown(item.ObjectID) && ShouldPickItem(item.Name))
                    return item;
            }

            return null;
        }

        private ItemObject FindNearestItem(UserObject user, int maximumDistance = AutoTargetRange)
        {
            ItemObject result = null;
            int nearest = int.MaxValue;
            foreach (MapObject mapObject in MapControl.Objects.Values)
            {
                if (!(mapObject is ItemObject item) || !item.CanPickUp || IsPickupCoolingDown(item.ObjectID) ||
                    !ShouldPickItem(item.Name))
                    continue;

                int distance = Functions.MaxDistance(user.CurrentLocation, item.CurrentLocation);
                if (distance > maximumDistance || distance >= nearest)
                    continue;

                nearest = distance;
                result = item;
            }

            return result;
        }

        private static int GetVisiblePickupPriorityRange()
        {
            int horizontalRange = Math.Max(1, Settings.ScreenWidth / (MapControl.CellWidth * 2));
            int verticalRange = Math.Max(1, Settings.ScreenHeight / (MapControl.CellHeight * 2));
            return Math.Min(9, Math.Min(horizontalRange, verticalRange));
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

                // The server sends objects up to Globals.DataRange (16) cells
                // away. Only monsters inside the engage radius are picked up,
                // otherwise a far away monster keeps a target selected forever
                // and the hunting modes never get a chance to patrol.
                if (distance > AutoTargetRange)
                    continue;

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

            // Past the retain radius the monster is off screen. Leaving it
            // selected keeps MapControl pursuing it, which blocks the automatic
            // path and starves the patrol used by the hunting modes.
            UserObject user = GameScene.User;
            if (user != null &&
                Functions.MaxDistance(user.CurrentLocation, monster.CurrentLocation) > AutoTargetRetainRange)
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

        public static bool IsAutoSummonSpell(Spell spell)
        {
            return Array.IndexOf(AutoSummonSpells, spell) >= 0;
        }

        /// <summary>Any skill this controller may cast on its own.</summary>
        public static bool IsAutomaticSpell(Spell spell)
        {
            return IsAutoCombatSpell(spell) || IsAutoSummonSpell(spell);
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
            // A patrol leg has to walk around walls, so the node budget needs
            // slack over the straight line distance. Allowing only range + 2
            // made almost every candidate fail on a map with obstacles.
            int maximumPathLength = Settings.AssistHuntMode == AssistSearchMode.Nearby
                ? NearbyPatrolRange * 2 + 2
                : CurrentMapPatrolSegmentRange * 2 + 2;

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

        /// <summary>
        /// Casts the next automatic combat skill. The skill pool comes from the
        /// F11 skill page: a skill without a bound key is never used. Which of
        /// the bound skills is chosen is decided by <see cref="AssistCombatAI"/>.
        /// </summary>
        internal bool TryUseAutoCombatSpell(UserObject user, MonsterObject target)
        {
            if (!CanAutoAttackTarget(target) || !CanCastAutomatically(user))
                return false;

            // A dead pet leaves the Taoist without its main damage source, so
            // replacing it takes priority over the next attack skill.
            if (TryResummonPets(user))
                return true;

            AssistCombatAI.CombatContext context = BuildCombatContext(user, target);
            ClientMagic magic = _combatAI.Select(context, candidate => CanCastCombatSpell(user, target, candidate));
            if (magic == null)
                return false;

            user.NextMagic = magic;
            user.NextMagicLocation = target.CurrentLocation;
            user.NextMagicObject = target;
            user.NextMagicDirection = Functions.DirectionFromPoint(user.CurrentLocation, target.CurrentLocation);
            _automaticSpellPending = true;
            return true;
        }

        private static AssistCombatAI.CombatContext BuildCombatContext(UserObject user, MonsterObject target)
        {
            AssistCombatAI.CombatContext context = new AssistCombatAI.CombatContext
            {
                User = user,
                Target = target,
                TargetDistance = Functions.MaxDistance(user.CurrentLocation, target.CurrentLocation)
            };

            foreach (MapObject mapObject in MapControl.Objects.Values)
            {
                if (!(mapObject is MonsterObject monster) || !IsAutoCombatTarget(monster))
                    continue;

                if (Functions.MaxDistance(target.CurrentLocation, monster.CurrentLocation) <= 1)
                    context.MonstersNextToTarget++;

                int distanceToUser = Functions.MaxDistance(user.CurrentLocation, monster.CurrentLocation);
                if (distanceToUser <= 1)
                    context.MonstersNextToUser++;
                if (distanceToUser <= 2)
                    context.MonstersNearUser++;
            }

            return context;
        }

        /// <summary>Player state that blocks every automatic cast.</summary>
        private static bool CanCastAutomatically(UserObject user)
        {
            if (user.NextMagic != null || user.QueuedAction != null || user.RidingMount || user.Fishing ||
                CMain.Time < GameScene.SpellTime || CMain.Time < user.BlizzardStopTime ||
                CMain.Time < user.ReincarnationStopTime ||
                user.Poison.HasFlag(PoisonType.Stun) || user.Poison.HasFlag(PoisonType.Paralysis) ||
                user.Poison.HasFlag(PoisonType.LRParalysis) || user.Poison.HasFlag(PoisonType.Frozen) ||
                user.Poison.HasFlag(PoisonType.Dazed))
                return false;

            return (user.HasClassWeapon || user.Weapon < 0) &&
                   (user.Class != MirClass.Archer || user.HasClassWeapon);
        }

        private static bool CanCastCombatSpell(UserObject user, MonsterObject target, ClientMagic magic)
        {
            if (magic.Spell == Spell.ElementalShot && !user.HasElements)
                return false;

            if (!HasRequiredCombatItems(user, magic.Spell) || !IsMagicReady(user, magic))
                return false;

            return Functions.InRange(user.CurrentLocation, target.CurrentLocation, GetEffectiveRange(magic));
        }

        private static bool IsMagicReady(UserObject user, ClientMagic magic)
        {
            if (magic == null || CMain.Time <= magic.CastTime + magic.Delay)
                return false;

            int cost = magic.Level * magic.LevelCost + magic.BaseCost;
            if (user.Stats[Stat.ManaPenaltyPercent] > 0)
                cost += cost * user.Stats[Stat.ManaPenaltyPercent] / 100;

            return cost <= user.MP;
        }

        /// <summary>
        /// Distance the skill is allowed to be cast from. Most skills use the
        /// range sent by the server, but a skill that only hits around the
        /// caster would be wasted from across the screen.
        /// </summary>
        private static int GetEffectiveRange(ClientMagic magic)
        {
            int range = magic.Range == 0 ? 1 : magic.Range;
            if (SelfCentredSpellRange.TryGetValue(magic.Spell, out int limit))
                range = Math.Min(range, limit);

            return range;
        }

        private static ClientMagic FindBoundMagic(UserObject user, Spell spell)
        {
            if (user.Magics == null)
                return null;

            foreach (ClientMagic magic in user.Magics)
            {
                if (magic != null && magic.Spell == spell && magic.Key != 0)
                    return magic;
            }

            return null;
        }

        /// <summary>
        /// Replaces a summoned pet that died or went missing. Only summon skills
        /// bound on the F11 skill page are used, and a summon is skipped while
        /// its pet is alive so a healthy pet is never recalled needlessly.
        /// </summary>
        private bool TryResummonPets(UserObject user)
        {
            if (CMain.Time < _nextSummonProcess || !CanCastAutomatically(user))
                return false;

            int livePets = 0;
            foreach (MapObject mapObject in MapControl.Objects.Values)
            {
                if (mapObject is MonsterObject pet && pet.MasterObjectId == user.ObjectID && !pet.Dead)
                    livePets++;
            }

            // The server refuses a third monster pet, so stop asking for one.
            if (livePets >= 2)
            {
                _nextSummonProcess = CMain.Time + 2000;
                return false;
            }

            foreach (Spell spell in AutoSummonSpells)
            {
                ClientMagic magic = FindBoundMagic(user, spell);
                if (magic == null || HasSummonedPet(user, spell) ||
                    !HasRequiredCombatItems(user, spell) || !IsMagicReady(user, magic))
                    continue;

                _nextSummonProcess = CMain.Time + 1500;

                // Summons have no target: UseMagic sends the caster location.
                user.NextMagic = magic;
                user.NextMagicLocation = user.CurrentLocation;
                user.NextMagicObject = null;
                user.NextMagicDirection = user.Direction;
                _automaticSpellPending = true;
                return true;
            }

            _nextSummonProcess = CMain.Time + 500;
            return false;
        }

        private static bool HasSummonedPet(UserObject user, Spell spell)
        {
            foreach (MapObject mapObject in MapControl.Objects.Values)
            {
                if (mapObject is MonsterObject pet && pet.MasterObjectId == user.ObjectID && !pet.Dead &&
                    IsSummonedBy(spell, pet.BaseImage))
                    return true;
            }

            return false;
        }

        private static bool IsSummonedBy(Spell spell, Monster image)
        {
            switch (spell)
            {
                case Spell.SummonSkeleton:
                    return image == Monster.BoneFamiliar;
                case Spell.SummonShinsu:
                    return image == Monster.Shinsu || image == Monster.Shinsu1;
                case Spell.SummonHolyDeva:
                    return image == Monster.HolyDeva;
                default:
                    return false;
            }
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
                case Spell.SummonSkeleton:
                    return HasAmulet(user, 1, 0);
                case Spell.SummonHolyDeva:
                    return HasAmulet(user, 2, 0);
                case Spell.SummonShinsu:
                    return HasAmulet(user, 5, 0);
                default:
                    return true;
            }
        }

        private static bool HasAmulet(UserObject user, int count, int shape)
        {
            return HasAmuletIn(user.Equipment, count, shape) ||
                   (Settings.AssistAutoPoisonAmulet && HasAmuletIn(user.Inventory, count, shape));
        }

        private static bool HasAmuletIn(IEnumerable<UserItem> items, int count, int shape)
        {
            return items.Any(item => item?.Info != null && item.Info.Type == ItemType.Amulet &&
                                     item.Info.Shape == shape && item.Count >= count);
        }

        private static bool HasPoison(UserObject user, int count, int shape = 0)
        {
            return HasPoisonIn(user.Equipment, count, shape) ||
                   (Settings.AssistAutoPoisonAmulet && HasPoisonIn(user.Inventory, count, shape));
        }

        private static bool HasPoisonIn(IEnumerable<UserItem> items, int count, int shape)
        {
            return items.Any(item => item?.Info != null && item.Info.Type == ItemType.Amulet &&
                                     item.Count >= count &&
                                     (shape == 0 ? item.Info.Shape == 1 || item.Info.Shape == 2 :
                                                   item.Info.Shape == shape));
        }

        /// <summary>
        /// True for the classes that must fight from a distance instead of
        /// closing in for melee swings. A bound skill that actually reaches
        /// further than melee is required, otherwise the player could never
        /// attack anything.
        /// </summary>
        public bool IsRangedAutoCombat(UserObject user)
        {
            if (user == null || !Settings.AssistAutoAttack)
                return false;

            if (user.Class != MirClass.Wizard && user.Class != MirClass.Taoist && user.Class != MirClass.Archer)
                return false;

            return GetRangedCastRange(user) >= RangedKeepAwayDistance;
        }

        /// <summary>
        /// Distance automatic pursuit stops at. Melee classes still walk up to
        /// the monster, ranged classes stop as soon as they can open fire.
        /// </summary>
        public int GetAutoPursuitRange(UserObject user)
        {
            if (!IsRangedAutoCombat(user))
                return 1;

            return Math.Max(RangedKeepAwayDistance, GetRangedCastRange(user) - 1);
        }

        private static int GetRangedCastRange(UserObject user)
        {
            int range = 0;

            // The bow already attacks from a distance without any skill.
            if (user.Class == MirClass.Archer && user.HasClassWeapon)
                range = Globals.MaxAttackRange;

            if (user.Magics != null)
            {
                foreach (ClientMagic magic in user.Magics)
                {
                    if (magic == null || magic.Key == 0 || !IsAutoCombatSpell(magic.Spell))
                        continue;

                    int magicRange = GetEffectiveRange(magic);
                    if (magicRange > range)
                        range = magicRange;
                }
            }

            return Math.Min(range, AutoTargetRange);
        }

        /// <summary>
        /// Steps away from the monsters that reached melee range while keeping
        /// the target inside casting range. Repositioning only runs when no
        /// skill was cast this tick, mirroring ArcherHero which kites while its
        /// attack is on cooldown.
        /// </summary>
        private void ProcessRangedPositioning(UserObject user, MapControl map, MonsterObject target)
        {
            if (!IsRangedAutoCombat(user) || user.RidingMount || user.Fishing || user.InTrapRock ||
                user.QueuedAction != null || user.NextMagic != null || map.AutoPath ||
                MapControl.MapButtons != MouseButtons.None || CMain.Time < _nextRepositionProcess ||
                user.Poison.HasFlag(PoisonType.Stun) || user.Poison.HasFlag(PoisonType.Paralysis) ||
                user.Poison.HasFlag(PoisonType.LRParalysis) || user.Poison.HasFlag(PoisonType.Frozen))
                return;

            int threatDistance = GetNearestThreatDistance(user.CurrentLocation);
            if (threatDistance >= RangedKeepAwayDistance)
                return;

            int castRange = Math.Max(RangedKeepAwayDistance, GetRangedCastRange(user));
            if (!TryFindRetreatCell(map, user.CurrentLocation, target.CurrentLocation, castRange,
                    threatDistance, out Point destination))
                return;

            _nextRepositionProcess = CMain.Time + 200;
            user.QueuedAction = new QueuedAction
            {
                Action = MirAction.Walking,
                Direction = Functions.DirectionFromPoint(user.CurrentLocation, destination),
                Location = destination
            };
        }

        private static int GetNearestThreatDistance(Point location)
        {
            int nearest = int.MaxValue;
            foreach (MapObject mapObject in MapControl.Objects.Values)
            {
                if (!(mapObject is MonsterObject monster) || !IsAutoCombatTarget(monster))
                    continue;

                int distance = Functions.MaxDistance(location, monster.CurrentLocation);
                if (distance < nearest)
                    nearest = distance;
            }

            return nearest;
        }

        /// <summary>
        /// Picks the neighbouring cell that gains the most space from the
        /// closest monster while the target stays inside casting range.
        /// </summary>
        private static bool TryFindRetreatCell(MapControl map, Point origin, Point targetLocation,
            int castRange, int currentThreat, out Point destination)
        {
            destination = origin;
            int bestThreat = 0;
            int bestTargetDistance = 0;
            bool found = false;

            for (MirDirection direction = MirDirection.Up; direction <= MirDirection.UpLeft; direction++)
            {
                Point candidate = Functions.PointMove(origin, direction, 1);
                if (!map.EmptyCell(candidate))
                    continue;

                int targetDistance = Functions.MaxDistance(candidate, targetLocation);
                if (targetDistance > castRange)
                    continue;

                int threat = GetNearestThreatDistance(candidate);
                if (threat <= currentThreat)
                    continue;

                // More space first, then the shortest way back to the target.
                if (found && (threat < bestThreat ||
                              (threat == bestThreat && targetDistance >= bestTargetDistance)))
                    continue;

                bestThreat = threat;
                bestTargetDistance = targetDistance;
                destination = candidate;
                found = true;
            }

            return found;
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

            return !_excludedItems.Contains(name);
        }

        private static string NormalizeItemName(string name)
        {
            return Regex.Replace(name ?? string.Empty, @"\s*\([\d,]+\)\s*$", string.Empty).Trim();
        }

        private static string GetExcludeFilePath()
        {
            return Path.Combine("Configs", AutoPickupExcludeFileName);
        }

        private static string ParseExcludedItem(string line)
        {
            string value = (line ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(value) || value.StartsWith("#") || value.StartsWith(";"))
                return string.Empty;

            return NormalizeItemName(value);
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
