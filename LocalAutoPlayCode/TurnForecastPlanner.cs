using System.Diagnostics;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Random;

namespace LocalAutoPlay;

/// <summary>
/// Snapshots the known draw order and RNG streams on the game thread, then
/// searches a pure, bounded turn model on a worker. Never plays preview actions
/// or advances the game's real RNG. Every executed card is followed by a fresh
/// snapshot, so a forecast mismatch cannot compound into subsequent actions.
/// </summary>
internal static class TurnForecastPlanner
{
    private const int MaxDepth = 48;
    private const int ForecastTurns = 3;
    private const int FutureTurnDepth = 12;
    private const int CurrentSearchMs = 350;
    private const int TotalSearchMs = 950;
    private const int BeamWidth = 72;
    private const int MaxNodes = 30000;
    private const int FutureBeamWidth = 18;
    private const int FutureExpansions = 240;
    private static readonly Lazy<MethodInfo?> ReworkCheckMethod = new(() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "BetterDefect")?
            .GetType("BetterDefect.BdCardVersionUpgrades")?
            .GetMethod("IsVersionEnabled", BindingFlags.Static | BindingFlags.NonPublic,
                null, [typeof(CardModel)], null));
    private static readonly FieldInfo? LocalCostModifiersField =
        typeof(CardEnergyCost).GetField("_localModifiers",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private enum Generator { None, Discovery, WhiteNoise, InfernalBlade, Distraction, JackOfAllTrades, BundleOfJoy, Abundance }
    private enum Pool { CharacterAll, CharacterPower, CharacterAttack, CharacterSkill, Colorless }

    private sealed record SimCard(
        CardModel Model, string Id, string SortId, int Upgrade, CardType Type,
        TargetType TargetType, int Cost, bool CostsX, bool Exhaust,
        double Damage, double Block, int Draw, int EnergyGain,
        bool DoubleEnergy, bool OrbCard, Generator Generator,
        bool RootPlayable, (int Index, Creature? Target)[] RootTargets,
        bool BestOfThree = false,
        bool GeneratedFree = false, int ConditionalDrawLimit = 0,
        string Kind = "", int SelectCount = 0, bool Reworked = false,
        int Amount = 0, int FocusGain = 0, int WeakApply = 0,
        int VulnerableApply = 0, int StrengthGain = 0,
        int DexterityGain = 0, int BufferGain = 0, int Hits = 1,
        SimEnchant? Enchant = null, int CostWithoutFreePower = 0,
        bool VirtualReplay = false, bool CapturedFreePower = false,
        int CostAfterPlay = 0, int CostAfterTurn = 0,
        int CostAfterPlayedTurn = 0,
        bool Retain = false, bool Ethereal = false,
        bool PermanentRetain = false);

    private sealed record SimEnchant(string Kind, int Amount, bool Active,
        int ExtraReplays);

    private sealed record SimOrb(string Kind, double Passive, double Evoke);

    // Card previews already include currently active player powers and relics.
    // Keep their live values so search applies only changes within the forecast.
    private sealed record EnemyFactors(int Weak, int Vulnerable, int Intangible,
        int Buffer, int Artifact, int Thorns, double Block = 0);

    private sealed record BattleFactors(
        EnemyFactors[] Enemies, int Strength, int Dexterity, int Vigor,
        int PlayerBuffer, int PlayerIntangible,
        int PenNib = -1, int Nunchaku = -1, int Shuriken = -1,
        int Kunai = -1, int OrnamentalFan = -1, int LetterOpener = -1,
        bool VigorConsumed = false, double SelfDamage = 0,
        int FreeAttack = 0, int FreeSkill = 0, int FreePower = 0,
        int Burst = 0, int EchoForm = 0, bool Corruption = false,
        int TemporaryStrength = 0, int TemporaryDexterity = 0)
    {
        public static BattleFactors Empty(int enemies) => new(
            Enumerable.Repeat(new EnemyFactors(0, 0, 0, 0, 0, 0), enemies).ToArray(),
            0, 0, 0, 0, 0);
    }

    private sealed record Snapshot(
        SimCard[] Hand, SimCard[] Draw, SimCard[] Discard,
        SimCard[][] Pools, Rng ShuffleRng,
        Rng GenerationRng, Rng OrbRng, int Energy, int CardsPlayedThisTurn,
        double Block, double PlayerHp, double[] EnemyHp, int[] Incoming,
        SimOrb[] Orbs, int OrbCapacity, int Focus, int TempFocus, int FeralUses,
        int Heatsinks, int Loop, bool FeralAllCards = false,
        BattleFactors? Factors = null, int SeriesPlayed = 0,
        Rng? CostRng = null, int MaxEnergy = 3, int HandDraw = 5,
        int FeralMax = 0, bool ReworkedLoop = false);

    private sealed record Node(
        SimCard[] Hand, SimCard[] Draw, int DrawCursor, SimCard[] Discard,
        Rng ShuffleRng, Rng GenerationRng, Rng OrbRng,
        int Energy, int CardsPlayedThisTurn, double Block, double[] EnemyHp,
        SimOrb[] Orbs, int OrbCapacity, int Focus, int TempFocus, int FeralUses,
        int Heatsinks, int Loop, double OtherPowerValue,
        int Depth, LocalMove? First, string Forecast,
        bool FeralAllCards = false, BattleFactors? Factors = null,
        int SeriesPlayed = 0, Rng? CostRng = null,
        int TurnIndex = 0, double PlayerHp = 0,
        int[]? ProjectedIncoming = null, int FeralMax = 0,
        string FutureFirst = "");

    private readonly record struct SearchResult(LocalMove? Move, int Nodes, int Depth,
        bool Exhaustive, string Forecast);
    private readonly record struct FutureResult(double Score, string Forecast);

    public static async Task<LocalMove?> ChooseAsync(
        CombatState combat, Player player, CancellationToken token)
    {
        Snapshot? snapshot = Capture(combat, player);
        if (snapshot is null) return null;
        SearchResult result = await Task.Run(() => Search(snapshot, token), token);
        MainFile.Log.Info($"[LocalAutoPlay] PLAN nodes={result.Nodes} depth={result.Depth} " +
            $"exhaustive={result.Exhaustive} forecast={result.Forecast} " +
            $"first={result.Move?.Card.Id.Entry ?? "none"}");
        return result.Move;
    }

    private static Snapshot? Capture(CombatState combat, Player player)
    {
        PlayerCombatState? pcs = player.PlayerCombatState;
        if (pcs is null) return null;
        Creature[] enemies = combat.HittableEnemies.Where(e => e.IsAlive).ToArray();
        if (enemies.Length == 0) return null;
        SimCard[] hand = pcs.Hand.Cards.Select(c => Card(c, enemies, combat.Allies, root: true)).ToArray();
        SimCard[] draw = pcs.DrawPile.Cards.Select(c => Card(c, enemies, combat.Allies, root: false)).ToArray();
        SimCard[] discard = pcs.DiscardPile.Cards.Select(c => Card(c, enemies, combat.Allies, root: false)).ToArray();

        SimCard[][] pools = new SimCard[5][];
        if (hand.Concat(draw).Concat(discard).Any(c => c.Generator != Generator.None))
        {
            CardModel[] character = CardFactory.FilterForCombat(
                player.Character.CardPool.GetUnlockedCards(player.UnlockState,
                    player.RunState.CardMultiplayerConstraint))
                .Where(c => c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly).ToArray();
            CardModel[] colorless = CardFactory.FilterForCombat(
                ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(player.UnlockState,
                    player.RunState.CardMultiplayerConstraint))
                .Where(c => c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly).ToArray();
            pools[(int)Pool.CharacterAll] = character.Select(c => Card(c, enemies, combat.Allies, false)).ToArray();
            pools[(int)Pool.CharacterPower] = pools[(int)Pool.CharacterAll].Where(c => c.Type == CardType.Power).ToArray();
            pools[(int)Pool.CharacterAttack] = pools[(int)Pool.CharacterAll].Where(c => c.Type == CardType.Attack).ToArray();
            pools[(int)Pool.CharacterSkill] = pools[(int)Pool.CharacterAll].Where(c => c.Type == CardType.Skill).ToArray();
            pools[(int)Pool.Colorless] = colorless.Select(c => Card(c, enemies, combat.Allies, false)).ToArray();
        }
        else for (int i = 0; i < pools.Length; i++) pools[i] = [];

        int[] incoming = enemies.Select(e => IncomingFrom(e, player)).ToArray();
        return new Snapshot(hand, draw, discard, pools,
            Fork(player.RunState.Rng.Shuffle),
            Fork(player.RunState.Rng.CombatCardGeneration),
            Fork(player.RunState.Rng.CombatOrbGeneration),
            Math.Max(0, pcs.Energy),
            CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                entry.HappenedThisTurn(combat) && entry.CardPlay.Card.Owner == player),
            (double)player.Creature.Block,
            (double)player.Creature.CurrentHp,
            enemies.Select(e => (double)(e.CurrentHp + e.Block)).ToArray(), incoming,
            pcs.OrbQueue.Orbs.Select(orb => new SimOrb(orb.GetType().Name,
                (double)orb.PassiveVal, (double)orb.EvokeVal)).ToArray(),
            pcs.OrbQueue.Capacity,
            player.Creature.Powers.Where(p => p.GetType().Name == "FocusPower").Sum(p => p.Amount),
            player.Creature.Powers.OfType<TemporaryFocusPower>()
                .Sum(p => p.GetType().Name.Contains("Down", StringComparison.Ordinal)
                    ? -p.Amount : p.Amount),
            player.Creature.Powers.Where(p => p.GetType().Name == "FeralPower").Sum(p => p.DisplayAmount),
            player.Creature.Powers.Where(p => p.GetType().Name == "BdHeatsinksPower").Sum(p => p.Amount),
            player.Creature.Powers.Where(p => p.GetType().Name is "LoopPower" or "BdLoopPower").Sum(p => p.Amount),
            IsReworkedFeral(), CaptureFactors(player, enemies),
            CombatManager.Instance.History.CardPlaysStarted.Count(entry =>
                entry.Actor == player.Creature && entry.CardPlay.IsFirstInSeries &&
                entry.HappenedThisTurn(combat)),
            Fork(player.RunState.Rng.CombatEnergyCosts),
            Math.Max(0, pcs.MaxEnergy), 5,
            player.Creature.Powers.Where(p => p.GetType().Name == "FeralPower")
                .Sum(p => p.Amount), IsReworkedLoop());
    }

    private static BattleFactors CaptureFactors(Player player, Creature[] enemies)
    {
        int Power(Creature creature, string name) => creature.Powers
            .Where(p => p.GetType().Name == name).Sum(p => p.Amount);
        int Relic(string name, bool lifetimeCounter = false)
        {
            var relic = player.Relics.FirstOrDefault(r => r.GetType().Name == name);
            if (relic is null) return -1;
            if (!lifetimeCounter) return Math.Max(0, relic.DisplayAmount);
            try
            {
                object? value = relic.GetType().GetProperty("AttacksPlayed")?.GetValue(relic);
                return value is int n ? Math.Max(0, n) : Math.Max(0, relic.DisplayAmount);
            }
            catch { return Math.Max(0, relic.DisplayAmount); }
        }
        return new BattleFactors(enemies.Select(e => new EnemyFactors(
                Power(e, "WeakPower"), Power(e, "VulnerablePower"),
                Power(e, "IntangiblePower"), Power(e, "BufferPower"),
                Power(e, "ArtifactPower"), Power(e, "ThornsPower"),
                (double)e.Block)).ToArray(),
            Power(player.Creature, "StrengthPower"),
            Power(player.Creature, "DexterityPower"),
            Power(player.Creature, "VigorPower"),
            Power(player.Creature, "BufferPower"),
            Power(player.Creature, "IntangiblePower"),
            Relic("PenNib", true), Relic("Nunchaku", true),
            Relic("Shuriken"), Relic("Kunai"),
            Relic("OrnamentalFan"), Relic("LetterOpener"),
            FreeAttack: Power(player.Creature, "FreeAttackPower"),
            FreeSkill: Power(player.Creature, "FreeSkillPower"),
            FreePower: Power(player.Creature, "FreePowerPower"),
            Burst: Power(player.Creature, "BurstPower"),
            EchoForm: Power(player.Creature, "EchoFormPower"),
            Corruption: Power(player.Creature, "CorruptionPower") > 0,
            TemporaryStrength: player.Creature.Powers.OfType<TemporaryStrengthPower>()
                .Sum(p => p.GetType().Name.Contains("Down", StringComparison.Ordinal)
                    ? -p.Amount : p.Amount),
            TemporaryDexterity: player.Creature.Powers.OfType<TemporaryDexterityPower>()
                .Sum(p => p.GetType().Name.Contains("Down", StringComparison.Ordinal)
                    ? -p.Amount : p.Amount));
    }

    private static SimCard Card(CardModel card, Creature[] enemies,
        IReadOnlyList<Creature> allies, bool root)
    {
        bool playable = root && card.CanPlay();
        List<(int, Creature?)> targets = new();
        if (playable && card.TargetType == TargetType.AnyEnemy)
        {
            for (int i = 0; i < enemies.Length; i++)
                if (card.CanPlayTargeting(enemies[i])) targets.Add((i, enemies[i]));
        }
        else if (playable && card.TargetType == TargetType.AnyAlly)
        {
            foreach (Creature ally in allies)
                if (card.CanPlayTargeting(ally)) targets.Add((-1, ally));
        }
        else if (playable && card.CanPlayTargeting(null)) targets.Add((-1, null));

        string name = card.GetType().Name;
        Generator generator = name switch
        {
            "Discovery" => Generator.Discovery,
            "WhiteNoise" => Generator.WhiteNoise,
            "InfernalBlade" => Generator.InfernalBlade,
            "Distraction" => Generator.Distraction,
            "JackOfAllTrades" => Generator.JackOfAllTrades,
            "BundleOfJoy" => Generator.BundleOfJoy,
            "Abundance" => Generator.Abundance,
            _ => Generator.None
        };
        bool orb = name is "Zap" or "BallLightning" or "ColdSnap" or "Glacier"
            or "Dualcast" or "Barrage" or "MultiCast" or "Recursion"
            or "Chaos" or "Rainbow" or "Tempest" or "Fusion"
            or "MeteorStrike" or "Darkness" or "Chill" or "Glasswork"
            or "Shatter" or "Quadcast";
        double repeat = Math.Clamp(Amount(card, "Repeat", 1), 1, 8);
        EnchantmentModel? enchantment = card.Enchantment;
        SimEnchant? enchant = enchantment is null ? null : new SimEnchant(
            enchantment.GetType().Name, enchantment.Amount,
            enchantment.Status == EnchantmentStatus.Normal,
            Math.Clamp(card.GetEnchantedReplayCount(), 0, 4));
        return new SimCard(card, card.Id.Entry, card.Id.ToString(), card.CurrentUpgradeLevel,
            card.Type, card.TargetType,
            card.EnergyCost.CostsX ? 0 : Math.Max(0, card.EnergyCost.GetAmountToSpend()),
            card.EnergyCost.CostsX,
            card.Keywords.Contains(CardKeyword.Exhaust),
            Math.Max(Amount(card, "Damage"), Amount(card, "OstyDamage")) * repeat,
            Amount(card, "Block"), (int)Math.Clamp(Amount(card, "Cards"), 0, 10),
            (int)Math.Clamp(name switch
            {
                // These vars describe a delayed/conditional effect, not energy
                // available to pay for the next card in this turn.
                "ChargeBattery" or "Automation" or "Convergence"
                    or "EnergySurge" or "Scavenge" or "Sunder" => 0,
                _ => Amount(card, "Energy")
            }, 0, 9),
            name == "DoubleEnergy", orb, generator, playable, targets.ToArray(),
            name == "WhiteNoise" && IsBetterDefectRework(card),
            ConditionalDrawLimit: name switch
            {
                "Ftl" => (int)Amount(card, "PlayMax", card.IsUpgraded ? 4 : 3),
                "Supercritical" => (int)Amount(card, "PlayMax", 4),
                _ => 0
            },
            Kind: name,
            SelectCount: name switch
            {
                "BdSeek" => (int)Amount(card, "Amount", card.IsUpgraded ? 2 : 1),
                "NeowsFury" => (int)Amount(card, "Cards", card.IsUpgraded ? 3 : 2),
                "Hologram" => 1,
                _ => 0
            },
            Reworked: name is "Chaos" or "MultiCast" or "Rainbow" or "Tempest"
                or "Loop" or "Feral" or "Barrage" or "Coolheaded"
                or "DoubleEnergy" or "ColdSnap" or "FocusedStrike"
                or "BdRecursion" or "Skim" or "Sunder" or "Stack"
                or "AllForOne" or "HelixDrill" or "BdReinforcedBody"
                or "MeteorStrike"
                ? IsBetterDefectRework(card) : false,
            Amount: (int)(name switch
            {
                "Chaos" or "Capacitor" or "Quadcast" => Amount(card, "Repeat", 1),
                "Feral" => Amount(card, "FeralPower", 1),
                "Loop" => Amount(card, "Loop", 1),
                "BdHeatsinks" => Amount(card, "Draw", 1),
                "Burst" => Amount(card, "Skills", 1),
                "EchoForm" => Amount(card, "EchoForm", 1),
                _ => Amount(card, "Amount", 1)
            }),
            FocusGain: (int)Amount(card, "FocusPower"),
            WeakApply: (int)Amount(card, "WeakPower"),
            VulnerableApply: (int)Amount(card, "VulnerablePower"),
            StrengthGain: (int)Amount(card, "StrengthPower"),
            DexterityGain: (int)Amount(card, "DexterityPower"),
            BufferGain: (int)Amount(card, "BufferPower"),
            Hits: (int)repeat, Enchant: enchant,
            CostWithoutFreePower: card.EnergyCost.CostsX ? 0 : Math.Max(0,
                card.EnergyCost.GetWithModifiers(CostModifiers.Local)),
            CapturedFreePower: !card.IsCanonical && card.Owner.Creature.Powers.Any(p =>
                p.Amount > 0 && p.GetType().Name == (card.Type switch
                {
                    CardType.Attack => "FreeAttackPower",
                    CardType.Skill => "FreeSkillPower",
                    CardType.Power => "FreePowerPower",
                    _ => ""
                })),
            CostAfterPlay: CostAfterPlaying(card),
            CostAfterTurn: CostAfterTurn(card),
            CostAfterPlayedTurn: CostAfterPlayedTurn(card),
            Retain: card.ShouldRetainThisTurn,
            Ethereal: card.Keywords.Contains(CardKeyword.Ethereal),
            PermanentRetain: card.Keywords.Contains(CardKeyword.Retain));
    }

    private static int CostAfterPlaying(CardModel card)
    {
        if (card.EnergyCost.CostsX) return 0;
        int cost = card.EnergyCost.GetWithModifiers(CostModifiers.None);
        if (LocalCostModifiersField?.GetValue(card.EnergyCost)
            is not IEnumerable<LocalCostModifier> modifiers)
            return Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.Local));
        foreach (LocalCostModifier modifier in modifiers)
            if (!modifier.Expiration.HasFlag(LocalCostModifierExpiration.WhenPlayed))
                cost = modifier.Modify(cost);
        return Math.Max(0, cost);
    }

    private static int CostAfterTurn(CardModel card)
    {
        if (card.EnergyCost.CostsX) return 0;
        int cost = card.EnergyCost.GetWithModifiers(CostModifiers.None);
        if (LocalCostModifiersField?.GetValue(card.EnergyCost)
            is not IEnumerable<LocalCostModifier> modifiers)
            return Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.Local));
        foreach (LocalCostModifier modifier in modifiers)
            if (!modifier.Expiration.HasFlag(LocalCostModifierExpiration.EndOfTurn))
                cost = modifier.Modify(cost);
        return Math.Max(0, cost);
    }

    private static int CostAfterPlayedTurn(CardModel card)
    {
        if (card.EnergyCost.CostsX) return 0;
        int cost = card.EnergyCost.GetWithModifiers(CostModifiers.None);
        if (LocalCostModifiersField?.GetValue(card.EnergyCost)
            is not IEnumerable<LocalCostModifier> modifiers)
            return Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.Local));
        foreach (LocalCostModifier modifier in modifiers)
            if (!modifier.Expiration.HasFlag(LocalCostModifierExpiration.WhenPlayed) &&
                !modifier.Expiration.HasFlag(LocalCostModifierExpiration.EndOfTurn))
                cost = modifier.Modify(cost);
        return Math.Max(0, cost);
    }

    private static bool IsReworkedLoop()
    {
        try { return IsBetterDefectRework(ModelDb.Card<Loop>()); }
        catch { return false; }
    }

    private static SearchResult Search(Snapshot snapshot, CancellationToken token)
    {
        List<Node> frontier = [new Node(snapshot.Hand, snapshot.Draw, 0,
            snapshot.Discard, snapshot.ShuffleRng, snapshot.GenerationRng, snapshot.OrbRng,
            snapshot.Energy, snapshot.CardsPlayedThisTurn,
            snapshot.Block, snapshot.EnemyHp, snapshot.Orbs, snapshot.OrbCapacity,
            snapshot.Focus, snapshot.TempFocus, snapshot.FeralUses,
            snapshot.Heatsinks, snapshot.Loop,
            0, 0, null, "", snapshot.FeralAllCards,
            snapshot.Factors ?? BattleFactors.Empty(snapshot.EnemyHp.Length),
            snapshot.SeriesPlayed, snapshot.CostRng,
            PlayerHp: snapshot.PlayerHp, FeralMax: snapshot.FeralMax)];
        Node best = frontier[0]; // Ending the turn is always a legal candidate.
        double bestScore = EvaluateEnd(best, snapshot);
        Stopwatch watch = Stopwatch.StartNew();
        int expanded = 0;
        int deepest = 0;
        bool cutoff = false;
        Dictionary<string, double> visited = new(StringComparer.Ordinal);
        Dictionary<string, List<Node>> boundary = new(StringComparer.Ordinal);
        void KeepBoundary(Node candidate)
        {
            string id = candidate.First is { } move
                ? $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(move.Card)}/" +
                  $"{(move.Target is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(move.Target))}"
                : "end-now";
            if (!boundary.TryGetValue(id, out List<Node>? choices))
                boundary[id] = choices = new List<Node>(2);
            choices.Add(candidate);
            choices.Sort((a, b) => EvaluateEnd(b, snapshot).CompareTo(EvaluateEnd(a, snapshot)));
            if (choices.Count > 2) choices.RemoveAt(2);
        }
        KeepBoundary(best);
        for (int depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            List<Node> next = new();
            foreach (Node node in frontier)
            {
                for (int handIndex = 0; handIndex < node.Hand.Length; handIndex++)
                {
                    SimCard card = node.Hand[handIndex];
                    if (card.Type is CardType.Status or CardType.Curse) continue;
                    int cost = EffectiveCost(card, node);
                    if (cost > node.Energy) continue;
                    foreach ((int targetIndex, Creature? target) in Targets(card,
                        node.EnemyHp, node.TurnIndex == 0 && node.Depth == 0))
                    {
                        if ((expanded & 63) == 0)
                        {
                            token.ThrowIfCancellationRequested();
                            if (expanded >= MaxNodes || watch.ElapsedMilliseconds >= CurrentSearchMs)
                            { cutoff = true; break; }
                        }
                        Node? child = Apply(node, card, handIndex, targetIndex, target,
                            snapshot);
                        if (child is null) continue;
                        expanded++;
                        deepest = Math.Max(deepest, child.Depth);
                        double terminal = EvaluateEnd(child, snapshot);
                        KeepBoundary(child);
                        if (terminal > bestScore + 0.001)
                        {
                            best = child;
                            bestScore = terminal;
                        }
                        if (child.EnemyHp.Any(h => h > 0))
                        {
                            string key = StateKey(child);
                            double rank = BeamScore(child, snapshot);
                            if (!visited.TryGetValue(key, out double previous)
                                || rank > previous + 0.001)
                            {
                                visited[key] = rank;
                                next.Add(child);
                            }
                        }
                    }
                    if (cutoff) break;
                }
                if (cutoff) break;
            }
            if (next.Count > BeamWidth) cutoff = true;
            frontier = next.OrderByDescending(n => BeamScore(n, snapshot))
                .Take(BeamWidth).ToList();
            if (cutoff && (expanded >= MaxNodes || watch.ElapsedMilliseconds >= CurrentSearchMs)) break;
        }
        if (deepest >= MaxDepth) cutoff = true;
        string bestTrace = best.Forecast;
        if (bestScore < 10000)
        {
            double bestForecast = double.NegativeInfinity;
            foreach (Node candidate in boundary.Values.SelectMany(c => c)
                .DistinctBy(StateKey)
                .OrderByDescending(c => EvaluateEnd(c, snapshot)).Take(18))
            {
                if (watch.ElapsedMilliseconds >= TotalSearchMs) { cutoff = true; break; }
                FutureResult forecast = ForecastThreeTurns(candidate, snapshot, watch, token);
                if (forecast.Score <= bestForecast + 0.001) continue;
                best = candidate;
                bestForecast = forecast.Score;
                bestScore = forecast.Score;
                bestTrace = forecast.Forecast;
            }
        }
        return new SearchResult(best.First is { } first ? first with { Score = bestScore } : null,
            expanded, deepest, !cutoff, bestTrace);
    }

    private static FutureResult ForecastThreeTurns(Node firstTurn, Snapshot snapshot,
        Stopwatch watch, CancellationToken token)
    {
        double firstScore = EvaluateEnd(firstTurn, snapshot);
        if (firstScore >= 10000 || firstScore <= -10000)
            return new FutureResult(firstScore, firstTurn.Forecast);
        Node? secondStart = AdvanceTurn(firstTurn, snapshot);
        if (secondStart is null)
            return new FutureResult(firstScore, firstTurn.Forecast);
        double best = double.NegativeInfinity;
        string bestTrace = firstTurn.Forecast;
        foreach (Node second in SearchFutureTurn(secondStart, snapshot, watch, token))
        {
            double secondScore = EvaluateEnd(second, snapshot);
            if (secondScore >= 10000 || secondScore <= -10000)
            {
                double value = 0.4 * firstScore + 0.6 * secondScore;
                if (value > best) { best = value; bestTrace = second.Forecast; }
                continue;
            }
            Node? thirdStart = AdvanceTurn(second, snapshot);
            if (thirdStart is null)
            {
                double value = 0.4 * firstScore + 0.6 * secondScore;
                if (value > best) { best = value; bestTrace = second.Forecast; }
                continue;
            }
            Node third = SearchFutureTurn(thirdStart, snapshot, watch, token)
                .OrderByDescending(n => EvaluateEnd(n, snapshot)).First();
            double thirdScore = EvaluateEnd(third, snapshot);
            double total = 0.25 * firstScore + 0.3 * secondScore +
                0.45 * thirdScore;
            if (total > best) { best = total; bestTrace = third.Forecast; }
            if (watch.ElapsedMilliseconds >= TotalSearchMs) break;
        }
        return double.IsNegativeInfinity(best)
            ? new FutureResult(firstScore, firstTurn.Forecast)
            : new FutureResult(best, bestTrace);
    }

    private static IReadOnlyList<Node> SearchFutureTurn(Node start, Snapshot snapshot,
        Stopwatch watch, CancellationToken token)
    {
        List<Node> candidates = [start];
        List<Node> frontier = [start];
        Dictionary<string, double> visited = new(StringComparer.Ordinal);
        int expanded = 0;
        for (int depth = 0; depth < FutureTurnDepth && frontier.Count > 0 &&
            expanded < FutureExpansions && watch.ElapsedMilliseconds < TotalSearchMs; depth++)
        {
            List<Node> next = new();
            foreach (Node node in frontier)
            {
                foreach (int handIndex in Enumerable.Range(0, node.Hand.Length))
                {
                    SimCard card = node.Hand[handIndex];
                    if (card.Type is CardType.Status or CardType.Curse ||
                        EffectiveCost(card, node) > node.Energy) continue;
                    foreach ((int targetIndex, Creature? target) in
                        Targets(card, node.EnemyHp, firstAction: false))
                    {
                        if ((expanded & 31) == 0)
                        {
                            token.ThrowIfCancellationRequested();
                            if (watch.ElapsedMilliseconds >= TotalSearchMs) break;
                        }
                        Node? child = Apply(node, card, handIndex, targetIndex,
                            target, snapshot);
                        if (child is null) continue;
                        expanded++;
                        candidates.Add(child);
                        if (child.EnemyHp.Any(h => h > 0))
                        {
                            string key = StateKey(child);
                            double score = BeamScore(child, snapshot);
                            if (!visited.TryGetValue(key, out double old) ||
                                score > old + 0.001)
                            {
                                visited[key] = score;
                                next.Add(child);
                            }
                        }
                        if (expanded >= FutureExpansions) break;
                    }
                    if (expanded >= FutureExpansions ||
                        watch.ElapsedMilliseconds >= TotalSearchMs) break;
                }
                if (expanded >= FutureExpansions ||
                    watch.ElapsedMilliseconds >= TotalSearchMs) break;
            }
            frontier = next.OrderByDescending(n => BeamScore(n, snapshot))
                .Take(FutureBeamWidth).ToList();
        }
        Node[] distinct = candidates.DistinctBy(StateKey).ToArray();
        Node[] options = distinct
            .Where(n => n.FutureFirst.Length > 0)
            .GroupBy(n => n.FutureFirst, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(n => EvaluateEnd(n, snapshot)).First())
            .OrderByDescending(n => EvaluateEnd(n, snapshot)).Take(3).ToArray();
        return [start, .. options];
    }

    private static Node? AdvanceTurn(Node node, Snapshot snapshot)
    {
        if (node.TurnIndex >= ForecastTurns - 1) return null;
        double[] hp = (double[])node.EnemyHp.Clone();
        double block = node.Block;
        int energy = node.Energy;
        List<SimOrb> orbs = node.Orbs.ToList();
        BattleFactors factors = node.Factors is { } live
            ? live with { Enemies = (EnemyFactors[])live.Enemies.Clone() }
            : BattleFactors.Empty(hp.Length);
        for (int i = 0; i < orbs.Count; i++)
            if (orbs[i].Kind != "PlasmaOrb")
                TriggerPassive(orbs, i, hp, ref block, ref energy, factors);
        if (hp.All(h => h <= 0)) return null;

        int[] incoming = node.ProjectedIncoming ?? snapshot.Incoming;
        BattleFactors initial = snapshot.Factors ?? BattleFactors.Empty(hp.Length);
        double attackTotal = 0;
        double largestAttack = 0;
        for (int i = 0; i < hp.Length; i++)
        {
            if (hp[i] <= 0) continue;
            double attack = incoming[i];
            if (factors.Enemies[i].Weak > 0 && initial.Enemies[i].Weak == 0)
                attack *= 0.75;
            attackTotal += attack;
            largestAttack = Math.Max(largestAttack, attack);
        }
        double loss = Math.Max(0, attackTotal + factors.SelfDamage - block);
        if (factors.PlayerIntangible > 0)
            loss = Math.Min(loss, Math.Max(1, hp.Count(h => h > 0) * 2));
        int bufferUsed = factors.PlayerBuffer > 0 && loss > 0 ? 1 : 0;
        if (bufferUsed > 0) loss = Math.Max(0, loss - largestAttack);
        double remainingHp = (node.PlayerHp > 0 ? node.PlayerHp : snapshot.PlayerHp) - loss;
        if (remainingHp <= 0) return null;

        // Clear enemy block when their next turn starts; status durations and
        // enemy actions beyond the visible intent remain approximate.
        for (int i = 0; i < hp.Length; i++)
        {
            EnemyFactors enemy = factors.Enemies[i];
            hp[i] = Math.Max(0, hp[i] - enemy.Block);
            factors.Enemies[i] = enemy with
            {
                Block = 0,
                Weak = Math.Max(0, enemy.Weak - 1),
                Vulnerable = Math.Max(0, enemy.Vulnerable - 1),
                Intangible = Math.Max(0, enemy.Intangible - 1)
            };
        }
        factors = factors with
        {
            Strength = factors.Strength - factors.TemporaryStrength,
            Dexterity = factors.Dexterity - factors.TemporaryDexterity,
            TemporaryStrength = 0,
            TemporaryDexterity = 0,
            PlayerBuffer = Math.Max(0, factors.PlayerBuffer - bufferUsed),
            PlayerIntangible = Math.Max(0, factors.PlayerIntangible - 1),
            Burst = 0,
            VigorConsumed = false,
            SelfDamage = 0
        };
        if (node.TempFocus != 0)
            for (int i = 0; i < orbs.Count; i++)
            {
                SimOrb orb = orbs[i];
                orbs[i] = orb with
                {
                    Passive = Math.Max(0, orb.Passive - node.TempFocus),
                    Evoke = orb.Kind is "DarkOrb" or "PlasmaOrb" ? orb.Evoke
                        : Math.Max(0, orb.Evoke -
                            (orb.Kind == "GlassOrb" ? 2 : 1) * node.TempFocus)
                };
            }

        SimCard Cleanup(SimCard card) => card with
        {
            Cost = card.CostAfterTurn,
            CostWithoutFreePower = card.CostAfterTurn,
            CapturedFreePower = false,
            GeneratedFree = false,
            Retain = card.PermanentRetain
        };
        List<SimCard> discard = node.Discard.Select(Cleanup).ToList();
        List<SimCard> hand = new();
        foreach (SimCard card in node.Hand)
        {
            if (card.Ethereal) continue;
            SimCard cleaned = Cleanup(card);
            if (card.Retain) hand.Add(cleaned);
            else discard.Add(cleaned);
        }
        SimCard[] draw = node.Draw.Select(Cleanup).ToArray();
        int cursor = node.DrawCursor;
        Rng shuffle = node.ShuffleRng;
        Rng costRng = node.CostRng ?? snapshot.CostRng ?? Fork(node.ShuffleRng);
        string forecast = node.Forecast.Length < 2048
            ? node.Forecast + $" turn:{node.TurnIndex + 2}" : node.Forecast;
        DrawCards(snapshot.HandDraw, hand, ref draw, ref cursor, discard,
            ref shuffle, ref costRng, ref forecast);
        energy = snapshot.MaxEnergy;
        block = 0;
        for (int repeat = 0; repeat < node.Loop && orbs.Count > 0; repeat++)
        {
            TriggerPassive(orbs, 0, hp, ref block, ref energy, factors);
            if (snapshot.ReworkedLoop)
                TriggerPassive(orbs, orbs.Count - 1, hp, ref block, ref energy, factors);
        }
        foreach (SimOrb orb in orbs)
            if (orb.Kind == "PlasmaOrb") energy += (int)orb.Passive;
        // Unknown future intents: use a capped risk estimate derived from the
        // currently visible attack, never pretend to know the monster's AI/RNG.
        int[] nextIncoming = snapshot.Incoming.Select((value, i) => hp[i] <= 0
            ? 0 : Math.Clamp((int)Math.Round(value * 0.55 + 4), 4, 35)).ToArray();
        return node with
        {
            Hand = hand.ToArray(), Draw = draw, DrawCursor = cursor,
            Discard = discard.ToArray(), ShuffleRng = shuffle, CostRng = costRng,
            Energy = energy, CardsPlayedThisTurn = 0,
            SeriesPlayed = 0, Block = block, EnemyHp = hp,
            Orbs = orbs.ToArray(), Focus = node.Focus - node.TempFocus,
            TempFocus = 0, FeralUses = node.FeralMax,
            Factors = factors, PlayerHp = remainingHp,
            TurnIndex = node.TurnIndex + 1, ProjectedIncoming = nextIncoming,
            Forecast = forecast, FutureFirst = ""
        };
    }

    private static IEnumerable<(int Index, Creature? Target)> Targets(
        SimCard card, double[] hp, bool firstAction)
    {
        if (firstAction) return card.RootTargets;
        if (card.TargetType == TargetType.AnyEnemy)
            return Enumerable.Range(0, hp.Length).Where(i => hp[i] > 0).Select(i => (i, (Creature?)null));
        if (card.TargetType == TargetType.AnyAlly) return [];
        return [(-1, null)];
    }

    private static int EffectiveCost(SimCard card, Node node)
    {
        if (card.VirtualReplay) return 0;
        if (card.CostsX) return node.Energy;
        BattleFactors factors = node.Factors ?? BattleFactors.Empty(node.EnemyHp.Length);
        if (card.Type == CardType.Skill && factors.Corruption) return 0;
        int free = card.Type switch
        {
            CardType.Attack => factors.FreeAttack,
            CardType.Skill => factors.FreeSkill,
            CardType.Power => factors.FreePower,
            _ => 0
        };
        if (free > 0 || card.GeneratedFree) return 0;
        return card.CapturedFreePower ? card.CostWithoutFreePower : card.Cost;
    }

    private static Node? Apply(Node node, SimCard card, int handIndex,
        int targetIndex, Creature? target, Snapshot snapshot)
    {
        if (node.Depth >= MaxDepth) return null;
        if (node.EnemyHp.All(h => h <= 0)) return null;
        int cost = EffectiveCost(card, node);
        if (cost > node.Energy) return null;

        int energy = node.Energy - cost;
        int xMultiplier = card.CostsX ? Math.Max(0, cost) : 1;
        if (card.Reworked && xMultiplier >= 4
            && card.Kind is "HelixDrill" or "BdReinforcedBody")
            xMultiplier *= 2;
        BattleFactors factors = node.Factors ?? BattleFactors.Empty(node.EnemyHp.Length);
        BattleFactors initialFactors = snapshot.Factors ?? BattleFactors.Empty(node.EnemyHp.Length);
        factors = factors with { Enemies = (EnemyFactors[])factors.Enemies.Clone() };
        SimEnchant? enchant = card.Enchant;
        int replays = card.VirtualReplay ? 0 : Math.Min(4,
            (enchant?.ExtraReplays ?? 0)
            + (card.Type == CardType.Skill && factors.Burst > 0 ? 1 : 0)
            + (node.SeriesPlayed < factors.EchoForm ? 1 : 0));
        if (card.CostsX) replays = 0; // X-value replay needs native captured X.
        double block = node.Block + (card.Block > 0 ? Math.Max(0,
            card.Block + factors.Dexterity - initialFactors.Dexterity) * xMultiplier : 0);
        if (enchant is { Active: true, Kind: "Adroit" })
            block += Math.Max(0, enchant.Amount);
        if (card.Kind == "Stack" && !card.Reworked)
            block += node.Discard.Length + (card.Upgrade > 0 ? 3 : 0);
        double[] hp = (double[])node.EnemyHp.Clone();
        List<SimOrb> orbs = node.Orbs.ToList();
        int capacity = node.OrbCapacity;
        int focusDelta = card.Kind == "Hyperbeam" ? -card.FocusGain
            : card.Kind == "Barrage" && card.Reworked ? (int)card.Damage
            : card.FocusGain;
        int focus = node.Focus + focusDelta;
        int tempFocus = node.TempFocus + (card.Kind is "FocusedStrike" or "Hyperbeam" or "Hotfix"
            || card.Kind == "Barrage" && card.Reworked ? focusDelta : 0);
        int feral = node.FeralUses;
        int heatsinks = node.Heatsinks;
        int loop = node.Loop;
        double otherPower = node.OtherPowerValue;
        List<SimCard> hand = node.Hand.ToList();
        hand.RemoveAt(handIndex);
        SimCard[] draw = node.Draw;
        int drawCursor = node.DrawCursor;
        List<SimCard> discard = node.Discard.ToList();
        Rng shuffleRng = node.ShuffleRng;
        Rng generationRng = node.GenerationRng;
        Rng orbRng = node.OrbRng;
        Rng costRng = node.CostRng ?? snapshot.CostRng ?? Fork(node.ShuffleRng);
        string forecast = node.Forecast.Length < 2048
            ? node.Forecast + $" play:{card.Id}" : node.Forecast;
        List<CardModel> selection = new();

        // Damage and block are taken from the game's current preview. The
        // remaining handlers cover effects that change what can be played next.
        int hits = Math.Max(1, card.Hits * xMultiplier);
        if (card.Kind == "Barrage" && !card.Reworked)
            hits *= orbs.Count;
        double damage = card.Damage * xMultiplier
            * (card.Kind == "Barrage" && !card.Reworked ? orbs.Count : 1);
        if (damage > 0 && !(card.Kind == "Barrage" && card.Reworked))
        {
            if (card.TargetType == TargetType.AllEnemies)
                for (int i = 0; i < hp.Length; i++)
                    factors = HitAttack(hp, i, damage, hits, factors, initialFactors);
            else if (targetIndex >= 0)
                factors = HitAttack(hp, targetIndex, damage, hits,
                    factors, initialFactors);
            else if (card.Type == CardType.Attack)
            {
                int i = Array.FindIndex(hp, h => h > 0);
                if (i >= 0) factors = HitAttack(hp, i, damage * 0.65,
                    hits, factors, initialFactors);
            }
        }
        // Debuffs are applied after the card's damage. Artifact blocks the
        // first incoming debuff; extra stacks extend duration, not strength.
        if (targetIndex >= 0 && targetIndex < factors.Enemies.Length)
        {
            EnemyFactors enemy = factors.Enemies[targetIndex];
            bool appliesWeak = card.WeakApply > 0 &&
                (card.Kind != "GoForTheEyes" || card.Reworked ||
                    (node.ProjectedIncoming ?? snapshot.Incoming)[targetIndex] > 0);
            if (appliesWeak)
            {
                if (enemy.Artifact > 0) enemy = enemy with { Artifact = enemy.Artifact - 1 };
                else enemy = enemy with { Weak = Math.Max(enemy.Weak, card.WeakApply) };
            }
            if (card.VulnerableApply > 0)
            {
                if (enemy.Artifact > 0) enemy = enemy with { Artifact = enemy.Artifact - 1 };
                else enemy = enemy with { Vulnerable = Math.Max(enemy.Vulnerable,
                    card.VulnerableApply) };
            }
            factors.Enemies[targetIndex] = enemy;
        }
        if (enchant is { Active: true, Kind: "Inky" })
        {
            IEnumerable<int> targets = card.TargetType == TargetType.AllEnemies
                ? Enumerable.Range(0, hp.Length)
                : targetIndex >= 0 ? [targetIndex] : [];
            foreach (int i in targets)
            {
                EnemyFactors enemy = factors.Enemies[i];
                factors.Enemies[i] = enemy.Artifact > 0
                    ? enemy with { Artifact = enemy.Artifact - 1 }
                    : enemy with { Weak = Math.Max(enemy.Weak, 1) };
            }
        }
        if (enchant is { Active: true, Kind: "Corrupted" })
            factors = factors with { SelfDamage = factors.SelfDamage + 2 };
        bool sunderKill = card.Kind == "Sunder" && targetIndex >= 0
            && hp[targetIndex] <= 0;

        // Temporary Focus remains active through this turn's orb passives but
        // must not inflate the projected next-turn board value.
        if (focusDelta != 0)
        {
            for (int i = 0; i < orbs.Count; i++)
            {
                SimOrb orb = orbs[i];
                orbs[i] = orb with
                {
                    Passive = Math.Max(0, orb.Passive + focusDelta),
                    Evoke = orb.Kind == "DarkOrb" || orb.Kind == "PlasmaOrb"
                        ? orb.Evoke : Math.Max(0, orb.Evoke +
                            (orb.Kind == "GlassOrb" ? 2 : 1) * focusDelta)
                };
            }
        }

        switch (card.Kind)
        {
            case "Barrage" when card.Reworked:
                for (int i = 0; i < orbs.Count; i++)
                    TriggerPassive(orbs, i, hp, ref block, ref energy, factors);
                break;
            case "BallLightning":
            case "Zap":
            case "BdElectrodynamics":
                for (int i = 0; i < (card.Kind == "BdElectrodynamics"
                    ? Math.Max(1, card.Amount) : 1); i++)
                    Channel(orbs, ref capacity, "LightningOrb", focus, hp,
                        ref block, ref energy, factors);
                break;
            case "ColdSnap":
                for (int i = 0; i < (card.Reworked ? 2 : 1); i++)
                    Channel(orbs, ref capacity, "FrostOrb", focus, hp,
                        ref block, ref energy, factors);
                break;
            case "Coolheaded":
                Channel(orbs, ref capacity, "FrostOrb", focus, hp,
                    ref block, ref energy, factors);
                break;
            case "Glacier":
                for (int i = 0; i < 2; i++)
                    Channel(orbs, ref capacity, "FrostOrb", focus, hp,
                        ref block, ref energy, factors);
                break;
            case "Chill":
                int chillCount = hp.Count(h => h > 0);
                for (int i = 0; i < chillCount; i++)
                    Channel(orbs, ref capacity, "FrostOrb", focus, hp,
                        ref block, ref energy, factors);
                break;
            case "Fusion":
                Channel(orbs, ref capacity, "PlasmaOrb", focus, hp,
                    ref block, ref energy, factors);
                break;
            case "MeteorStrike":
                for (int i = 0; i < (card.Reworked ? 2 : 3); i++)
                    Channel(orbs, ref capacity, "PlasmaOrb", focus, hp,
                        ref block, ref energy, factors);
                break;
            case "Glasswork":
                Channel(orbs, ref capacity, "GlassOrb", focus, hp,
                    ref block, ref energy, factors);
                break;
            case "Darkness":
                Channel(orbs, ref capacity, "DarkOrb", focus, hp,
                    ref block, ref energy, factors);
                for (int i = 0; i < orbs.Count; i++)
                    if (orbs[i].Kind == "DarkOrb")
                        for (int n = 0; n < (card.Upgrade > 0 ? 2 : 1); n++)
                            TriggerPassive(orbs, i, hp, ref block, ref energy, factors);
                break;
            case "BdDoomAndGloom":
            case "ConsumingShadow":
                Channel(orbs, ref capacity, "DarkOrb", focus, hp,
                    ref block, ref energy, factors);
                break;
            case "Chaos":
                for (int i = 0; i < (card.Reworked ? 2
                    : Math.Max(1, card.Amount)); i++)
                {
                    Rng rng = Fork(orbRng);
                    string[] kinds = ["LightningOrb", "FrostOrb", "DarkOrb",
                        "PlasmaOrb", "GlassOrb"];
                    string kind;
                    if (card.Reworked)
                    {
                        List<string> missing = kinds
                            .Where(k => orbs.All(o => o.Kind != k)).ToList();
                        List<string> pool = missing.Count > 0 ? missing : kinds.ToList();
                        pool.Sort(StringComparer.Ordinal);
                        rng.Shuffle(pool);
                        kind = pool[0];
                    }
                    else kind = rng.NextInt(5) switch
                    {
                        0 => "LightningOrb", 1 => "FrostOrb",
                        2 => "DarkOrb", 3 => "PlasmaOrb", _ => "GlassOrb"
                    };
                    orbRng = rng;
                    Channel(orbs, ref capacity, kind, focus, hp,
                        ref block, ref energy, factors);
                }
                break;
            case "Rainbow":
                foreach (string kind in card.Reworked
                    ? new[] { "LightningOrb", "FrostOrb", "GlassOrb",
                        "DarkOrb", "PlasmaOrb" }
                    : new[] { "LightningOrb", "FrostOrb", "DarkOrb" })
                    Channel(orbs, ref capacity, kind, focus, hp,
                        ref block, ref energy, factors);
                break;
            case "Tempest":
                int tempestDraw = 0;
                for (int i = 0; i < Math.Min(12, cost + (card.Upgrade > 0 ? 1 : 0)); i++)
                {
                    if (card.Reworked && orbs.Count >= capacity
                        && orbs.Count > 0 && orbs[0].Kind == "LightningOrb")
                        tempestDraw++;
                    Channel(orbs, ref capacity, "LightningOrb", focus, hp,
                        ref block, ref energy, factors);
                }
                if (tempestDraw > 0)
                    DrawCards(tempestDraw, hand, ref draw, ref drawCursor, discard,
                        ref shuffleRng, ref costRng, ref forecast);
                break;
            case "Capacitor":
            case "BdExpand":
                capacity = Math.Min(10, capacity + Math.Max(1, card.Amount));
                break;
            case "Dualcast":
                if (orbs.Count > 0)
                {
                    Evoke(orbs[0], hp, ref block, ref energy, factors);
                    Evoke(orbs[0], hp, ref block, ref energy, factors);
                    orbs.RemoveAt(0);
                }
                break;
            case "Quadcast":
                if (orbs.Count > 0)
                {
                    SimOrb first = orbs[0];
                    for (int i = 0; i < Math.Max(1, card.Amount); i++)
                        Evoke(first, hp, ref block, ref energy, factors);
                    orbs.RemoveAt(0);
                }
                break;
            case "Shatter":
                while (orbs.Count > 0)
                {
                    Evoke(orbs[0], hp, ref block, ref energy, factors);
                    Evoke(orbs[0], hp, ref block, ref energy, factors);
                    orbs.RemoveAt(0);
                }
                break;
            case "MultiCast":
                for (int i = 0; i < Math.Min(12, cost + (card.Upgrade > 0 ? 1 : 0))
                    && orbs.Count > 0; i++)
                {
                    SimOrb first = orbs[0];
                    Evoke(first, hp, ref block, ref energy, factors);
                    if (card.Reworked) Evoke(first, hp, ref block, ref energy, factors);
                    orbs.RemoveAt(0);
                    if (card.Reworked)
                    {
                        // The rework restores the same type; a dark orb keeps
                        // the charge of the evoked orb.
                        Channel(orbs, ref capacity, first.Kind, focus, hp,
                            ref block, ref energy, factors, first.Kind == "DarkOrb"
                                ? first.Evoke : null);
                    }
                }
                break;
            case "BdRecursion":
                if (orbs.Count > 0)
                {
                    int index = card.Reworked ? orbs.Count - 1 : 0;
                    SimOrb selectedOrb = orbs[index];
                    Evoke(selectedOrb, hp, ref block, ref energy, factors);
                    if (card.Reworked)
                        Evoke(selectedOrb, hp, ref block, ref energy, factors);
                    orbs.RemoveAt(index);
                    Channel(orbs, ref capacity, selectedOrb.Kind, focus, hp,
                        ref block, ref energy, factors, selectedOrb.Kind == "DarkOrb"
                            ? selectedOrb.Evoke : null);
                }
                break;
            case "Feral":
                feral += Math.Max(1, card.Amount);
                break;
            case "BdHeatsinks":
                heatsinks += Math.Max(1, card.Amount);
                break;
            case "Loop":
                loop += Math.Max(1, card.Amount);
                break;
            case "ChargeBattery":
            case "Convergence":
                otherPower += 2;
                break;
            case "Scavenge":
                otherPower += 2;
                break;
            default:
                if (card.Type == CardType.Power && card.FocusGain == 0)
                    otherPower += 2.0;
                break;
        }

        // A selection is part of the search state, not a later arbitrary UI
        // heuristic. At the root the exact CardModel choices accompany the
        // move and are passed to the native selection screen.
        if (card.Kind == "Skim" && card.Reworked && hand.Count > 0)
        {
            SimCard victim = hand.OrderBy(ChoicePotentialForDiscard).First();
            hand.Remove(victim);
            discard.Add(victim);
            selection.Add(victim.Model);
            forecast += $" discard:{victim.Id}";
        }
        else if ((card.Kind is "BdRecycle" or "Scavenge"
            || card.Kind == "Stack" && card.Reworked)
            && hand.Count > 0)
        {
            SimCard victim = hand.OrderByDescending(c =>
                card.Kind == "BdRecycle"
                    ? c.Cost * 5 - ChoicePotentialForDiscard(c)
                    : -ChoicePotentialForDiscard(c)).First();
            hand.Remove(victim);
            selection.Add(victim.Model);
            forecast += $" exhaust:{victim.Id}";
            if (card.Kind == "BdRecycle") energy += victim.Cost;
            else if (card.Kind == "Stack") capacity = Math.Min(10, capacity + 1);
        }
        else if (card.Kind == "Stack" && card.Reworked)
            capacity = Math.Min(10, capacity + 1);

        if (card.Kind == "BdSeek")
        {
            List<SimCard> remaining = draw.Skip(drawCursor).ToList();
            SimCard[] chosen = remaining
                .OrderByDescending(c => ChoicePotential(c, energy))
                .Take(Math.Min(Math.Max(0, card.SelectCount), 10 - hand.Count))
                .ToArray();
            foreach (SimCard picked in chosen)
            {
                remaining.Remove(picked);
                hand.Add(picked);
                selection.Add(picked.Model);
                forecast += $" seek:{picked.Id}";
            }
            draw = remaining.ToArray();
            drawCursor = 0;
        }
        else if (card.Kind is "NeowsFury" or "Hologram")
        {
            SimCard[] chosen = discard
                .OrderByDescending(c => ChoicePotential(c, energy))
                .Where(c => c.Type is not (CardType.Status or CardType.Curse))
                .Take(Math.Min(Math.Max(0, card.SelectCount), 10 - hand.Count))
                .ToArray();
            foreach (SimCard picked in chosen)
            {
                discard.Remove(picked);
                hand.Add(picked);
                selection.Add(picked.Model);
                forecast += $" return:{picked.Id}";
            }
        }
        else if (card.Kind == "AllForOne")
        {
            int limit = card.Reworked ? card.Upgrade > 0 ? 3 : 2 : 10;
            SimCard[] chosen = discard.Where(c => c.Cost == 0 && !c.CostsX
                    && c.Type is CardType.Attack or CardType.Skill or CardType.Power)
                .OrderByDescending(c => ChoicePotential(c, energy))
                .Take(Math.Min(limit, 10 - hand.Count)).ToArray();
            foreach (SimCard picked in chosen)
            {
                discard.Remove(picked);
                hand.Add(picked);
                if (card.Reworked) selection.Add(picked.Model);
                forecast += $" return:{picked.Id}";
            }
        }

        if (card.Kind == "Reboot")
        {
            List<SimCard> shuffled = discard.Concat(draw.Skip(drawCursor))
                .Concat(hand).ToList();
            shuffled.Sort((a, b) =>
            {
                int order = string.Compare(a.SortId, b.SortId,
                    StringComparison.Ordinal);
                return order != 0 ? order : a.Upgrade.CompareTo(b.Upgrade);
            });
            Rng rng = Fork(shuffleRng);
            rng.Shuffle(shuffled);
            shuffleRng = rng;
            draw = shuffled.ToArray();
            drawCursor = 0;
            hand.Clear();
            discard.Clear();
            forecast += " reboot-shuffle";
        }

        if (card.DoubleEnergy) energy += Math.Max(0, energy);
        else if (sunderKill) energy += 3;
        else energy += card.EnergyGain;
        if (enchant is { Active: true, Kind: "Sown" })
            energy += Math.Max(0, enchant.Amount);

        int requestedDraw = card.Generator is Generator.JackOfAllTrades or Generator.BundleOfJoy
            ? 0 : card.Draw;
        if (card.Kind == "DoubleEnergy" && card.Reworked)
            requestedDraw = Math.Max(1, requestedDraw);
        if (card.ConditionalDrawLimit > 0)
            requestedDraw = node.CardsPlayedThisTurn < card.ConditionalDrawLimit
                ? Math.Max(1, requestedDraw) : 0;
        if (card.Kind == "CompileDriver")
            requestedDraw = orbs.Select(o => o.Kind).Distinct().Count();
        if (enchant is { Active: true, Kind: "Swift" })
            requestedDraw += Math.Max(0, enchant.Amount);
        int handBeforeDraw = hand.Count;
        DrawCards(requestedDraw, hand, ref draw, ref drawCursor, discard,
            ref shuffleRng, ref costRng, ref forecast);
        if (card.Kind == "Scrape")
        {
            // Scrape discards only cards drawn by this play whose current cost
            // is nonzero; cards already held before Scrape stay in hand.
            foreach (SimCard drawn in hand.Skip(handBeforeDraw)
                .Where(c => c.Cost != 0 || c.CostsX).ToArray())
            {
                hand.Remove(drawn);
                discard.Add(drawn);
                forecast += $" scrape-discard:{drawn.Id}";
            }
        }

        if (card.Type == CardType.Attack)
        {
            factors = factors with
            {
                VigorConsumed = factors.VigorConsumed || factors.Vigor > 0,
                PenNib = factors.PenNib < 0 ? -1 : (factors.PenNib + 1) % 10,
                Nunchaku = factors.Nunchaku < 0 ? -1 : (factors.Nunchaku + 1) % 10,
                Shuriken = factors.Shuriken < 0 ? -1 : (factors.Shuriken + 1) % 3,
                Kunai = factors.Kunai < 0 ? -1 : (factors.Kunai + 1) % 3,
                OrnamentalFan = factors.OrnamentalFan < 0 ? -1 :
                    (factors.OrnamentalFan + 1) % 3
            };
            if (factors.Nunchaku == 0) energy++;
            if (factors.Shuriken == 0) factors = factors with { Strength = factors.Strength + 1 };
            if (factors.Kunai == 0) factors = factors with { Dexterity = factors.Dexterity + 1 };
            if (factors.OrnamentalFan == 0) block += 4;
        }
        else if (card.Type == CardType.Skill && factors.LetterOpener >= 0)
        {
            factors = factors with { LetterOpener = (factors.LetterOpener + 1) % 3 };
            if (factors.LetterOpener == 0)
                for (int i = 0; i < hp.Length; i++) Hit(hp, i, 5, factors);
        }
        if (card.StrengthGain != 0 || card.DexterityGain != 0 || card.BufferGain != 0)
            factors = factors with
            {
                Strength = factors.Strength + card.StrengthGain,
                Dexterity = factors.Dexterity + card.DexterityGain,
                PlayerBuffer = factors.PlayerBuffer + card.BufferGain,
                TemporaryStrength = factors.TemporaryStrength +
                    (card.Kind == "Flex" ? card.StrengthGain : 0)
            };
        if (!card.VirtualReplay)
        {
            factors = factors with
            {
                FreeAttack = card.Type == CardType.Attack
                    ? Math.Max(0, factors.FreeAttack - 1) : factors.FreeAttack,
                FreeSkill = card.Type == CardType.Skill
                    ? Math.Max(0, factors.FreeSkill - 1) : factors.FreeSkill,
                FreePower = card.Type == CardType.Power
                    ? Math.Max(0, factors.FreePower - 1) : factors.FreePower,
                Burst = card.Type == CardType.Skill
                    ? Math.Max(0, factors.Burst - 1) : factors.Burst
            };
        }
        if (card.Kind == "Burst")
            factors = factors with { Burst = factors.Burst + Math.Max(1, card.Amount) };
        else if (card.Kind == "EchoForm")
            factors = factors with { EchoForm = factors.EchoForm + Math.Max(1, card.Amount) };
        else if (card.Kind == "Corruption")
            factors = factors with { Corruption = true };

        if (card.Generator != Generator.None && hand.Count < 10)
        {
            (SimCard[] generated, Rng nextRng) =
                PredictGenerated(card, generationRng, snapshot.Pools);
            generationRng = nextRng;
            foreach (SimCard generatedCard in generated)
            {
                if (hand.Count >= 10) break;
                hand.Add(generatedCard);
                if (forecast.Length < 2048) forecast += $" rng:{generatedCard.Id}";
            }
        }
        if (card.Type == CardType.Power && heatsinks > 0)
            DrawCards(heatsinks, hand, ref draw, ref drawCursor, discard,
                ref shuffleRng, ref costRng, ref forecast);

        // When-played cost offsets (including temporary zero-cost effects)
        // expire before a card can return to hand or be drawn again.
        SimCard playedCard = card with
        {
            Cost = card.CostAfterPlay,
            CostWithoutFreePower = card.CostAfterPlay,
            CostAfterTurn = card.CostAfterPlayedTurn,
            CapturedFreePower = false,
            GeneratedFree = false
        };
        if (enchant is { Active: true })
        {
            playedCard = enchant.Kind switch
            {
                "Glam" or "Sown" or "Swift" => playedCard with
                {
                    Enchant = enchant with { Active = false, ExtraReplays = 0 }
                },
                "Vigorous" => playedCard with
                {
                    Damage = Math.Max(0, playedCard.Damage -
                        enchant.Amount * playedCard.Hits),
                    Enchant = enchant with { Active = false }
                },
                "Momentum" => playedCard with
                {
                    Damage = playedCard.Damage + enchant.Amount * playedCard.Hits
                },
                "Goopy" => playedCard with
                {
                    Block = playedCard.Block + 1,
                    Enchant = enchant with { Amount = enchant.Amount + 1 }
                },
                _ => playedCard
            };
        }
        bool feralAllCards = node.FeralAllCards
            || card.Kind == "Feral" && card.Reworked;
        bool returnsToHand = !card.VirtualReplay &&
            (feralAllCards || card.Type == CardType.Attack)
            && !(factors.Corruption && card.Type == CardType.Skill)
            && !card.CostsX && cost == 0 && feral > 0 && hand.Count < 10;
        if (returnsToHand)
        {
            hand.Add(playedCard);
            feral--;
        }
        else if (!card.VirtualReplay && card.Type != CardType.Power
            && !card.Exhaust && !(factors.Corruption && card.Type == CardType.Skill))
            discard.Add(card.Kind == "Sunder" && card.Reworked && !sunderKill
                ? playedCard with
                {
                    Cost = Math.Max(0, playedCard.Cost - 1),
                    CostAfterPlay = Math.Max(0, playedCard.CostAfterPlay - 1),
                    CostWithoutFreePower = Math.Max(0, playedCard.CostWithoutFreePower - 1),
                    CostAfterTurn = Math.Max(0, playedCard.CostAfterTurn - 1),
                    CostAfterPlayedTurn = Math.Max(0, playedCard.CostAfterPlayedTurn - 1)
                }
                : playedCard);

        LocalMove? firstMove = node.First ?? (node.TurnIndex == 0
            ? new LocalMove(card.Model, target, 0,
                selection.Count > 0 ? selection.ToArray() : null) : null);
        Node resolved = new Node(hand.ToArray(), draw, drawCursor, discard.ToArray(),
            shuffleRng, generationRng, orbRng,
            Math.Clamp(energy, 0, 99), node.CardsPlayedThisTurn + 1,
            block, hp, orbs.ToArray(), capacity, focus, tempFocus,
            feral, heatsinks, loop, otherPower, node.Depth + 1, firstMove,
            forecast, feralAllCards, factors,
            node.SeriesPlayed + (card.VirtualReplay ? 0 : 1), costRng,
            node.TurnIndex, node.PlayerHp, node.ProjectedIncoming,
            node.FeralMax + (card.Kind == "Feral" ? Math.Max(1, card.Amount) : 0),
            node.FutureFirst.Length > 0 || node.TurnIndex == 0
                ? node.FutureFirst : card.Id);
        if (replays == 0) return resolved;
        SimCard replay = playedCard with
        {
            VirtualReplay = true,
            Cost = 0,
            CapturedFreePower = false,
            Enchant = playedCard.Enchant is { } active
                ? active with { ExtraReplays = 0 } : null
        };
        for (int i = 0; i < replays && resolved.Depth < MaxDepth; i++)
        {
            if (resolved.EnemyHp.All(h => h <= 0) ||
                targetIndex >= 0 && resolved.EnemyHp[targetIndex] <= 0) break;
            Node seeded = resolved with { Hand = [replay, .. resolved.Hand] };
            Node? repeated = Apply(seeded, replay, 0, targetIndex, target, snapshot);
            if (repeated is null) break;
            resolved = repeated;
            if (replay.Enchant is { Active: true, Kind: "Momentum" } momentum)
                replay = replay with
                {
                    Damage = replay.Damage + momentum.Amount * replay.Hits
                };
            else if (replay.Enchant is { Active: true, Kind: "Goopy" } goopy)
                replay = replay with
                {
                    Block = replay.Block + 1,
                    Enchant = goopy with { Amount = goopy.Amount + 1 }
                };
        }
        if (enchant is { Active: true, Kind: "Momentum" or "Goopy" })
        {
            SimCard settled = playedCard with
            {
                Damage = replay.Damage,
                Block = replay.Block,
                Enchant = replay.Enchant is { } last
                    ? last with { ExtraReplays = playedCard.Enchant?.ExtraReplays ?? 0 }
                    : playedCard.Enchant
            };
            SimCard Replace(SimCard item) => ReferenceEquals(item, playedCard)
                ? settled : item;
            resolved = resolved with
            {
                Hand = resolved.Hand.Select(Replace).ToArray(),
                Draw = resolved.Draw.Select(Replace).ToArray(),
                Discard = resolved.Discard.Select(Replace).ToArray()
            };
        }
        return resolved;
    }

    private static void DrawCards(int count, List<SimCard> hand,
        ref SimCard[] draw, ref int cursor, List<SimCard> discard,
        ref Rng shuffleRng, ref Rng costRng, ref string forecast)
    {
        for (int i = 0; i < count && hand.Count < 10; i++)
        {
            if (cursor >= draw.Length && discard.Count > 0)
            {
                Rng rng = Fork(shuffleRng);
                discard.Sort((a, b) =>
                {
                    int order = string.Compare(a.SortId, b.SortId, StringComparison.Ordinal);
                    return order != 0 ? order : a.Upgrade.CompareTo(b.Upgrade);
                });
                rng.Shuffle(discard);
                draw = discard.ToArray();
                cursor = 0;
                discard.Clear();
                shuffleRng = rng;
            }
            if (cursor >= draw.Length) break;
            SimCard next = draw[cursor++];
            if (next.Enchant is { Active: true, Kind: "Slither" })
            {
                Rng rng = Fork(costRng);
                int rolled = rng.NextInt(4);
                costRng = rng;
                next = next with
                {
                    Cost = rolled,
                    CostWithoutFreePower = rolled,
                    CostAfterPlay = rolled,
                    CostAfterTurn = rolled,
                    CostAfterPlayedTurn = rolled,
                    CapturedFreePower = false
                };
                if (forecast.Length < 2048) forecast += $" slither:{next.Id}/{rolled}";
            }
            hand.Add(next);
            if (forecast.Length < 2048) forecast += $" draw:{next.Id}";
        }
    }

    private static double ChoicePotential(SimCard card, int energy)
    {
        if (card.Type is CardType.Status or CardType.Curse) return -100;
        double value = card.Damage + card.Block * 0.45 + card.Draw * 3.5
            + card.EnergyGain * (energy <= 2 ? 5 : 1)
            + (card.DoubleEnergy ? Math.Max(0, energy - 1) * 2 : 0)
            + (card.OrbCard ? 3 : 0)
            + (card.Kind is "BdSeek" or "NeowsFury" ? 6 : 0)
            + (card.Type == CardType.Power ? 2 : 0);
        return value - Math.Max(0, card.Cost - energy) * 5;
    }

    private static double ChoicePotentialForDiscard(SimCard card) =>
        ChoicePotential(card, Math.Max(0, card.Cost))
        + (card.Type == CardType.Power ? 3 : 0);

    private static SimOrb NewOrb(string kind, int focus, double? darkCharge = null)
    {
        return kind switch
        {
            "LightningOrb" => new(kind, Math.Max(0, 3 + focus), Math.Max(0, 8 + focus)),
            "FrostOrb" => new(kind, Math.Max(0, 2 + focus), Math.Max(0, 5 + focus)),
            "DarkOrb" => new(kind, Math.Max(0, 6 + focus), darkCharge ?? 6),
            "PlasmaOrb" => new(kind, 1, 2),
            "GlassOrb" => new(kind, Math.Max(0, 4 + focus), Math.Max(0, 8 + 2 * focus)),
            _ => new(kind, 0, 0)
        };
    }

    private static void Channel(List<SimOrb> orbs, ref int capacity, string kind,
        int focus, double[] hp, ref double block, ref int energy,
        BattleFactors factors, double? darkCharge = null)
    {
        if (capacity <= 0) capacity = 1;
        if (orbs.Count >= capacity)
        {
            Evoke(orbs[0], hp, ref block, ref energy, factors);
            orbs.RemoveAt(0);
        }
        orbs.Add(NewOrb(kind, focus, darkCharge));
    }

    private static void Evoke(SimOrb orb, double[] hp,
        ref double block, ref int energy, BattleFactors factors)
    {
        switch (orb.Kind)
        {
            case "LightningOrb":
            case "DarkOrb":
                Hit(hp, Weakest(hp), orb.Evoke, factors);
                break;
            case "FrostOrb": block += orb.Evoke; break;
            case "PlasmaOrb": energy += (int)orb.Evoke; break;
            case "GlassOrb":
                for (int i = 0; i < hp.Length; i++) Hit(hp, i, orb.Evoke, factors);
                break;
        }
    }

    private static void TriggerPassive(List<SimOrb> orbs, int index,
        double[] hp, ref double block, ref int energy, BattleFactors factors)
    {
        SimOrb orb = orbs[index];
        switch (orb.Kind)
        {
            case "LightningOrb": Hit(hp, Weakest(hp), orb.Passive, factors); break;
            case "FrostOrb": block += orb.Passive; break;
            case "DarkOrb":
                orbs[index] = orb with { Evoke = orb.Evoke + orb.Passive };
                break;
            case "GlassOrb":
                for (int i = 0; i < hp.Length; i++) Hit(hp, i, orb.Passive, factors);
                orbs[index] = orb with
                {
                    Passive = Math.Max(0, orb.Passive - 1),
                    Evoke = Math.Max(0, orb.Evoke - 2)
                };
                break;
            case "PlasmaOrb": energy += (int)orb.Passive; break;
        }
    }

    private static int Weakest(double[] hp)
    {
        int best = -1;
        for (int i = 0; i < hp.Length; i++)
            if (hp[i] > 0 && (best < 0 || hp[i] < hp[best])) best = i;
        return best;
    }

    private static BattleFactors HitAttack(double[] hp, int index,
        double totalDamage, int hits, BattleFactors factors, BattleFactors initial)
    {
        if (index < 0 || index >= hp.Length || hp[index] <= 0 || hits <= 0)
            return factors;
        EnemyFactors enemy = factors.Enemies[index];
        double perHit = totalDamage / hits;
        // The displayed card preview already contains current Strength,
        // Vigor, Weak and Pen Nib. Only correct changes made by a simulated
        // earlier card or relic proc; otherwise these would be double-counted.
        if (initial.PenNib == 9) perHit /= 2;
        perHit += factors.Strength - initial.Strength;
        if (factors.VigorConsumed) perHit -= initial.Vigor;
        if (factors.PenNib == 9) perHit *= 2;
        if (enemy.Vulnerable > 0) perHit *= 1.5;
        double selfDamage = factors.SelfDamage;
        for (int hit = 0; hit < hits && hp[index] > 0; hit++)
        {
            double amount = Math.Max(0, perHit);
            if (enemy.Intangible > 0) amount = Math.Min(amount, 1);
            if (enemy.Thorns > 0) selfDamage += enemy.Thorns;
            double blocked = Math.Min(enemy.Block, amount);
            enemy = enemy with { Block = enemy.Block - blocked };
            hp[index] -= blocked;
            amount -= blocked;
            if (amount <= 0) continue;
            if (enemy.Buffer > 0) enemy = enemy with { Buffer = enemy.Buffer - 1 };
            else hp[index] = Math.Max(0, hp[index] - amount);
        }
        factors.Enemies[index] = enemy;
        return factors with { SelfDamage = selfDamage };
    }

    private static void Hit(double[] hp, int index, double value,
        BattleFactors? factors = null)
    {
        if (index < 0 || index >= hp.Length || hp[index] <= 0 || value <= 0)
            return;
        if (factors is null)
        {
            hp[index] = Math.Max(0, hp[index] - value);
            return;
        }
        EnemyFactors enemy = factors.Enemies[index];
        double damage = enemy.Intangible > 0 ? Math.Min(value, 1) : value;
        double blocked = Math.Min(enemy.Block, damage);
        hp[index] -= blocked;
        damage -= blocked;
        enemy = enemy with { Block = enemy.Block - blocked };
        if (damage > 0)
        {
            if (enemy.Buffer > 0) enemy = enemy with { Buffer = enemy.Buffer - 1 };
            else hp[index] = Math.Max(0, hp[index] - damage);
        }
        factors.Enemies[index] = enemy;
    }

    private static double EvaluateEnd(Node node, Snapshot snapshot)
    {
        double[] hp = (double[])node.EnemyHp.Clone();
        double block = node.Block;
        int unusedEnergy = node.Energy;
        List<SimOrb> orbs = node.Orbs.ToList();
        BattleFactors factors = node.Factors is { } live
            ? live with { Enemies = (EnemyFactors[])live.Enemies.Clone() }
            : BattleFactors.Empty(hp.Length);
        // Orb passives occur as the player turn ends, before the enemy attacks.
        for (int i = 0; i < orbs.Count; i++)
            if (orbs[i].Kind != "PlasmaOrb")
                TriggerPassive(orbs, i, hp, ref block, ref unusedEnergy, factors);
        double dealt = 0;
        BattleFactors initial = snapshot.Factors ?? BattleFactors.Empty(hp.Length);
        for (int i = 0; i < hp.Length; i++)
            dealt += Math.Max(0, snapshot.EnemyHp[i] -
                (node.TurnIndex > 0 ? initial.Enemies[i].Block : 0) - hp[i]);
        if (hp.All(h => h <= 0))
            return 10000 + dealt - node.Depth * 0.15;

        double incoming = 0;
        double largestAttack = 0;
        for (int i = 0; i < hp.Length; i++)
        {
            if (hp[i] <= 0) continue;
            double attack = (node.ProjectedIncoming ?? snapshot.Incoming)[i];
            if (factors.Enemies[i].Weak > 0 && initial.Enemies[i].Weak == 0)
                attack *= 0.75; // Existing Weak is already in the live intent.
            incoming += attack;
            largestAttack = Math.Max(largestAttack, attack);
        }
        incoming = Math.Min(999, incoming);
        double loss = Math.Max(0, incoming + factors.SelfDamage - block);
        if (factors.PlayerIntangible > 0)
            loss = Math.Min(loss, Math.Max(1, hp.Count(h => h > 0) * 2));
        if (factors.PlayerBuffer > 0)
            loss = Math.Max(0, loss - largestAttack * Math.Min(factors.PlayerBuffer, 1));
        double playerHp = node.PlayerHp > 0 ? node.PlayerHp : snapshot.PlayerHp;
        double totalLoss = snapshot.PlayerHp - playerHp + loss;
        if (loss >= playerHp)
            return -10000 + dealt - totalLoss * 10;

        double future = node.OtherPowerValue
            + Math.Min(6, node.Heatsinks * 3)
            + Math.Min(5, node.FeralUses * 2)
            + Math.Min(8, node.Loop * (orbs.Count > 0 ? 3 : 1));
        foreach (SimOrb orb in orbs)
        {
            double nextPassive = orb.Kind == "PlasmaOrb" ? orb.Passive
                : Math.Max(0, orb.Passive - node.TempFocus);
            future += orb.Kind switch
            {
                "DarkOrb" => Math.Min(25, orb.Evoke * 0.3),
                "PlasmaOrb" => 4,
                "FrostOrb" => nextPassive * 0.7,
                "LightningOrb" => nextPassive * 0.65,
                "GlassOrb" => nextPassive * 0.6,
                _ => 0
            };
        }
        future += Math.Max(0, node.Focus - node.TempFocus) * 2;
        future += Math.Clamp((factors.Strength - factors.TemporaryStrength)
            - (initial.Strength - initial.TemporaryStrength), -10, 10) * 2;
        future += Math.Clamp((factors.Dexterity - factors.TemporaryDexterity)
            - (initial.Dexterity - initial.TemporaryDexterity), -10, 10) * 1.5;
        future += Math.Min(2, factors.PlayerBuffer) * 4;
        for (int i = 0; i < hp.Length; i++)
            if (hp[i] > 0)
            {
                if (factors.Enemies[i].Weak > initial.Enemies[i].Weak) future += 2;
                if (factors.Enemies[i].Vulnerable > initial.Enemies[i].Vulnerable) future += 2;
            }
        return dealt - totalLoss * 12 + future + Math.Min(30, block - incoming) * 0.01
            - node.Depth * 0.15;
    }

    private static double BeamScore(Node node, Snapshot snapshot)
    {
        double value = EvaluateEnd(node, snapshot);
        if (value >= 10000) return value;
        double possibilities = node.Hand
            .Where(c => c.Type is not (CardType.Status or CardType.Curse))
            .Select(c => ChoicePotential(c with
                { Cost = EffectiveCost(c, node) }, node.Energy))
            .OrderByDescending(x => x).Take(4).Sum();
        return value + Math.Min(40, Math.Max(0, possibilities)) * 0.55
            + Math.Min(12, node.Energy) * 0.25;
    }

    private static string StateKey(Node node)
    {
        // Looping zero-cost cards may recreate the same position. Dominance
        // prefers the first (shorter) route, which also prevents frame spikes.
        return $"{node.TurnIndex}:{node.PlayerHp:0.0}:{node.Energy}:{node.Block:0.0}:{node.DrawCursor}:" +
            $"{node.CardsPlayedThisTurn}:{node.SeriesPlayed}:{node.OrbCapacity}:{node.OtherPowerValue:0.0}:" +
            $"{string.Join(',', node.EnemyHp.Select(h => Math.Round(h)))}:" +
            $"{string.Join(',', node.Hand.Select(c =>
                CardKey(c, EffectiveCost(c, node))).OrderBy(s => s))}:" +
            $"{string.Join(',', node.Draw.Skip(node.DrawCursor).Select(c => CardKey(c, c.Cost)))}:" +
            $"{string.Join(',', node.Discard.Select(c => CardKey(c, c.Cost)).OrderBy(s => s))}:" +
            $"{string.Join(',', node.Orbs.Select(o => $"{o.Kind}/{o.Passive:0}/{o.Evoke:0}"))}:" +
            $"{node.FeralUses}:{node.FeralMax}:{node.FeralAllCards}:{node.Heatsinks}:{node.Loop}:{node.Focus}:{node.TempFocus}:" +
            (node.Factors is { } f
                ? $"{f.Strength}/{f.Dexterity}/{f.PlayerBuffer}/{f.PenNib}/{f.Nunchaku}/" +
                  $"{f.Shuriken}/{f.Kunai}/{f.OrnamentalFan}/{f.LetterOpener}/{f.VigorConsumed}/" +
                  $"{f.FreeAttack}/{f.FreeSkill}/{f.FreePower}/{f.Burst}/{f.EchoForm}/{f.Corruption}/" +
                  $"{f.TemporaryStrength}/{f.TemporaryDexterity}/" +
                  $"{f.SelfDamage:0.0}/{string.Join(',', f.Enemies.Select(e =>
                      $"{e.Weak}-{e.Vulnerable}-{e.Intangible}-{e.Buffer}-{e.Artifact}-{e.Block:0}"))}"
                : "");
    }

    private static string CardKey(SimCard card, int cost) =>
        $"{card.Id}/{cost}/{card.CostAfterPlay}/{card.CostAfterTurn}/" +
        $"{card.CostAfterPlayedTurn}/{card.Retain}/{card.PermanentRetain}/{card.Ethereal}/" +
        $"{card.Enchant?.Kind}/{card.Enchant?.Amount}/{card.Enchant?.Active}/" +
        $"{card.Enchant?.ExtraReplays}";
    private static (SimCard[], Rng) PredictGenerated(
        SimCard source, Rng state, SimCard[][] pools)
    {
        Pool pool = source.Generator switch
        {
            Generator.WhiteNoise or Generator.Abundance => Pool.CharacterPower,
            Generator.InfernalBlade => Pool.CharacterAttack,
            Generator.Distraction => Pool.CharacterSkill,
            Generator.JackOfAllTrades or Generator.BundleOfJoy => Pool.Colorless,
            _ => Pool.CharacterAll
        };
        List<SimCard> choices = pools[(int)pool].ToList();
        if (source.Generator == Generator.JackOfAllTrades)
            choices.RemoveAll(c => c.Id == source.Id);
        if (choices.Count == 0) return ([], state);
        Rng rng = Fork(state);
        // Native StableShuffle sorts models before consuming RNG. Repeating
        // that order is necessary to predict a generated card reliably.
        choices.Sort((a, b) => string.Compare(a.SortId, b.SortId,
            StringComparison.Ordinal));
        rng.Shuffle(choices);
        int count = source.Generator switch
        {
            Generator.Discovery or Generator.Abundance => 3,
            Generator.WhiteNoise when source.BestOfThree => 3,
            Generator.JackOfAllTrades or Generator.BundleOfJoy =>
                Math.Clamp(source.Draw, 1, 10),
            _ => 1
        };
        SimCard[] rolled = choices.Take(count).ToArray();
        if (source.Generator is Generator.Discovery or Generator.Abundance || source.BestOfThree)
            rolled = rolled.OrderByDescending(ChoiceValue).Take(1).ToArray();
        bool free = source.Generator is Generator.Discovery or Generator.WhiteNoise
            or Generator.InfernalBlade or Generator.Distraction or Generator.Abundance;
        if (free) rolled = rolled.Select(c => c with { Cost = 0, GeneratedFree = true }).ToArray();
        return (rolled, rng);
    }

    private static double ChoiceValue(SimCard card) => card.Type is CardType.Status or CardType.Curse
        ? -12 : card.Damage + card.Block * 0.65 + card.Draw * 3 + card.EnergyGain * 4
            + (card.Type == CardType.Power ? 7 : 0) - (card.CostsX ? 2 : card.Cost) * 1.5;

    private static bool IsBetterDefectRework(CardModel card)
    {
        try
        {
            return ReworkCheckMethod.Value?.Invoke(null, [card]) is true;
        }
        catch { return false; }
    }

    private static bool IsReworkedFeral()
    {
        try { return IsBetterDefectRework(ModelDb.Card<Feral>()); }
        catch { return false; }
    }

    private static int RemainingIncoming(double[] hp, int[] incoming)
    {
        int total = 0;
        for (int i = 0; i < hp.Length; i++) if (hp[i] > 0) total += incoming[i];
        return Math.Min(999, total);
    }

    private static int IncomingFrom(Creature enemy, Player player)
    {
        int total = 0;
        try
        {
            if (enemy.Monster is null) return 0;
            foreach (AttackIntent intent in enemy.Monster.NextMove.Intents.OfType<AttackIntent>())
                total = Math.Min(999, total + Math.Max(0,
                    intent.GetTotalDamage([player.Creature], enemy)));
        }
        catch { }
        return total;
    }

    // v0.107.1 stores seed/counter; v0.111.0 stores the full four-word state.
    // Reflection keeps the two builds source-compatible without touching the
    // live RNG. Every search branch owns an independent fork.
    private static Rng Fork(Rng source)
    {
        Type type = typeof(Rng);
        MethodInfo? save = type.GetMethod("ToSerializable", Type.EmptyTypes);
        if (save is not null)
        {
            object state = save.Invoke(source, null)
                ?? throw new InvalidOperationException("RNG snapshot unavailable");
            return (Rng)(Activator.CreateInstance(type, state)
                ?? throw new InvalidOperationException("Cannot fork RNG"));
        }
        uint seed = (uint)(type.GetProperty("Seed")?.GetValue(source)
            ?? throw new InvalidOperationException("RNG seed unavailable"));
        int counter = (int)(type.GetProperty("Counter")?.GetValue(source)
            ?? throw new InvalidOperationException("RNG counter unavailable"));
        return (Rng)(Activator.CreateInstance(type, seed, counter)
            ?? throw new InvalidOperationException("Cannot fork RNG"));
    }

    private static double Amount(CardModel card, string key, double fallback = 0)
    {
        try
        {
            if (!card.DynamicVars.TryGetValue(key, out DynamicVar? value)) return fallback;
            decimal amount = value is DamageVar or BlockVar ? value.PreviewValue : value.BaseValue;
            return Math.Max(0, (double)amount);
        }
        catch { return fallback; }
    }
}
