using LocalAutoPlay;

static void Expect(string name, int expected, double[] immediate, double[] future)
{
    var result = ForecastTieBreaker.Choose(immediate, future, 0.12, 2.5);
    if (result.Index != expected)
        throw new Exception($"{name}: expected {expected}, got {result.Index}.");
    if (result.Adjustment is < -2.5 or > 0)
        throw new Exception($"{name}: adjustment out of range: {result.Adjustment}.");
    Console.WriteLine($"PASS {name}: index={result.Index} delta={result.Adjustment:0.0}");
}

Expect("future-breaks-near-tie", 1, [10, 9.5], [0, 30]);
Expect("current-gap-dominates", 0, [10, 7], [0, 100]);
Expect("equal-future-preserves-current", 0, [10, 9.5], [20, 20]);
Expect("absolute-offset-invariant-a", 1, [10, 9.5], [1000, 1030]);
Expect("absolute-offset-invariant-b", 1, [10, 9.5], [-100, -70]);
Expect("deterministic-equal-scores", 0, [10, 10], [20, 20]);
