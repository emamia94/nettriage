namespace NetTriage.Core.Analysis;

/// <summary>
/// A deliberately simple, fully disclosed heuristic. It is an order-of-magnitude aid for planning
/// and prioritisation - it is NOT a quote, and it is not calibrated against your team.
///
/// The model is additive:
///   mechanical  = source lines / 1500          (retargeting, package fixes, mechanical rewrites)
///   architectural = sum of detector baselines  (each weighted by how often it appears)
///   range       = 0.7x to 1.6x the sum
///
/// The 1500 lines/day figure is a planning constant for one engineer working on a codebase they do
/// not yet know, including test updates. Calibrate it with a pilot project before anyone commits to
/// a date.
/// </summary>
public static class EffortModel
{
    public const double LinesPerEngineerDay = 1500.0;

    public static EffortEstimate Estimate(ProjectReport report)
    {
        double mechanical = report.SourceLines / LinesPerEngineerDay;

        // An architectural rewrite cost scales with how much application there is to rewrite.
        // Without this, a 50-line WebForms demo would be priced like a 200-page one.
        double sizeFactor = 0.35 + 0.65 * Math.Min(1.0, report.SourceLines / 5000.0);
        double architectural = 0;

        foreach (var hit in report.Hits)
        {
            var baseline = hit.Detector.EffortDays;
            if (baseline <= 0) continue;

            // The first occurrence carries the baseline cost; repetition adds work at a
            // diminishing rate rather than scaling linearly.
            var repeat = Math.Min(2.5, 1.0 + Math.Log(1.0 + hit.Count) / 4.0);
            architectural += baseline * repeat * sizeFactor;
        }

        double total = mechanical + architectural;
        if (total < 2.0) total = 2.0;

        double low = total * 0.7;
        double high = total * 1.6;

        var rationale =
            $"{report.SourceLines:N0} source lines in {report.SourceFiles} files " +
            $"({mechanical:F1} engineer-days mechanical at {LinesPerEngineerDay:N0} lines/day) " +
            $"+ {architectural:F1} engineer-days of architectural work across " +
            $"{report.Hits.Count(h => h.Detector.EffortDays > 0)} structural item(s), " +
            $"size-scaled by {sizeFactor:F2}.";

        return new EffortEstimate(Math.Round(low, 1), Math.Round(high, 1), rationale);
    }

    public static string Humanize(double days)
    {
        if (days < 1) return "< 1 day";
        if (days < 10) return $"{days:F0} days";
        if (days < 45) return $"{days / 5:F0} weeks";
        return $"{days / 21:F0} months";
    }
}
