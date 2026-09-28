using System.Diagnostics;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Random;

namespace LocalAutoPlay;

/// <summary>
/// Snapshots the known draw order and RNG streams on the game thread, then
/// searches a pure, approximate turn model on a worker. Never plays preview
/// actions or advances the game's real RNG.
/// </summary>
internal static class TurnForecastPlanner
{
    private const int MaxDepth = 48;
    private const int BeamWidth = 72;
    private const int MaxNodes = 30000;
    private const int MaxSearchMs = 350;

    private enum Generator { None, Discovery, WhiteNoise, InfernalBlade, Distraction, JackOfAllTrades, BundleOfJoy, Abundance }
    private enum Pool { CharacterAll, CharacterPower, CharacterAttack, CharacterSkill, Colorless }

    private sealed record SimCard(
        CardModel Model, string Id, string SortId, int Upgrade, CardType Type,
        TargetType TargetType, int Cost, bool CostsX, bool Exhaust,
        double Damage, double Block, int Draw, int EnergyGain,
        bool DoubleEnergy, bool OrbCard, Generator Generator,
        bool RootPlayable, (int Index, Creature? Target)[] RootTargets,
        bool BestOfThree = false,
        bool GeneratedFree = false);

    private sealed record Snapshot(
        SimCard[] Hand, SimCard[] Draw, SimCard[] Discard,
        SimCard[][] Pools, Rng ShuffleRng,
        Rng GenerationRng, int Energy, double Block,
        double PlayerHp, double[] EnemyHp, int[] Incoming);

