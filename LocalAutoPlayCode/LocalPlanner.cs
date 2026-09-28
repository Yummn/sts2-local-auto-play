using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace LocalAutoPlay;

internal readonly record struct LocalMove(CardModel Card, Creature? Target, double Score);

/// <summary>
/// A bounded, side-effect-free approximation of the current turn. Only one
/// chosen card is actually played; we then plan again from the real game state.
/// </summary>
internal static class LocalPlanner
{
    private const int Depth = 3;
    private const int BeamWidth = 8;
    private const int MaxExpansions = 640;
    private const int MaxMilliseconds = 7;
    private const double FutureDiscount = 0.88;

    private sealed record Candidate(
        CardModel Card, Creature? Target, int CardIndex, int EnemyIndex,
        int Cost, bool CostsX, double Damage, double Block, double Draw,
        double EnergyGain, bool IsAttack, bool IsPower, bool HitsAll,
        bool IsOrbCard, bool IsDoubleEnergy, bool IsHazard, bool RootPlayable);

    private sealed record PlanState(
        ulong UsedCards, int Energy, int HandCount, int DeckRemaining,
        double Block, double[] EnemyHp, double Total, LocalMove? First);

    public static LocalMove? Choose(CombatState state, Player player)
    {
        PlayerCombatState? pcs = player.PlayerCombatState;
        if (pcs is null) return null;
        Creature[] enemies = state.HittableEnemies.Where(e => e.IsAlive).ToArray();
        if (enemies.Length == 0) return null;
        CardModel[] hand = pcs.Hand.Cards.ToArray();
        int[] incoming = enemies.Select(enemy => IncomingFrom(enemy, player)).ToArray();
        List<Candidate> candidates = new();

        for (int cardIndex = 0; cardIndex < Math.Min(hand.Length, 63); cardIndex++)
        {
            CardModel card = hand[cardIndex];
            try
            {
                bool playable = card.CanPlay(out UnplayableReason reason, out _);
                if (!playable && reason != UnplayableReason.EnergyCostTooHigh) continue;
                if (card.TargetType == TargetType.AnyEnemy)
                {
                    for (int enemyIndex = 0; enemyIndex < enemies.Length; enemyIndex++)
                        Add(card, enemies[enemyIndex], cardIndex, enemyIndex, playable);
                }
                else if (card.TargetType == TargetType.AnyAlly)
                {
                    foreach (Creature ally in state.Allies.Where(a => a.IsAlive))
                        Add(card, ally, cardIndex, -1, playable);
                }
                else Add(card, null, cardIndex, -1, playable);
            }
            catch (Exception ex)
            {
                MainFile.Log.Warn($"[LocalAutoPlay] skipped {card.Id.Entry}: {ex.Message}");
            }
        }
        if (candidates.Count == 0) return null;

        PlanState initial = new(0, Math.Max(0, pcs.Energy), hand.Length,
            pcs.DrawPile.Cards.Count + pcs.DiscardPile.Cards.Count,
            (double)player.Creature.Block,
            enemies.Select(e => (double)(e.CurrentHp + e.Block)).ToArray(), 0, null);
        List<PlanState> frontier = [initial];
        PlanState? best = null;
        Stopwatch timer = Stopwatch.StartNew();
        int expansions = 0;
        for (int depth = 0; depth < Depth && frontier.Count > 0; depth++)
        {
            List<PlanState> next = new();
            foreach (PlanState node in frontier)
            {
                foreach (Candidate candidate in candidates)
                {
                    // Root candidates are always evaluated. Later levels are
                    // bounded to avoid frame spikes on large modded hands.
                    if (depth >= 2 && (expansions >= MaxExpansions || timer.ElapsedMilliseconds >= MaxMilliseconds))
                        break;
                    PlanState? projected = Apply(node, candidate, incoming, (double)player.Creature.CurrentHp, depth);
                    if (projected is null) continue;
                    expansions++;
                    next.Add(projected);
                    if (best is null || projected.Total > best.Total)
                        best = projected;
                }
                if (depth >= 2 && (expansions >= MaxExpansions || timer.ElapsedMilliseconds >= MaxMilliseconds))
                    break;
            }
            frontier = next.OrderByDescending(x => x.Total).Take(BeamWidth).ToList();
        }
        return best?.First is { } first ? first with { Score = best.Total } : null;

        void Add(CardModel card, Creature? target, int cardIndex, int enemyIndex, bool playable)
        {
            if (!card.IsValidTarget(target)) return;
            string name = card.GetType().Name;
            bool orb = name is "Zap" or "BallLightning" or "ColdSnap" or "Glacier"
                or "Dualcast" or "Barrage" or "MultiCast" or "Recursion"
                or "Chaos" or "Rainbow" or "Tempest";
            candidates.Add(new Candidate(card, target, cardIndex, enemyIndex,
                Math.Max(0, card.EnergyCost.GetAmountToSpend()), card.EnergyCost.CostsX,
                Math.Max(Amount(card, "Damage"), Amount(card, "OstyDamage"))
                    * Math.Clamp(Amount(card, "Repeat"), 1, 8),
                Amount(card, "Block"), Amount(card, "Cards"), Amount(card, "Energy"),
                card.Type == CardType.Attack, card.Type == CardType.Power,
                card.TargetType == TargetType.AllEnemies, orb, name == "DoubleEnergy",
                card.Type is CardType.Status or CardType.Curse, playable));
        }
    }

