using System.Globalization;
using System.Text;
using NetTriage.Core.Analysis;
using NetTriage.Core.Estate;

namespace NetTriage.Core.Reporting;

/// <summary>
/// A one-page summary written for the person who approves the budget rather than the person who
/// runs the tool. Numbers first, no jargon, and the method and its limits stated on the same page.
/// </summary>
public static class ExecutiveSummary
{
    public static string ForEstate(EstateReport estate)
    {
        var s = estate.Summary;
        var sb = new StringBuilder();

        sb.AppendLine("# .NET modernization: portfolio assessment");
        sb.AppendLine();
        sb.AppendLine($"**{s.AppCount} application(s) · {s.Projects} project(s) · {s.SourceLines:N0} source lines**  ");
        sb.AppendLine($"Assessed {estate.GeneratedAt:yyyy-MM-dd} with {ToolInfo.Name} {estate.ToolVersion}. Offline, deterministic analysis - no code was executed.");
        sb.AppendLine();

        sb.AppendLine("## Where you stand");
        sb.AppendLine();

        if (s.Blockers == 0)
        {
            sb.AppendLine("- No hard blockers were found. The remaining work is retargeting and package upgrades.");
        }
        else
        {
            var worst = estate.Ranked.FirstOrDefault(a => a.Blockers > 0);
            sb.AppendLine($"- **{s.Blockers} hard blocker(s)** across {s.RedApps} application(s). These need architectural work, not a version bump.");
            if (worst is not null)
            {
                sb.AppendLine($"- The most affected application is **{worst.Name}**, with {worst.Blockers} blocker(s).");
            }
        }

        if (s.AppsOutOfSupport > 0)
        {
            sb.AppendLine($"- **{s.AppsOutOfSupport} application(s) already run a runtime that receives no security patches.** This is the part with a deadline attached.");
        }

        if (s.EolTargetFrameworks.Count > 0)
        {
            sb.AppendLine($"- End-of-support or about-to-expire targets in use: `{string.Join("`, `", s.EolTargetFrameworks)}`.");
        }

        sb.AppendLine($"- Estimated effort: **{s.EffortLowDays:N0} to {s.EffortHighDays:N0} engineer-days** for one engineer. Treat it as an order of magnitude, not a quote.");
        sb.AppendLine();

        sb.AppendLine("## Start here");
        sb.AppendLine();
        sb.AppendLine("Ranked by risk. The order matters: shared libraries go first so their consumers can follow.");
        sb.AppendLine();

        var rank = 1;
        foreach (var app in estate.Ranked.Take(10))
        {
            var verdict = app.Report.Summary.Red > 0
                ? "scope as a rewrite of the affected layers"
                : app.Report.Summary.Yellow > 0
                    ? "retarget plus targeted refactors"
                    : "mechanical retarget - a good pilot candidate";

            sb.AppendLine($"{rank++}. **{app.Name}** - {app.Blockers} blocker(s), {app.Warnings} warning(s), " +
                          $"{app.Report.Summary.ProjectCount} project(s), effort {app.Report.Summary.EffortLowDays:N0}-{app.Report.Summary.EffortHighDays:N0} days: {verdict}.");
        }

        if (estate.Apps.Count > 10)
        {
            sb.AppendLine();
            sb.AppendLine($"_({estate.Apps.Count - 10} further application(s) are in the attached workbook.)_");
        }

        sb.AppendLine();

        if (estate.Trend is { } trend)
        {
            sb.AppendLine("## Movement since the last assessment");
            sb.AppendLine();
            sb.AppendLine($"Compared with {trend.PreviousTakenAt:yyyy-MM-dd}.");
            sb.AppendLine();
            sb.AppendLine($"- Net blocker change: **{(trend.NetBlockerChange >= 0 ? "+" : "")}{trend.NetBlockerChange}**.");
            sb.AppendLine($"- {trend.Worsening.Count()} application(s) got worse, {trend.Improving.Count()} improved.");

            foreach (var entry in trend.Worsening.Take(3))
            {
                sb.AppendLine($"  - Worse: **{entry.Name}** ({entry.BlockersBefore} to {entry.BlockersNow} blockers).");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Method and limits");
        sb.AppendLine();
        sb.AppendLine("- The assessment is **static and lexical**: it reads project files and source text. Nothing is built, loaded or executed.");
        sb.AppendLine("- It finds **referenced APIs**, not data flow. A method with a matching name on your own type is indistinguishable from the framework one.");
        sb.AppendLine("- Effort is a **disclosed heuristic** (source volume plus per-finding structural baselines), deliberately shown as a range.");
        sb.AppendLine("- No network access, no telemetry, no account. The same input always produces the same output.");
        sb.AppendLine();
        sb.AppendLine("_Calibrate the estimate on one real application before committing to dates. That single data point is worth more than any model._");

        return sb.ToString();
    }

    public static string ForScan(ScanReport report)
    {
        var s = report.Summary;
        var sb = new StringBuilder();

        sb.AppendLine("# .NET modernization: assessment");
        sb.AppendLine();
        sb.AppendLine($"**{s.ProjectCount} project(s) · {s.TotalSourceLines:N0} source lines**  ");
        sb.AppendLine($"Assessed {report.GeneratedAt:yyyy-MM-dd} with {ToolInfo.Name} {report.ToolVersion}.");
        sb.AppendLine();

        sb.AppendLine("## Where you stand");
        sb.AppendLine();
        sb.AppendLine($"- **{s.BlockerFindings} hard blocker finding(s)**, {s.WarningFindings} warning(s), {s.InfoFindings} informational.");
        sb.AppendLine($"- {s.Red} project(s) need architectural work, {s.Yellow} need targeted refactors, {s.Green} are mechanical.");

        if (s.ProjectsOutOfSupport > 0)
        {
            sb.AppendLine($"- **{s.ProjectsOutOfSupport} project(s) target a runtime that receives no security patches.**");
        }

        if (s.EolTargetFrameworks.Count > 0)
        {
            sb.AppendLine($"- End-of-support targets in use: `{string.Join("`, `", s.EolTargetFrameworks)}`.");
        }

        sb.AppendLine($"- Estimated effort: **{s.EffortLowDays:N0} to {s.EffortHighDays:N0} engineer-days** for one engineer.");
        sb.AppendLine();

        if (report.BaselineDiff is { } diff)
        {
            sb.AppendLine("## Movement since the baseline");
            sb.AppendLine();
            sb.AppendLine($"- **{diff.NewOccurrences} new occurrence(s)** since the baseline, of which {diff.NewBlockers} are blockers.");
            sb.AppendLine($"- {diff.ResolvedOccurrences} occurrence(s) resolved.");
            sb.AppendLine();
        }

        var blockers = report.Projects
            .SelectMany(p => p.Hits.Select(h => (Project: p, Hit: h)))
            .Where(x => x.Hit.Detector.Severity == Severity.Blocker)
            .GroupBy(x => x.Hit.Detector.Code)
            .OrderByDescending(g => g.Count())
            .Take(8)
            .ToList();

        if (blockers.Count > 0)
        {
            sb.AppendLine("## What is blocking you");
            sb.AppendLine();
            foreach (var group in blockers)
            {
                var detector = group.First().Hit.Detector;
                sb.AppendLine($"- **{detector.Title}** ({detector.Code}) - {group.Count()} project(s). {detector.Recommendation}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Method and limits");
        sb.AppendLine();
        sb.AppendLine("- Static and lexical analysis: nothing is built, loaded or executed.");
        sb.AppendLine("- Effort is a disclosed heuristic, shown as a range. It is not a quote.");
        sb.AppendLine("- No network access, no telemetry, no account.");

        return sb.ToString();
    }
}