    private sealed record Node(
        SimCard[] Hand, SimCard[] Draw, int DrawCursor, SimCard[] Discard,
        Rng ShuffleRng, Rng GenerationRng,
        int Energy, double Block, double[] EnemyHp,
        double Score, int Depth, LocalMove? First, string Forecast);

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
            Math.Max(0, pcs.Energy), (double)player.Creature.Block,
            (double)player.Creature.CurrentHp,
            enemies.Select(e => (double)(e.CurrentHp + e.Block)).ToArray(), incoming);
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
            or "Chaos" or "Rainbow" or "Tempest";
        double repeat = Math.Clamp(Amount(card, "Repeat", 1), 1, 8);
        return new SimCard(card, card.Id.Entry, card.Id.ToString(), card.CurrentUpgradeLevel,
            card.Type, card.TargetType,
            card.EnergyCost.CostsX ? 0 : Math.Max(0, card.EnergyCost.GetAmountToSpend()),
            card.EnergyCost.CostsX,
            card.Keywords.Contains(CardKeyword.Exhaust),
            Math.Max(Amount(card, "Damage"), Amount(card, "OstyDamage")) * repeat,
            Amount(card, "Block"), (int)Math.Clamp(Amount(card, "Cards"), 0, 10),
            (int)Math.Clamp(Amount(card, "Energy"), 0, 9),
            name == "DoubleEnergy", orb, generator, playable, targets.ToArray(),
            name == "WhiteNoise" && IsBetterDefectRework(card));
    }

    private static SearchResult Search(Snapshot snapshot, CancellationToken token)
    {
        List<Node> frontier = [new Node(snapshot.Hand, snapshot.Draw, 0,
            snapshot.Discard, snapshot.ShuffleRng, snapshot.GenerationRng,
            snapshot.Energy, snapshot.Block, snapshot.EnemyHp, 0, 0, null, "")];
        Node? best = null;
        Stopwatch watch = Stopwatch.StartNew();
        int expanded = 0;
        int deepest = 0;
        bool cutoff = false;
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
                            snapshot.Incoming, snapshot.PlayerHp, snapshot.Pools);
                        if (child is null) continue;
                        expanded++;
                        deepest = Math.Max(deepest, child.Depth);
                        next.Add(child);
                        if (best is null || child.Score > best.Score) best = child;
                    }
                    if (cutoff) break;
                }
                if (cutoff) break;
            }
            if (next.Count > BeamWidth) cutoff = true;
            frontier = next.OrderByDescending(n => n.Score).Take(BeamWidth).ToList();
            if (cutoff && (expanded >= MaxNodes || watch.ElapsedMilliseconds >= MaxSearchMs)) break;
        }
        if (deepest >= MaxDepth) cutoff = true;
        return new SearchResult(best?.First is { } first ? first with { Score = best.Score } : null,
            expanded, deepest, !cutoff, best?.Forecast ?? "");
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
        int targetIndex, Creature? target, int[] incoming, double playerHp,
        SimCard[][] pools)
    {
        if (node.EnemyHp.All(h => h <= 0)) return null;
        int cost = card.CostsX ? node.Energy : card.Cost;
        int energy = node.Energy - cost;
        double[] hp = (double[])node.EnemyHp.Clone();
        int beforeIncoming = RemainingIncoming(hp, incoming);
        double value = 0.15;
        if (card.Damage > 0)
        {
            if (card.TargetType == TargetType.AllEnemies)
            {
                for (int i = 0; i < hp.Length; i++) value += Deal(i, 0.85);
            }
            else if (targetIndex >= 0) value += Deal(targetIndex, 1);
            else if (card.Type == CardType.Attack)
            {
                int i = Array.FindIndex(hp, h => h > 0);
                if (i >= 0) value += Deal(i, 0.6);
            }
        }
        if (hp.All(h => h <= 0)) value += 14;
        int afterIncoming = RemainingIncoming(hp, incoming);
        double usefulBlock = Math.Min(card.Block, Math.Max(0, afterIncoming - node.Block));
        value += usefulBlock * (afterIncoming - node.Block >= playerHp ? 2.5 : 1.5);
        if (card.Block > usefulBlock) value += Math.Min(card.Block - usefulBlock, 10) * 0.03;
        if (card.Type == CardType.Power && hp.Any(h => h > 0)) value += 9;
        if (card.OrbCard) value += 4;
        if (card.Generator != Generator.None) value += 1.5;
        if (card.DoubleEnergy) energy += Math.Max(0, energy);
        else energy += card.EnergyGain;
        if (card.EnergyGain > 0 || card.DoubleEnergy)
            value += Math.Min(4, card.DoubleEnergy ? energy / 2 : card.EnergyGain) * 2.1;
        value -= cost * 0.45;

        List<SimCard> hand = node.Hand.ToList();
        hand.RemoveAt(handIndex);
        SimCard[] draw = node.Draw;
        int drawCursor = node.DrawCursor;
        List<SimCard> discard = node.Discard.ToList();
        Rng shuffleRng = node.ShuffleRng;
        Rng generationRng = node.GenerationRng;
        string forecast = node.Forecast;
        int requestedDraw = card.Generator is Generator.JackOfAllTrades or Generator.BundleOfJoy
            ? 0 : card.Draw;
        for (int i = 0; i < requestedDraw && hand.Count < 10; i++)
        {
            if (drawCursor >= draw.Length && discard.Count > 0)
            {
                Rng rng = Fork(shuffleRng);
                discard.Sort((a, b) =>
                {
                    int result = string.Compare(a.SortId, b.SortId, StringComparison.Ordinal);
                    return result != 0 ? result : a.Upgrade.CompareTo(b.Upgrade);
                });
                rng.Shuffle(discard);
                draw = discard.ToArray();
                drawCursor = 0;
                discard.Clear();
                shuffleRng = rng;
            }
            if (drawCursor >= draw.Length) break;
            SimCard drawn = draw[drawCursor++];
            hand.Add(drawn);
            value += 1.2;
            if (forecast.Length < 2048) forecast += $" draw:{drawn.Id}";
        }
        if (card.Generator != Generator.None && hand.Count < 10)
        {
            (SimCard[] generated, Rng nextRng) = PredictGenerated(card, generationRng, pools);
            generationRng = nextRng;
            foreach (SimCard generatedCard in generated)
            {
                if (hand.Count >= 10) break;
                hand.Add(generatedCard);
                value += 1.5;
                if (forecast.Length < 2048) forecast += $" rng:{generatedCard.Id}";
            }
        }
        if (card.Type != CardType.Power && !card.Exhaust) discard.Add(card);
        bool known = card.Damage > 0 || card.Block > 0 || card.Draw > 0
            || card.EnergyGain > 0 || card.DoubleEnergy || card.OrbCard
            || card.Type == CardType.Power || card.Generator != Generator.None;
        if (!known) value = Math.Max(0.25, value);
        if (value <= 0) return null;

        LocalMove first = node.First ?? new LocalMove(card.Model, target, value);
        double score = node.Score + value * Math.Pow(0.975, node.Depth);
        return new Node(hand.ToArray(), draw, drawCursor, discard.ToArray(),
            shuffleRng, generationRng, Math.Min(99, energy), node.Block + card.Block,
            hp, score, node.Depth + 1, first, forecast);

        double Deal(int i, double multiplier)
        {
            if (hp[i] <= 0) return 0;
            double dealt = Math.Min(hp[i], card.Damage);
            hp[i] = Math.Max(0, hp[i] - dealt);
            return dealt * multiplier + (hp[i] <= 0 ? 4 + incoming[i] * 0.5 : 0);
        }
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
        rng.Shuffle(choices); // CardFactory.GetDistinctForCombat shuffles the whole pool.
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
            Type? type = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "BetterDefect")?
                .GetType("BetterDefect.BdCardVersionUpgrades");
            MethodInfo? method = type?.GetMethod("IsVersionEnabled",
                BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(CardModel)], null);
            return method?.Invoke(null, [card]) is true;
        }
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