    private static PlanState? Apply(PlanState state, Candidate move, int[] incoming, double playerHp, int depth)
    {
        ulong bit = 1UL << move.CardIndex;
        if ((state.UsedCards & bit) != 0 || move.IsHazard) return null;
        if (depth == 0 && !move.RootPlayable) return null;
        int cost = move.CostsX ? state.Energy : move.Cost;
        if (cost > state.Energy || state.EnemyHp.All(hp => hp <= 0)) return null;
        if (move.EnemyIndex >= 0 && state.EnemyHp[move.EnemyIndex] <= 0) return null;

        int beforeIncoming = RemainingIncoming(state.EnemyHp, incoming);
        int energyAfterCost = state.Energy - cost;
        double[] hp = (double[])state.EnemyHp.Clone();
        double value = 0.15;
        if (move.Damage > 0)
        {
            if (move.HitsAll)
            {
                for (int i = 0; i < hp.Length; i++) value += Deal(i, 0.85);
            }
            else if (move.EnemyIndex >= 0)
                value += Deal(move.EnemyIndex, 1.0);
            else if (move.IsAttack)
            {
                // Random-target and special attacks are estimated conservatively.
                int target = Array.FindIndex(hp, x => x > 0);
                if (target >= 0) value += Deal(target, 0.6);
            }
        }
        if (hp.All(x => x <= 0) && state.EnemyHp.Any(x => x > 0)) value += 14;

        int afterIncoming = RemainingIncoming(hp, incoming);
        double blockNeeded = Math.Max(0, afterIncoming - state.Block);
        double usefulBlock = Math.Min(blockNeeded, move.Block);
        double defenseWeight = afterIncoming - state.Block >= playerHp ? 2.5 : 1.5;
        value += usefulBlock * defenseWeight;
        if (move.Block > usefulBlock) value += Math.Min(move.Block - usefulBlock, 10) * 0.03;

        int freeHandSlots = Math.Max(0, 10 - Math.Max(0, state.HandCount - 1));
        int drawn = (int)Math.Min(Math.Min(move.Draw, state.DeckRemaining), freeHandSlots);
        if (drawn > 0)
        {
            // Draw before spending energy can expose additional actions.
            value += drawn * (1.5 + Math.Min(3, energyAfterCost) * 0.65);
            if (energyAfterCost > 0) value += Math.Min(drawn, 3) * 0.7;
        }
        int otherCards = Math.Max(0, state.HandCount - 1);
        int energyGained = move.IsDoubleEnergy ? energyAfterCost
            : (int)Math.Clamp(move.EnergyGain, 0, 9);
        if (energyGained > 0 && (otherCards > 0 || drawn > 0))
            value += Math.Min(energyGained, 4) * 2.1;
        if (move.IsPower)
            value += hp.Any(x => x > 0) ? 9 + (otherCards > 0 ? 1.5 : 0) : 0;
        if (move.IsOrbCard) value += 4;
        if (move.IsAttack && move.Damage <= 0 && !move.IsOrbCard) value += 0.7;
        bool known = move.Damage > 0 || move.Block > 0 || move.Draw > 0
            || move.EnergyGain > 0 || move.IsPower || move.IsOrbCard || move.IsDoubleEnergy;
        value -= cost * 0.45;
        // Unknown custom effects remain eligible, but cannot outrank an
        // obviously valuable action simply by spending more energy.
        if (!known) value = Math.Max(value, 0.25);
        if (value <= 0) return null;

        double total = state.Total + value * Math.Pow(FutureDiscount, depth);
        LocalMove first = state.First ?? new LocalMove(move.Card, move.Target, total);
        return new PlanState(state.UsedCards | bit,
            Math.Min(99, energyAfterCost + energyGained),
            Math.Max(0, state.HandCount - 1 + drawn),
            Math.Max(0, state.DeckRemaining - drawn), state.Block + move.Block,
            hp, total, first);

        double Deal(int index, double multiplier)
        {
            if (hp[index] <= 0) return 0;
            double applied = Math.Min(hp[index], move.Damage);
            hp[index] = Math.Max(0, hp[index] - applied);
            double result = applied * multiplier;
            if (hp[index] <= 0)
                result += 4 + Math.Min(incoming[index], beforeIncoming) * 0.5;
            else if (incoming[index] > 0)
                result += applied * 0.12;
            return result;
        }
    }

    private static int RemainingIncoming(double[] hp, int[] incoming)
    {
        int total = 0;
        for (int i = 0; i < hp.Length; i++)
            if (hp[i] > 0) total = Math.Min(999, total + incoming[i]);
        return total;
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
        catch { /* Hidden or modded intents must not make the UI unusable. */ }
        return total;
    }

    internal static double ChoiceValue(CardModel card)
    {
        if (card.Type is CardType.Status or CardType.Curse) return -12;
        double damage = Math.Max(Amount(card, "Damage"), Amount(card, "OstyDamage"));
        double block = Amount(card, "Block");
        double draw = Amount(card, "Cards");
        double energy = Amount(card, "Energy");
        double cost = card.EnergyCost.CostsX ? 2 : Math.Max(0, card.EnergyCost.GetAmountToSpend());
        return damage + block * 0.65 + draw * 3 + energy * 4
            + (card.Type == CardType.Power ? 7 : 0) - cost * 1.5;
    }

    private static double Amount(CardModel card, string key)
    {
        try
        {
            if (!card.DynamicVars.TryGetValue(key, out DynamicVar? value)) return 0;
            decimal amount = value is DamageVar or BlockVar ? value.PreviewValue : value.BaseValue;
            return Math.Max(0, (double)amount);
        }
        catch { return 0; }
    }
}
