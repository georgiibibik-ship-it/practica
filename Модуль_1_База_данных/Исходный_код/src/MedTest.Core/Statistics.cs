namespace MedTest.Core;

/// <summary>Which values of an indicator form the statistical sample.</summary>
public enum SampleMode
{
    /// <summary>The latest value of every patient: one independent observation per person.</summary>
    LatestPerPatient,
    /// <summary>Every recorded value.</summary>
    AllMeasurements
}

/// <summary>Descriptive statistics of an indicator and its statistical norm μ ± 2σ.</summary>
public sealed record IndicatorStats(int N, double Mean, double StdDev, double Median, double Min, double Max, int BelowRef, int AboveRef)
{
    /// <summary>Smallest sample for which the statistical norm is computed.</summary>
    public const int MinimumSample = 3;
    /// <summary>The statistical norm exists: enough values and they are not all equal.</summary>
    public bool HasNorm => N >= MinimumSample && StdDev > 0;
    public double NormLow => Mean - 2 * StdDev;
    public double NormHigh => Mean + 2 * StdDev;
    /// <summary>Share of the sample outside the reference range, 0–1.</summary>
    public double OutsideShare => N == 0 ? 0 : (BelowRef + AboveRef) / (double)N;
}

public enum RefStatus { Normal, Below, Above }

/// <summary>Position of a value against the reference range: distance to the nearest bound and that distance in percent of the bound.</summary>
public sealed record RefDeviation(RefStatus Status, decimal Delta, double? Percent)
{
    public string Title => Status switch { RefStatus.Below => "Ниже нормы", RefStatus.Above => "Выше нормы", _ => "Норма" };
}

/// <summary>Assessment of one value: reference status and its z-score in the sample.</summary>
public sealed record Assessment(Indicator Indicator, decimal Value, RefDeviation Reference, double? Z)
{
    /// <summary>Further than two standard deviations from the sample mean.</summary>
    public bool Atypical => Z is { } z && Math.Abs(z) > 2;
}

public static class Statistics
{
    /// <summary>Values of one indicator that form the sample in the chosen mode.</summary>
    public static IReadOnlyList<decimal> Sample(IEnumerable<SamplePoint> points, int indicatorId, SampleMode mode)
    {
        var own = points.Where(p => p.IndicatorId == indicatorId);
        if (mode == SampleMode.AllMeasurements) return own.Select(p => p.Value).ToList();
        return own.GroupBy(p => p.PatientId)
            .Select(g => g.OrderByDescending(p => p.TakenAt).First().Value)
            .ToList();
    }

    /// <summary>n, mean, sample standard deviation (n − 1), median, min, max and the count outside the reference range.</summary>
    public static IndicatorStats Compute(IReadOnlyCollection<decimal> values, Indicator? indicator = null)
    {
        int n = values.Count;
        if (n == 0) return new(0, 0, 0, 0, 0, 0, 0, 0);
        var data = values.Select(v => (double)v).OrderBy(v => v).ToArray();
        double mean = data.Average();
        double sd = n < 2 ? 0 : Math.Sqrt(data.Sum(v => (v - mean) * (v - mean)) / (n - 1));
        // Values equal up to rounding noise have no spread.
        if (sd < 1e-12 * Math.Max(1, Math.Abs(mean))) sd = 0;
        double median = n % 2 == 1 ? data[n / 2] : (data[n / 2 - 1] + data[n / 2]) / 2;
        int below = indicator == null ? 0 : values.Count(v => v < indicator.RefMin);
        int above = indicator == null ? 0 : values.Count(v => v > indicator.RefMax);
        return new(n, mean, sd, median, data[0], data[^1], below, above);
    }

    /// <summary>(x − μ) / σ, or null when the sample has no statistical norm.</summary>
    public static double? ZScore(decimal value, IndicatorStats stats) =>
        stats.HasNorm ? ((double)value - stats.Mean) / stats.StdDev : null;

    /// <summary>Compares a value with the reference range of its indicator.</summary>
    public static RefDeviation Reference(decimal value, Indicator indicator)
    {
        if (value < indicator.RefMin) return new(RefStatus.Below, indicator.RefMin - value, Percent(indicator.RefMin - value, indicator.RefMin));
        if (value > indicator.RefMax) return new(RefStatus.Above, value - indicator.RefMax, Percent(value - indicator.RefMax, indicator.RefMax));
        return new(RefStatus.Normal, 0, 0);
    }

    static double? Percent(decimal delta, decimal bound) => bound == 0 ? null : (double)(delta / Math.Abs(bound) * 100);

    public static Assessment Assess(decimal value, Indicator indicator, IndicatorStats? stats) =>
        new(indicator, value, Reference(value, indicator), stats == null ? null : ZScore(value, stats));
}
