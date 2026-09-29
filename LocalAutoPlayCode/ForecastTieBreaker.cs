namespace LocalAutoPlay;

/// <summary>
/// Compares only relative, speculative future scores. The current-turn score
/// remains primary and the forecast can penalize a candidate by at most cap.
/// Kept independent of game types so the ranking rule can be unit-tested.
/// </summary>
internal static class ForecastTieBreaker
{
    public static (int Index, double Adjustment, double Score) Choose(
        IReadOnlyList<double> immediate, IReadOnlyList<double> future,
        double weight, double cap)
    {
        if (immediate.Count == 0 || immediate.Count != future.Count ||
            weight < 0 || cap < 0 || immediate.Any(x => !double.IsFinite(x)) ||
            future.Any(x => !double.IsFinite(x)))
            throw new ArgumentException("Invalid forecast comparison.");

        double bestFuture = future.Max();
        int bestIndex = 0;
        double bestAdjustment = 0;
        double bestScore = double.NegativeInfinity;
        for (int i = 0; i < immediate.Count; i++)
        {
            double adjustment = Math.Clamp((future[i] - bestFuture) * weight,
                -cap, 0);
            double score = immediate[i] + adjustment;
            if (score <= bestScore) continue;
            bestIndex = i;
            bestAdjustment = adjustment;
            bestScore = score;
        }
        return (bestIndex, bestAdjustment, bestScore);
    }
}
