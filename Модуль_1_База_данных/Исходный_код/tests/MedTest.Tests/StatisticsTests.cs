using MedTest.Core;

namespace MedTest.Tests;

/// <summary>Пункт В: статистическая норма и отклонения на числах, посчитанных вручную.</summary>
public class StatisticsTests
{
    static readonly Indicator Glucose = new(1, "GLU", "Биохимия", "Глюкоза", "ммоль/л", 3.3m, 5.5m, 1m, 40m, 1, 1, true);
    static decimal[] D(params double[] v) => v.Select(x => (decimal)x).ToArray();

    [Fact]
    public void Mean_sample_deviation_median_and_norm()
    {
        // 2 4 4 4 5 5 7 9: μ = 5, Σ(x−μ)² = 32, σ = √(32/7), медиана = 4,5.
        var s = Statistics.Compute(D(2, 4, 4, 4, 5, 5, 7, 9));
        Assert.Equal(8, s.N);
        Assert.Equal(5, s.Mean, 10);
        Assert.Equal(Math.Sqrt(32.0 / 7), s.StdDev, 10);
        Assert.Equal(4.5, s.Median, 10);
        Assert.Equal(2, s.Min); Assert.Equal(9, s.Max);
        Assert.True(s.HasNorm);
        Assert.Equal(5 - 2 * Math.Sqrt(32.0 / 7), s.NormLow, 10);
        Assert.Equal(5 + 2 * Math.Sqrt(32.0 / 7), s.NormHigh, 10);
    }

    [Fact]
    public void Median_of_odd_sample_is_the_middle_value()
    {
        Assert.Equal(5, Statistics.Compute(D(9, 1, 5)).Median, 10);
    }

    [Fact]
    public void Counts_values_outside_the_reference_range()
    {
        // 3,0 ниже 3,3; 6,1 и 7,0 выше 5,5; 3,3 и 5,5 — границы, это норма.
        var s = Statistics.Compute(D(3.0, 3.3, 4.8, 5.5, 6.1, 7.0), Glucose);
        Assert.Equal(1, s.BelowRef);
        Assert.Equal(2, s.AboveRef);
        Assert.Equal(0.5, s.OutsideShare, 10);
    }

    [Theory]
    [InlineData(new double[0])]
    [InlineData(new[] { 5.0 })]
    [InlineData(new[] { 5.0, 6.0 })]
    [InlineData(new[] { 4.2, 4.2, 4.2, 4.2 })]
    public void No_statistical_norm_for_small_or_flat_sample(double[] values)
    {
        var s = Statistics.Compute(D(values));
        Assert.False(s.HasNorm);
        Assert.Null(Statistics.ZScore(5m, s));
    }

    [Fact]
    public void Z_score_and_atypical_value()
    {
        var s = Statistics.Compute(D(2, 4, 4, 4, 5, 5, 7, 9));
        double sd = Math.Sqrt(32.0 / 7);
        Assert.Equal((9 - 5) / sd, Statistics.ZScore(9m, s)!.Value, 10);
        Assert.Equal(0, Statistics.ZScore(5m, s)!.Value, 10);
        // 9,5: z ≈ 2,1 — нетипично; 9: z ≈ 1,87 — в пределах μ ± 2σ.
        Assert.True(Statistics.Assess(9.5m, Glucose, s).Atypical);
        Assert.False(Statistics.Assess(9m, Glucose, s).Atypical);
    }

    [Fact]
    public void Reference_deviation_to_the_nearest_bound()
    {
        var high = Statistics.Reference(6.6m, Glucose);
        Assert.Equal(RefStatus.Above, high.Status);
        Assert.Equal(1.1m, high.Delta);
        Assert.Equal(20, high.Percent!.Value, 6);
        Assert.Equal("Выше нормы", high.Title);

        var low = Statistics.Reference(2.97m, Glucose);
        Assert.Equal(RefStatus.Below, low.Status);
        Assert.Equal(0.33m, low.Delta);
        Assert.Equal(10, low.Percent!.Value, 6);

        var ok = Statistics.Reference(5.5m, Glucose);
        Assert.Equal(RefStatus.Normal, ok.Status);
        Assert.Equal(0m, ok.Delta);
    }

    [Fact]
    public void Sample_takes_latest_value_per_patient_or_every_value()
    {
        var t = new DateTime(2026, 1, 1);
        var points = new[]
        {
            new SamplePoint(1, 1, t, 4.0m), new SamplePoint(1, 1, t.AddDays(10), 6.0m),
            new SamplePoint(2, 1, t.AddDays(3), 5.0m),
            new SamplePoint(2, 2, t.AddDays(3), 120m), // другой показатель
        };
        Assert.Equal(new[] { 6.0m, 5.0m }, Statistics.Sample(points, 1, SampleMode.LatestPerPatient).OrderByDescending(v => v));
        Assert.Equal(3, Statistics.Sample(points, 1, SampleMode.AllMeasurements).Count);
    }

    [Theory]
    [InlineData("2000-03-10", "2026-03-09", 25)]
    [InlineData("2000-03-10", "2026-03-10", 26)]
    [InlineData("2008-02-29", "2026-02-27", 17)]
    public void Age_in_full_years(string birth, string day, int years) =>
        Assert.Equal(years, Ages.Years(DateOnly.Parse(birth), DateOnly.Parse(day)));

    [Theory]
    [InlineData(1, "1 год")] [InlineData(22, "22 года")] [InlineData(25, "25 лет")] [InlineData(11, "11 лет")] [InlineData(41, "41 год")] [InlineData(114, "114 лет")]
    public void Age_text(int years, string text) => Assert.Equal(text, Ages.Text(years));
}
