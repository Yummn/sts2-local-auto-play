using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
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
        int count = Math.Clamp(Math.Max(minSelect, 1), 0, Math.Min(maxSelect, candidates.Length));
        if (count == 0)
            return Task.FromResult<IEnumerable<CardModel>>([]);

        // A hand choice usually discards or exhausts; draw/discard choices
        // usually retrieve or play.  Recycle is the notable hand exception.
        bool hand = candidates.All(card => card.Pile?.Type == PileType.Hand);
        bool recycle = source.Id.Entry.Contains("RECYCLE", StringComparison.OrdinalIgnoreCase);
        IEnumerable<CardModel> ordered = hand && !recycle
            ? candidates.OrderBy(LocalPlanner.ChoiceValue)
            : candidates.OrderByDescending(LocalPlanner.ChoiceValue);
        CardModel[] selected = ordered.Take(count).ToArray();
        MainFile.Log.Info($"[LocalAutoPlay] CHOICE source={source.Id.Entry} count={count} " +
            $"cards={string.Join(',', selected.Select(c => c.Id.Entry))}");
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        CardModel? best = options.Select(option => option.Card)
            .OrderByDescending(LocalPlanner.ChoiceValue).FirstOrDefault();
        MainFile.Log.Info($"[LocalAutoPlay] REWARD_CHOICE source={source.Id.Entry} card={best?.Id.Entry ?? "none"}");
        return new CardRewardSelection { card = best };
    }
}
