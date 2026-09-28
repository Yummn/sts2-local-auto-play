using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.TestSupport;

namespace LocalAutoPlay;

/// <summary>
/// Supplies native CardSelectCmd choices only while one of our own card actions
/// is resolving.  Never touches the player's reward screens outside combat.
/// </summary>
internal sealed class AutoCardSelector(CardModel source) : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        CardModel[] candidates = options.ToArray();
        int limit = Math.Max(0, Math.Min(maxSelect, candidates.Length));
        if (limit == 0) return Task.FromResult<IEnumerable<CardModel>>([]);

        // A hand choice usually discards or exhausts; draw/discard choices
        // usually retrieve or play.  Recycle is the notable hand exception.
        bool hand = candidates.All(card => card.Pile?.Type == PileType.Hand);
        bool recycle = source.Id.Entry.Contains("RECYCLE", StringComparison.OrdinalIgnoreCase);
        Func<CardModel, double> value = recycle
            ? card => Math.Max(0, card.EnergyCost.GetAmountToSpend())
            : hand ? card => -LocalPlanner.ChoiceValue(card)
                   : ContextualChoiceValue;
        CardModel[] ordered = candidates.OrderByDescending(value).ToArray();
        int required = Math.Min(Math.Max(0, minSelect), limit);
        int count = required;
        // Optional selections may be skipped. Do not force a weak card into the
        // hand or voluntarily discard a valuable one merely because it exists.
        if (required == 0 && value(ordered[0]) > 0)
            count = 1;
        CardModel[] selected = ordered.Take(count).ToArray();
        MainFile.Log.Info($"[LocalAutoPlay] CHOICE source={source.Id.Entry} count={count} " +
            $"cards={string.Join(',', selected.Select(c => c.Id.Entry))}");
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    private double ContextualChoiceValue(CardModel card)
    {
        double score = LocalPlanner.ChoiceValue(card);
        if (!source.Id.Entry.Contains("SEEK", StringComparison.OrdinalIgnoreCase)
            || source.Owner.PlayerCombatState is not { } pcs)
            return score;

        // Fetching more energy has little value when the current hand can
        // already be paid for. This avoids selecting Turbo at 6+ energy while
        // useful attacks or draw cards remain in the draw pile.
        int handCost = pcs.Hand.Cards.Where(other => !ReferenceEquals(other, source))
            .Sum(other => other.EnergyCost.CostsX ? 0 : Math.Max(0, other.EnergyCost.GetAmountToSpend()));
        if (pcs.Energy >= handCost + 2
            && card.DynamicVars.TryGetValue("Energy", out DynamicVar? gain)
            && gain.BaseValue > 0)
            score -= 16;
        return score;
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        CardModel? best = options.Select(option => option.Card)
            .OrderByDescending(LocalPlanner.ChoiceValue).FirstOrDefault();
        CardRewardAlternative? skip = alternatives.FirstOrDefault(option =>
            option.OptionId.Equals("Skip", StringComparison.OrdinalIgnoreCase));
        if (best is null || (skip is not null && LocalPlanner.ChoiceValue(best) <= 0))
        {
            MainFile.Log.Info($"[LocalAutoPlay] REWARD_CHOICE source={source.Id.Entry} skip");
            return new CardRewardSelection { alternative = skip };
        }
        MainFile.Log.Info($"[LocalAutoPlay] REWARD_CHOICE source={source.Id.Entry} card={best?.Id.Entry ?? "none"}");
        return new CardRewardSelection { card = best };
    }
}
