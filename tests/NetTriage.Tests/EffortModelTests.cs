using NetTriage.Core;
using NetTriage.Core.Analysis;
using Xunit;

namespace NetTriage.Tests;

public class EffortModelTests
{
    private static ProjectReport Report(int lines, params (string Code, int Count)[] hits)
    {
        var report = new ProjectReport { Name = "P", SourceLines = lines };
        foreach (var (code, count) in hits)
        {
            var detector = DetectorCatalog.ById(code);
            Assert.NotNull(detector);
            report.Hits.Add(new DetectorHit(detector!, count, new List<Occurrence>()));
        }
        return report;
    }

    [Fact]
    public void EffortGrowsWithLineCount()
    {
        var small = EffortModel.Estimate(Report(5_000));
        var large = EffortModel.Estimate(Report(50_000));

        Assert.True(large.LowDays > small.LowDays);
        Assert.True(large.HighDays > small.HighDays);
    }

    [Fact]
    public void StructuralBlockersAddEffort()
    {
        var clean = EffortModel.Estimate(Report(20_000));
        var webforms = EffortModel.Estimate(Report(20_000, ("NT1001", 40)));

        Assert.True(webforms.LowDays > clean.LowDays);
    }

    [Fact]
    public void RepeatedOccurrencesAddAtADiminishingRate()
    {
        var once = EffortModel.Estimate(Report(20_000, ("NT1001", 1)));
        var many = EffortModel.Estimate(Report(20_000, ("NT1001", 5_000)));

        // More occurrences cost more, but nowhere near 5,000x more.
        Assert.True(many.LowDays > once.LowDays);
        Assert.True(many.LowDays < once.LowDays * 3);
    }

    [Fact]
    public void TinyProjectsStillCarryAMinimumEstimate()
    {
        var estimate = EffortModel.Estimate(Report(0));

        Assert.True(estimate.LowDays >= 1.0);
        Assert.True(estimate.HighDays > estimate.LowDays);
    }

    [Fact]
    public void HighEstimateAlwaysExceedsLowEstimate()
    {
        foreach (var lines in new[] { 0, 100, 10_000, 500_000 })
        {
            var estimate = EffortModel.Estimate(Report(lines, ("NT1002", 3)));
            Assert.True(estimate.HighDays > estimate.LowDays, $"lines={lines}");
        }
    }

    [Fact]
    public void RationaleDisclosesItsInputs()
    {
        var estimate = EffortModel.Estimate(Report(12_345, ("NT1001", 2)));

        Assert.Contains("source lines", estimate.Rationale);
        Assert.Contains("mechanical", estimate.Rationale);
        Assert.Contains("architectural", estimate.Rationale);
    }

    [Theory]
    [InlineData(0.5, "< 1 day")]
    [InlineData(3, "days")]
    [InlineData(40, "weeks")]
    [InlineData(400, "months")]
    public void HumanizeUsesSensibleUnits(double days, string expectedUnit)
    {
        Assert.Contains(expectedUnit, EffortModel.Humanize(days));
    }
}
