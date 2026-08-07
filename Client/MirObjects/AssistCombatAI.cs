namespace Client.MirObjects
{
    /// <summary>
    /// Picks the skill automatic combat should cast next. The skill pool is the
    /// set of skills bound on the F11 skill page, so an unbound skill is never
    /// used. Selection mirrors the server side hero AI in
    /// Server/MirObjects/Hero: the close range classes simply rotate through
    /// their skills while the ranged classes follow a priority chain that
    /// prefers area skills on packs and single target skills on a lone monster.
    /// </summary>
    internal sealed class AssistCombatAI
    {
        /// <summary>Battlefield snapshot handed to the priority chains.</summary>
        internal struct CombatContext
        {
            public UserObject User;
            public MonsterObject Target;
            public int TargetDistance;

            /// <summary>Legal monsters within one cell of the target, the target included.</summary>
            public int MonstersNextToTarget;

            /// <summary>Legal monsters within one cell of the player.</summary>
            public int MonstersNextToUser;

            /// <summary>Legal monsters within two cells of the player.</summary>
            public int MonstersNearUser;
        }

        private enum SpellUse
        {
            /// <summary>No positional requirement.</summary>
            Any,

            /// <summary>Several monsters are standing around the target.</summary>
            TargetPack,

            /// <summary>Several monsters are standing around the player.</summary>
            SelfPack,

            /// <summary>At least one monster reached melee range of the player.</summary>
            AdjacentThreat
        }

        private sealed class SpellRule
        {
            public readonly Spell Spell;
            public readonly SpellUse Use;

            /// <summary>Minimum milliseconds between two casts, 0 casts as often as possible.</summary>
            public readonly int Reuse;

            /// <summary>The target already suffering one of these poisons skips the skill.</summary>
            public readonly PoisonType BlockedByPoison;

            /// <summary>The target already carrying this buff skips the skill.</summary>
            public readonly BuffType BlockedByBuff;

            public SpellRule(Spell spell, SpellUse use = SpellUse.Any, int reuse = 0,
                PoisonType blockedByPoison = PoisonType.None, BuffType blockedByBuff = BuffType.None)
            {
                Spell = spell;
                Use = use;
                Reuse = reuse;
                BlockedByPoison = blockedByPoison;
                BlockedByBuff = blockedByBuff;
            }
        }

        private static readonly SpellRule DefaultRule = new SpellRule(Spell.None);
        private static readonly SpellRule[] NoChain = new SpellRule[0];

        // Warriors have no chain on purpose: every bound skill is rotated through.
        private static readonly SpellRule[] WarriorChain = NoChain;

        private static readonly SpellRule[] WizardChain =
        {
            // Push the pack off first, the wizard must never trade blows.
            new SpellRule(Spell.Repulsion, SpellUse.AdjacentThreat, 1500),

            // Area skills centred on the caster.
            new SpellRule(Spell.ThunderStorm, SpellUse.SelfPack),
            new SpellRule(Spell.FlameField, SpellUse.SelfPack),

            // Area skills centred on the target.
            new SpellRule(Spell.MeteorStrike, SpellUse.TargetPack, 4000),
            new SpellRule(Spell.MeteorShower, SpellUse.TargetPack, 4000),
            new SpellRule(Spell.Blizzard, SpellUse.TargetPack, 5000),
            new SpellRule(Spell.IceStorm, SpellUse.TargetPack),
            new SpellRule(Spell.FireBang, SpellUse.TargetPack),
            new SpellRule(Spell.HellFire, SpellUse.TargetPack),
            new SpellRule(Spell.Lightning, SpellUse.TargetPack),
            new SpellRule(Spell.FireWall, SpellUse.TargetPack, 4000),

            // Single target damage, strongest first.
            new SpellRule(Spell.FlameDisruptor),
            new SpellRule(Spell.Vampirism),
            new SpellRule(Spell.ThunderBolt),
            new SpellRule(Spell.FrostCrunch),
            new SpellRule(Spell.IceThrust),
            new SpellRule(Spell.FireBounce),
            new SpellRule(Spell.GreatFireBall),
            new SpellRule(Spell.FireBall),

            // The client cannot tell an undead monster apart, so this is a last resort.
            new SpellRule(Spell.TurnUndead, SpellUse.Any, 3000)
        };

        private static readonly SpellRule[] TaoistChain =
        {
            new SpellRule(Spell.EnergyRepulsor, SpellUse.AdjacentThreat, 1500),
            new SpellRule(Spell.PoisonCloud, SpellUse.TargetPack, 4000),
            new SpellRule(Spell.TrapHexagon, SpellUse.TargetPack, 6000),
            new SpellRule(Spell.Poisoning, SpellUse.Any, 0, PoisonType.Green | PoisonType.Red),
            new SpellRule(Spell.Plague, SpellUse.TargetPack, 6000),
            new SpellRule(Spell.Curse, SpellUse.Any, 6000, PoisonType.None, BuffType.Curse),
            new SpellRule(Spell.Hallucination, SpellUse.Any, 8000),
            new SpellRule(Spell.SoulFireBall)
        };

        private static readonly SpellRule[] AssassinChain =
        {
            new SpellRule(Spell.CrescentSlash, SpellUse.SelfPack),
            new SpellRule(Spell.FireBurst, SpellUse.AdjacentThreat, 2000),
            new SpellRule(Spell.HeavenlySword),
            new SpellRule(Spell.PoisonSword, SpellUse.Any, 0, PoisonType.Green),
            new SpellRule(Spell.CatTongue),
            new SpellRule(Spell.Trap, SpellUse.Any, 8000),
            new SpellRule(Spell.MoonMist, SpellUse.Any, 10000)
        };

        private static readonly SpellRule[] ArcherChain =
        {
            new SpellRule(Spell.NapalmShot, SpellUse.TargetPack),
            new SpellRule(Spell.OneWithNature, SpellUse.SelfPack, 3000),
            new SpellRule(Spell.ExplosiveTrap, SpellUse.AdjacentThreat, 8000),
            new SpellRule(Spell.DelayedExplosion, SpellUse.TargetPack, 0, PoisonType.DelayedExplosion),
            new SpellRule(Spell.BindingShot, SpellUse.Any, 8000),
            new SpellRule(Spell.PoisonShot, SpellUse.Any, 0, PoisonType.Green),
            new SpellRule(Spell.CrippleShot, SpellUse.Any, 4000, PoisonType.Slow),
            new SpellRule(Spell.VampireShot),
            new SpellRule(Spell.ElementalShot),
            new SpellRule(Spell.DoubleShot),
            new SpellRule(Spell.StraightShot)
        };

        private readonly Dictionary<Spell, long> _nextUse = new Dictionary<Spell, long>();
        private int _rotationIndex;

        public void Reset()
        {
            _nextUse.Clear();
            _rotationIndex = 0;
        }

        /// <summary>
        /// Returns the skill to cast, or null when nothing bound is usable.
        /// <paramref name="canCast"/> performs the hard validation (cooldown,
        /// mana, range, required items) so the chain only decides intent.
        /// </summary>
        internal ClientMagic Select(in CombatContext context, Func<ClientMagic, bool> canCast)
        {
            UserObject user = context.User;
            if (user?.Magics == null || context.Target == null)
                return null;

            List<ClientMagic> bound = null;
            foreach (ClientMagic magic in user.Magics)
            {
                // An empty F11 binding excludes the skill from automatic combat.
                if (magic == null || magic.Key == 0 || !AssistController.IsAutoCombatSpell(magic.Spell))
                    continue;

                (bound ??= new List<ClientMagic>()).Add(magic);
            }

            if (bound == null)
                return null;

            SpellRule[] chain = GetChain(user.Class);
            foreach (SpellRule rule in chain)
            {
                ClientMagic magic = FindMagic(bound, rule.Spell);
                if (magic == null || !Allowed(rule, context) || !canCast(magic))
                    continue;

                Commit(rule);
                return magic;
            }

            // Anything the chain does not cover still takes part in combat. The
            // rotation is what the close range classes run on, and it lets the
            // ranged classes fall back to a skill the chain never lists.
            for (int offset = 0; offset < bound.Count; offset++)
            {
                int index = (_rotationIndex + offset) % bound.Count;
                ClientMagic magic = bound[index];
                SpellRule rule = FindRule(chain, magic.Spell);
                if (!Allowed(rule, context) || !canCast(magic))
                    continue;

                _rotationIndex = (index + 1) % bound.Count;
                Commit(rule);
                return magic;
            }

            return null;
        }

        private static SpellRule[] GetChain(MirClass playerClass)
        {
            switch (playerClass)
            {
                case MirClass.Wizard:
                    return WizardChain;
                case MirClass.Taoist:
                    return TaoistChain;
                case MirClass.Assassin:
                    return AssassinChain;
                case MirClass.Archer:
                    return ArcherChain;
                default:
                    return WarriorChain;
            }
        }

        private static ClientMagic FindMagic(List<ClientMagic> bound, Spell spell)
        {
            foreach (ClientMagic magic in bound)
            {
                if (magic.Spell == spell)
                    return magic;
            }

            return null;
        }

        private static SpellRule FindRule(SpellRule[] chain, Spell spell)
        {
            foreach (SpellRule rule in chain)
            {
                if (rule.Spell == spell)
                    return rule;
            }

            return DefaultRule;
        }

        private bool Allowed(SpellRule rule, in CombatContext context)
        {
            if (rule.Reuse > 0 && rule.Spell != Spell.None &&
                _nextUse.TryGetValue(rule.Spell, out long next) && CMain.Time < next)
                return false;

            if (rule.BlockedByPoison != PoisonType.None &&
                (context.Target.Poison & rule.BlockedByPoison) != PoisonType.None)
                return false;

            if (rule.BlockedByBuff != BuffType.None && context.Target.Buffs.Contains(rule.BlockedByBuff))
                return false;

            switch (rule.Use)
            {
                case SpellUse.TargetPack:
                    return context.MonstersNextToTarget > 1;
                case SpellUse.SelfPack:
                    return context.MonstersNearUser > 1;
                case SpellUse.AdjacentThreat:
                    return context.MonstersNextToUser > 0;
                default:
                    return true;
            }
        }

        private void Commit(SpellRule rule)
        {
            if (rule.Reuse > 0 && rule.Spell != Spell.None)
                _nextUse[rule.Spell] = CMain.Time + rule.Reuse;
        }
    }
}
