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
/// A bounded one-step heuristic.  Never executes card effects in a preview:
/// all values below are read-only estimates, and the game checks legality again
/// immediately before the real action is enqueued.
/// </summary>
internal static class LocalPlanner
{
    public static LocalMove? Choose(CombatState state, Player player)
    {
        var pcs = player.PlayerCombatState;
        if (pcs is null)
            return null;
        Creature[] enemies = state.HittableEnemies.Where(e => e.IsAlive).ToArray();
        if (enemies.Length == 0)
            return null;
        int incoming = Incoming(state, player, enemies);
        decimal blockMissing = Math.Max(0, incoming - player.Creature.Block);
        LocalMove? best = null;

        foreach (CardModel card in pcs.Hand.Cards.ToArray())
        {
            try
            {
                if (!card.CanPlay())
                    continue;
                if (card.TargetType == TargetType.AnyEnemy)
                {
                    foreach (Creature enemy in enemies)
                        Consider(card, enemy);
                }
                else if (card.TargetType == TargetType.AnyAlly)
                {
                    foreach (Creature ally in state.Allies.Where(a => a.IsAlive))
                        Consider(card, ally);
                }
                else if (card.CanPlayTargeting(null))
                    Consider(card, null);
            }
            catch (Exception ex)
            {
                MainFile.Log.Warn($"[LocalAutoPlay] skipped {card.Id.Entry}: {ex.Message}");
            }
        }
        return best;

        void Consider(CardModel card, Creature? target)
        {
            if (!card.CanPlayTargeting(target))
                return;
            double score = Score(card, target, enemies, pcs, blockMissing, incoming);
            if (score <= 0 || (best.HasValue && score <= best.Value.Score))
                return;
            best = new LocalMove(card, target, score);
        }
    }

    private static int Incoming(CombatState state, Player player, Creature[] enemies)
    {
        int total = 0;
        foreach (Creature enemy in enemies)
        {
            try
            {
                if (enemy.Monster is null)
                    continue;
                foreach (AttackIntent intent in enemy.Monster.NextMove.Intents.OfType<AttackIntent>())
                    total = Math.Min(999, total + Math.Max(0,
                        intent.GetTotalDamage([player.Creature], enemy)));
            }
            catch { /* Hidden or modded intents must not make the UI unusable. */ }
        }
        return total;
    }

    private static double Score(CardModel card, Creature? target, Creature[] enemies,
        PlayerCombatState pcs, decimal blockMissing, int incoming)
    {
        int cost = Math.Max(0, card.EnergyCost.GetAmountToSpend());
        if (card.EnergyCost.CostsX)
            cost = Math.Max(0, pcs.Energy);
        double damage = Math.Max(Amount(card, "Damage"), Amount(card, "OstyDamage"));
        double repeat = Math.Clamp(Amount(card, "Repeat"), 1, 8);
        double block = Amount(card, "Block");
        double draw = Amount(card, "Cards");
        double energy = Amount(card, "Energy");
        string name = card.GetType().Name;

        // Every game-playable card remains a candidate, including status/curse,
        // custom cards, and X-cost cards.  Unknown effects receive a small
        // positive fallback instead of being silently excluded.
        double score = card.Type is CardType.Status or CardType.Curse ? 0.25 : 1.0;
        if (card.Type == CardType.Attack)
        {
            // Attack text is not simulated; dynamic damage is only an estimate.
            double estimated = damage * repeat;
            if (card.TargetType == TargetType.AllEnemies)
                score += enemies.Sum(e => Math.Min(estimated, e.CurrentHp + e.Block)) * 0.85;
            else if (target is not null)
            {
                score += Math.Min(estimated, target.CurrentHp + target.Block);
                if (estimated >= target.CurrentHp + target.Block && estimated > 0)
                    score += 80; // Secure visible lethal before long-term value.
                if (target.Monster?.IntendsToAttack == true)
                    score += Math.Min(estimated, target.CurrentHp + target.Block) * 0.18;
            }
            if (estimated <= 0)
                score += 1; // Explicitly whitelisted orb-trigger attacks.
        }

        if (block > 0 && blockMissing > 0)
            score += Math.Min((double)blockMissing, block) * 1.5;
        if (draw > 0 && (pcs.DrawPile.Cards.Count + pcs.DiscardPile.Cards.Count) > 0)
            score += Math.Min(draw, 4) * (pcs.Hand.Cards.Count < 8 ? 3.2 : 1.0);
        if (energy > 0 && pcs.Hand.Cards.Count > 1)
            score += Math.Min(energy, 4) * 5;
        if (card.Type == CardType.Power)
            score += 9;
        if (card.EnergyCost.CostsX && pcs.Energy > 0)
            score += Math.Min(pcs.Energy, 6) * 2.0;
        if (name is "Zap" or "BallLightning" or "ColdSnap" or "Glacier")
            score += 5;
        if (name is "Dualcast" or "Barrage")
            score += 5;
        if (block > 0 && incoming <= 0)
            score -= Math.Min(block, 15) * 0.15;
        // Pure setup cards are useful before damage, but not when this fight is
        // effectively over or when the hand has nothing to spend their energy on.
        if (name is "Ftl" or "Coolheaded" or "SweepingBeam")
            score += 2;
        // A negative approximate score is not a legality ruling: still allow
        // the card after more useful plays have been exhausted.
        return Math.Max(0.1, score - cost * 0.75 + (cost == 0 ? 1.5 : 0));
    }

    internal static double ChoiceValue(CardModel card)
    {
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
            return card.DynamicVars.TryGetValue(key, out DynamicVar? value)
                ? Math.Max(0, (double)value.BaseValue)
                : 0;
        }
        catch { return 0; }
    }
}
