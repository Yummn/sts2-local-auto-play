using System.Diagnostics;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.CardPools;
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
    private const int BeamWidth = 72;
    private const int MaxNodes = 30000;
    private const int MaxSearchMs = 450;
    private static readonly Lazy<MethodInfo?> ReworkCheckMethod = new(() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "BetterDefect")?
            .GetType("BetterDefect.BdCardVersionUpgrades")?
            .GetMethod("IsVersionEnabled", BindingFlags.Static | BindingFlags.NonPublic,
                null, [typeof(CardModel)], null));

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
        int Amount = 0, int FocusGain = 0);

    private sealed record SimOrb(string Kind, double Passive, double Evoke);

    private sealed record Snapshot(
        SimCard[] Hand, SimCard[] Draw, SimCard[] Discard,
        SimCard[][] Pools, Rng ShuffleRng,
        Rng GenerationRng, Rng OrbRng, int Energy, int CardsPlayedThisTurn,
        double Block, double PlayerHp, double[] EnemyHp, int[] Incoming,
        SimOrb[] Orbs, int OrbCapacity, int Focus, int TempFocus, int FeralUses,
        int Heatsinks, int Loop, bool FeralAllCards = false);

    private sealed record Node(
        SimCard[] Hand, SimCard[] Draw, int DrawCursor, SimCard[] Discard,
        Rng ShuffleRng, Rng GenerationRng, Rng OrbRng,
        int Energy, int CardsPlayedThisTurn, double Block, double[] EnemyHp,
        SimOrb[] Orbs, int OrbCapacity, int Focus, int TempFocus, int FeralUses,
        int Heatsinks, int Loop, double OtherPowerValue,
        int Depth, LocalMove? First, string Forecast,
        bool FeralAllCards = false);

    private readonly record struct SearchResult(LocalMove? Move, int Nodes, int Depth,
        bool Exhaustive, string Forecast);

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
            player.Creature.Powers.Sum(p => p.GetType().Name switch
            {
                "FocusedStrikePower" or "BdBarrageTemporaryFocusPower" => p.Amount,
                "BdHyperbeamTemporaryFocusDownPower" => -p.Amount,
                _ => 0
            }),
            player.Creature.Powers.Where(p => p.GetType().Name == "FeralPower").Sum(p => p.DisplayAmount),
            player.Creature.Powers.Where(p => p.GetType().Name == "BdHeatsinksPower").Sum(p => p.Amount),
            player.Creature.Powers.Where(p => p.GetType().Name is "LoopPower" or "BdLoopPower").Sum(p => p.Amount),
            IsReworkedFeral());
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
                _ => Amount(card, "Amount", 1)
            }),
            FocusGain: (int)Amount(card, "FocusPower"));
    }

    private static SearchResult Search(Snapshot snapshot, CancellationToken token)
    {
        List<Node> frontier = [new Node(snapshot.Hand, snapshot.Draw, 0,
            snapshot.Discard, snapshot.ShuffleRng, snapshot.GenerationRng, snapshot.OrbRng,
            snapshot.Energy, snapshot.CardsPlayedThisTurn,
            snapshot.Block, snapshot.EnemyHp, snapshot.Orbs, snapshot.OrbCapacity,
            snapshot.Focus, snapshot.TempFocus, snapshot.FeralUses,
            snapshot.Heatsinks, snapshot.Loop,
            0, 0, null, "", snapshot.FeralAllCards)];
        Node best = frontier[0]; // Ending the turn is always a legal candidate.
        double bestScore = EvaluateEnd(best, snapshot);
        Stopwatch watch = Stopwatch.StartNew();
        int expanded = 0;
        int deepest = 0;
        bool cutoff = false;
        Dictionary<string, double> visited = new(StringComparer.Ordinal);
        for (int depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            List<Node> next = new();
            foreach (Node node in frontier)
            {
                for (int handIndex = 0; handIndex < node.Hand.Length; handIndex++)
                {
                    SimCard card = node.Hand[handIndex];
                    if (card.Type is CardType.Status or CardType.Curse) continue;
                    int cost = card.CostsX ? node.Energy : card.Cost;
                    if (cost > node.Energy) continue;
                    foreach ((int targetIndex, Creature? target) in Targets(card, node.EnemyHp, depth))
                    {
                        if ((expanded & 63) == 0)
                        {
                            token.ThrowIfCancellationRequested();
                            if (expanded >= MaxNodes || watch.ElapsedMilliseconds >= MaxSearchMs)
                            { cutoff = true; break; }
                        }
                        Node? child = Apply(node, card, handIndex, targetIndex, target,
                            snapshot);
                        if (child is null) continue;
                        expanded++;
                        deepest = Math.Max(deepest, child.Depth);
                        double terminal = EvaluateEnd(child, snapshot);
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
            if (cutoff && (expanded >= MaxNodes || watch.ElapsedMilliseconds >= MaxSearchMs)) break;
        }
        if (deepest >= MaxDepth) cutoff = true;
        return new SearchResult(best.First is { } first ? first with { Score = bestScore } : null,
            expanded, deepest, !cutoff, best.Forecast);
    }

    private static IEnumerable<(int Index, Creature? Target)> Targets(
        SimCard card, double[] hp, int depth)
    {
        if (depth == 0) return card.RootTargets;
        if (card.TargetType == TargetType.AnyEnemy)
            return Enumerable.Range(0, hp.Length).Where(i => hp[i] > 0).Select(i => (i, (Creature?)null));
        if (card.TargetType == TargetType.AnyAlly) return [];
        return [(-1, null)];
    }

    private static Node? Apply(Node node, SimCard card, int handIndex,
        int targetIndex, Creature? target, Snapshot snapshot)
    {
        if (node.EnemyHp.All(h => h <= 0)) return null;
        int cost = card.CostsX ? node.Energy : card.Cost;
        if (cost > node.Energy) return null;

        int energy = node.Energy - cost;
        int xMultiplier = card.CostsX ? Math.Max(0, cost) : 1;
        if (card.Reworked && xMultiplier >= 4
            && card.Kind is "HelixDrill" or "BdReinforcedBody")
            xMultiplier *= 2;
        double block = node.Block + card.Block * xMultiplier;
        if (card.Kind == "Stack" && !card.Reworked)
            block += node.Discard.Length + (card.Upgrade > 0 ? 3 : 0);
        double[] hp = (double[])node.EnemyHp.Clone();
        List<SimOrb> orbs = node.Orbs.ToList();
        int capacity = node.OrbCapacity;
        int focusDelta = card.Kind == "Hyperbeam" ? -card.FocusGain
            : card.Kind == "Barrage" && card.Reworked ? (int)card.Damage
            : card.FocusGain;
        int focus = node.Focus + focusDelta;
        int tempFocus = node.TempFocus + (card.Kind is "FocusedStrike" or "Hyperbeam"
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
        string forecast = node.Forecast.Length < 2048
            ? node.Forecast + $" play:{card.Id}" : node.Forecast;
        List<CardModel> selection = new();

        // Damage and block are taken from the game's current preview. The
        // remaining handlers cover effects that change what can be played next.
        double damage = card.Damage * xMultiplier;
        if (card.Kind == "Barrage" && !card.Reworked)
            damage *= orbs.Count;
        if (damage > 0 && !(card.Kind == "Barrage" && card.Reworked))
        {
            if (card.TargetType == TargetType.AllEnemies)
                for (int i = 0; i < hp.Length; i++) Hit(hp, i, damage);
            else if (targetIndex >= 0) Hit(hp, targetIndex, damage);
            else if (card.Type == CardType.Attack)
            {
                int i = Array.FindIndex(hp, h => h > 0);
                if (i >= 0) Hit(hp, i, damage * 0.65);
            }
        }
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
                    TriggerPassive(orbs, i, hp, ref block, ref energy);
                break;
            case "BallLightning":
            case "Zap":
            case "BdElectrodynamics":
                for (int i = 0; i < (card.Kind == "BdElectrodynamics"
                    ? Math.Max(1, card.Amount) : 1); i++)
                    Channel(orbs, ref capacity, "LightningOrb", focus, hp,
                        ref block, ref energy);
                break;
            case "ColdSnap":
                for (int i = 0; i < (card.Reworked ? 2 : 1); i++)
                    Channel(orbs, ref capacity, "FrostOrb", focus, hp,
                        ref block, ref energy);
                break;
            case "Coolheaded":
                Channel(orbs, ref capacity, "FrostOrb", focus, hp,
                    ref block, ref energy);
                break;
            case "Glacier":
                for (int i = 0; i < 2; i++)
                    Channel(orbs, ref capacity, "FrostOrb", focus, hp,
                        ref block, ref energy);
                break;
            case "Chill":
                int chillCount = hp.Count(h => h > 0);
                for (int i = 0; i < chillCount; i++)
                    Channel(orbs, ref capacity, "FrostOrb", focus, hp,
                        ref block, ref energy);
                break;
            case "Fusion":
                Channel(orbs, ref capacity, "PlasmaOrb", focus, hp,
                    ref block, ref energy);
                break;
            case "MeteorStrike":
                for (int i = 0; i < (card.Reworked ? 2 : 3); i++)
                    Channel(orbs, ref capacity, "PlasmaOrb", focus, hp,
                        ref block, ref energy);
                break;
            case "Glasswork":
                Channel(orbs, ref capacity, "GlassOrb", focus, hp,
                    ref block, ref energy);
                break;
            case "Darkness":
                Channel(orbs, ref capacity, "DarkOrb", focus, hp,
                    ref block, ref energy);
                for (int i = 0; i < orbs.Count; i++)
                    if (orbs[i].Kind == "DarkOrb")
                        for (int n = 0; n < (card.Upgrade > 0 ? 2 : 1); n++)
                            TriggerPassive(orbs, i, hp, ref block, ref energy);
                break;
            case "BdDoomAndGloom":
            case "ConsumingShadow":
                Channel(orbs, ref capacity, "DarkOrb", focus, hp,
                    ref block, ref energy);
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
                        ref block, ref energy);
                }
                break;
            case "Rainbow":
                foreach (string kind in card.Reworked
                    ? new[] { "LightningOrb", "FrostOrb", "GlassOrb",
                        "DarkOrb", "PlasmaOrb" }
                    : new[] { "LightningOrb", "FrostOrb", "DarkOrb" })
                    Channel(orbs, ref capacity, kind, focus, hp,
                        ref block, ref energy);
                break;
            case "Tempest":
                int tempestDraw = 0;
                for (int i = 0; i < Math.Min(12, cost + (card.Upgrade > 0 ? 1 : 0)); i++)
                {
                    if (card.Reworked && orbs.Count >= capacity
                        && orbs.Count > 0 && orbs[0].Kind == "LightningOrb")
                        tempestDraw++;
                    Channel(orbs, ref capacity, "LightningOrb", focus, hp,
                        ref block, ref energy);
                }
                if (tempestDraw > 0)
                    DrawCards(tempestDraw, hand, ref draw, ref drawCursor, discard,
                        ref shuffleRng, ref forecast);
                break;
            case "Capacitor":
            case "BdExpand":
                capacity = Math.Min(10, capacity + Math.Max(1, card.Amount));
                break;
            case "Dualcast":
                if (orbs.Count > 0)
                {
                    Evoke(orbs[0], hp, ref block, ref energy);
                    Evoke(orbs[0], hp, ref block, ref energy);
                    orbs.RemoveAt(0);
                }
                break;
            case "Quadcast":
                if (orbs.Count > 0)
                {
                    SimOrb first = orbs[0];
                    for (int i = 0; i < Math.Max(1, card.Amount); i++)
                        Evoke(first, hp, ref block, ref energy);
                    orbs.RemoveAt(0);
                }
                break;
            case "Shatter":
                while (orbs.Count > 0)
                {
                    Evoke(orbs[0], hp, ref block, ref energy);
                    Evoke(orbs[0], hp, ref block, ref energy);
                    orbs.RemoveAt(0);
                }
                break;
            case "MultiCast":
                for (int i = 0; i < Math.Min(12, cost + (card.Upgrade > 0 ? 1 : 0))
                    && orbs.Count > 0; i++)
                {
                    SimOrb first = orbs[0];
                    Evoke(first, hp, ref block, ref energy);
                    if (card.Reworked) Evoke(first, hp, ref block, ref energy);
                    orbs.RemoveAt(0);
                    if (card.Reworked)
                    {
                        // The rework restores the same type; a dark orb keeps
                        // the charge of the evoked orb.
                        Channel(orbs, ref capacity, first.Kind, focus, hp,
                            ref block, ref energy, first.Kind == "DarkOrb"
                                ? first.Evoke : null);
                    }
                }
                break;
            case "BdRecursion":
                if (orbs.Count > 0)
                {
                    int index = card.Reworked ? orbs.Count - 1 : 0;
                    SimOrb selectedOrb = orbs[index];
                    Evoke(selectedOrb, hp, ref block, ref energy);
                    if (card.Reworked)
                        Evoke(selectedOrb, hp, ref block, ref energy);
                    orbs.RemoveAt(index);
                    Channel(orbs, ref capacity, selectedOrb.Kind, focus, hp,
                        ref block, ref energy, selectedOrb.Kind == "DarkOrb"
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

        int requestedDraw = card.Generator is Generator.JackOfAllTrades or Generator.BundleOfJoy
            ? 0 : card.Draw;
        if (card.Kind == "DoubleEnergy" && card.Reworked)
            requestedDraw = Math.Max(1, requestedDraw);
        if (card.ConditionalDrawLimit > 0)
            requestedDraw = node.CardsPlayedThisTurn < card.ConditionalDrawLimit
                ? Math.Max(1, requestedDraw) : 0;
        if (card.Kind == "CompileDriver")
            requestedDraw = orbs.Select(o => o.Kind).Distinct().Count();
        int handBeforeDraw = hand.Count;
        DrawCards(requestedDraw, hand, ref draw, ref drawCursor, discard,
            ref shuffleRng, ref forecast);
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
                ref shuffleRng, ref forecast);

        bool feralAllCards = node.FeralAllCards
            || card.Kind == "Feral" && card.Reworked;
        bool returnsToHand = (feralAllCards || card.Type == CardType.Attack)
            && !card.CostsX && card.Cost == 0 && feral > 0 && hand.Count < 10;
        if (returnsToHand)
        {
            hand.Add(card);
            feral--;
        }
        else if (card.Type != CardType.Power && !card.Exhaust)
            discard.Add(card.Kind == "Sunder" && card.Reworked && !sunderKill
                ? card with { Cost = Math.Max(0, card.Cost - 1) } : card);

        LocalMove firstMove = node.First
            ?? new LocalMove(card.Model, target, 0,
                selection.Count > 0 ? selection.ToArray() : null);
        return new Node(hand.ToArray(), draw, drawCursor, discard.ToArray(),
            shuffleRng, generationRng, orbRng,
            Math.Clamp(energy, 0, 99), node.CardsPlayedThisTurn + 1,
            block, hp, orbs.ToArray(), capacity, focus, tempFocus,
            feral, heatsinks, loop, otherPower, node.Depth + 1, firstMove,
            forecast, feralAllCards);
    }

    private static void DrawCards(int count, List<SimCard> hand,
        ref SimCard[] draw, ref int cursor, List<SimCard> discard,
        ref Rng shuffleRng, ref string forecast)
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
        double? darkCharge = null)
    {
        if (capacity <= 0) capacity = 1;
        if (orbs.Count >= capacity)
        {
            Evoke(orbs[0], hp, ref block, ref energy);
            orbs.RemoveAt(0);
        }
        orbs.Add(NewOrb(kind, focus, darkCharge));
    }

    private static void Evoke(SimOrb orb, double[] hp,
        ref double block, ref int energy)
    {
        switch (orb.Kind)
        {
            case "LightningOrb":
            case "DarkOrb":
                Hit(hp, Weakest(hp), orb.Evoke);
                break;
            case "FrostOrb": block += orb.Evoke; break;
            case "PlasmaOrb": energy += (int)orb.Evoke; break;
            case "GlassOrb":
                for (int i = 0; i < hp.Length; i++) Hit(hp, i, orb.Evoke);
                break;
        }
    }

    private static void TriggerPassive(List<SimOrb> orbs, int index,
        double[] hp, ref double block, ref int energy)
    {
        SimOrb orb = orbs[index];
        switch (orb.Kind)
        {
            case "LightningOrb": Hit(hp, Weakest(hp), orb.Passive); break;
            case "FrostOrb": block += orb.Passive; break;
            case "DarkOrb":
                orbs[index] = orb with { Evoke = orb.Evoke + orb.Passive };
                break;
            case "GlassOrb":
                for (int i = 0; i < hp.Length; i++) Hit(hp, i, orb.Passive);
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

    private static void Hit(double[] hp, int index, double value)
    {
        if (index >= 0 && index < hp.Length && hp[index] > 0)
            hp[index] = Math.Max(0, hp[index] - Math.Max(0, value));
    }

    private static double EvaluateEnd(Node node, Snapshot snapshot)
    {
        double[] hp = (double[])node.EnemyHp.Clone();
        double block = node.Block;
        int unusedEnergy = node.Energy;
        List<SimOrb> orbs = node.Orbs.ToList();
        // Orb passives occur as the player turn ends, before the enemy attacks.
        for (int i = 0; i < orbs.Count; i++)
            TriggerPassive(orbs, i, hp, ref block, ref unusedEnergy);
        double dealt = 0;
        for (int i = 0; i < hp.Length; i++)
            dealt += Math.Max(0, snapshot.EnemyHp[i] - hp[i]);
        if (hp.All(h => h <= 0))
            return 10000 + dealt - node.Depth * 0.15;

        double incoming = RemainingIncoming(hp, snapshot.Incoming);
        double loss = Math.Max(0, incoming - block);
        if (loss >= snapshot.PlayerHp)
            return -10000 + dealt - loss * 10;

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
        return dealt - loss * 12 + future + Math.Min(30, block - incoming) * 0.01
            - node.Depth * 0.15;
    }

    private static double BeamScore(Node node, Snapshot snapshot)
    {
        double value = EvaluateEnd(node, snapshot);
        if (value >= 10000) return value;
        double possibilities = node.Hand
            .Where(c => c.Type is not (CardType.Status or CardType.Curse))
            .Select(c => ChoicePotential(c, node.Energy))
            .OrderByDescending(x => x).Take(4).Sum();
        return value + Math.Min(40, Math.Max(0, possibilities)) * 0.55
            + Math.Min(12, node.Energy) * 0.25;
    }

    private static string StateKey(Node node)
    {
        // Looping zero-cost cards may recreate the same position. Dominance
        // prefers the first (shorter) route, which also prevents frame spikes.
        return $"{node.Energy}:{node.Block:0.0}:{node.DrawCursor}:" +
            $"{node.CardsPlayedThisTurn}:{node.OrbCapacity}:{node.OtherPowerValue:0.0}:" +
            $"{string.Join(',', node.EnemyHp.Select(h => Math.Round(h)))}:" +
            $"{string.Join(',', node.Hand.Select(c => $"{c.Id}/{c.Cost}").OrderBy(s => s))}:" +
            $"{string.Join(',', node.Draw.Skip(node.DrawCursor).Select(c => c.Id))}:" +
            $"{string.Join(',', node.Discard.Select(c => c.Id).OrderBy(s => s))}:" +
            $"{string.Join(',', node.Orbs.Select(o => $"{o.Kind}/{o.Passive:0}/{o.Evoke:0}"))}:" +
            $"{node.FeralUses}:{node.FeralAllCards}:{node.Heatsinks}:{node.Loop}:{node.Focus}:{node.TempFocus}";
    }
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
