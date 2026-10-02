using NetTriage.Core.Estate;

namespace NetTriage.Core.Reporting;

/// <summary>
/// Turns a scan or an estate report into a workbook a lead can filter, sort and forward. This is
/// the artefact the licensed edition exists to produce: the CLI's markdown is for the engineer, the
/// spreadsheet is for the conversation with the person who approves the budget.
/// </summary>
public static class WorkbookBuilder
{
    public static List<XlsxSheet> ForScan(ScanReport report)
    {
        var sheets = new List<XlsxSheet> { ScanSummarySheet(report), ProjectsSheet(report), FindingsSheet(report), SequenceSheet(report) };

        if (report.BaselineDiff is not null) sheets.Add(BaselineSheet(report.BaselineDiff));
        if (report.Suppressed.Count > 0) sheets.Add(SuppressedSheet(report));

        return sheets;
    }

    public static List<XlsxSheet> ForEstate(EstateReport estate)
    {
        var sheets = new List<XlsxSheet> { EstateSummarySheet(estate), ApplicationsSheet(estate), EstateFindingsSheet(estate) };

        if (estate.Trend is not null) sheets.Add(TrendSheet(estate.Trend));

        return sheets;
    }

    // -- scan ----------------------------------------------------------------------------------

    private static XlsxSheet ScanSummarySheet(ScanReport report)
    {
        var s = report.Summary;
        var sheet = new XlsxSheet { Name = "Summary", ColumnWidths = { 34, 46 } };

        sheet.AddRow(Cell.Header("Metric"), Cell.Header("Value"));
        sheet.AddRow(Cell.S("Tool version"), Cell.S(report.ToolVersion));
        sheet.AddRow(Cell.S("Scanned"), Cell.S(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm 'UTC'")));
        sheet.AddRow(Cell.S("Root"), Cell.S(report.Root));
        sheet.AddRow(Cell.S("Projects"), Cell.N(s.ProjectCount));
        sheet.AddRow(Cell.S("Source files"), Cell.N(s.TotalSourceFiles));
        sheet.AddRow(Cell.S("Source lines"), Cell.N(s.TotalSourceLines));
        sheet.AddRow(Cell.S("On .NET Framework"), Cell.N(s.ProjectsOnDotNetFramework));
        sheet.AddRow(Cell.S("On modern .NET"), Cell.N(s.ProjectsOnModernDotNet));
        sheet.AddRow(Cell.S("Runtime unknown"), Cell.N(s.ProjectsOnUnknownRuntime));
        sheet.AddRow(Cell.S("Red projects"), Cell.N(s.Red));
        sheet.AddRow(Cell.S("Yellow projects"), Cell.N(s.Yellow));
        sheet.AddRow(Cell.S("Green projects"), Cell.N(s.Green));
        sheet.AddRow(Cell.S("Blocker findings"), Cell.N(s.BlockerFindings));
        sheet.AddRow(Cell.S("Warning findings"), Cell.N(s.WarningFindings));
        sheet.AddRow(Cell.S("Info findings"), Cell.N(s.InfoFindings));
        sheet.AddRow(Cell.S("Projects out of support"), Cell.N(s.ProjectsOutOfSupport));
        sheet.AddRow(Cell.S("End-of-support targets"), Cell.S(string.Join(", ", s.EolTargetFrameworks)));
        sheet.AddRow(Cell.S("Effort low (engineer-days)"), Cell.N(s.EffortLowDays));
        sheet.AddRow(Cell.S("Effort high (engineer-days)"), Cell.N(s.EffortHighDays));
        sheet.AddRow(Cell.S("Custom detectors"), Cell.N(report.CustomDetectors.Count));
        sheet.AddRow(Cell.S("Suppressed findings"), Cell.N(report.Suppressed.Count));

        sheet.AddRow(Cell.Empty, Cell.Empty);
        sheet.AddRow(Cell.S("Effort is a disclosed heuristic, not a quote."), Cell.Empty);
        sheet.AddRow(Cell.S("Analysis is lexical: it detects referenced APIs, not data flow."), Cell.Empty);

        return sheet;
    }

    private static XlsxSheet ProjectsSheet(ScanReport report)
    {
        var sheet = new XlsxSheet
        {
            Name = "Projects",
            ColumnWidths = { 26, 44, 9, 16, 16, 9, 9, 9, 9, 10, 10, 12, 12 },
        };

        sheet.AddRow(
            Cell.Header("Project"), Cell.Header("Path"), Cell.Header("Format"), Cell.Header("Frameworks"),
            Cell.Header("Kind"), Cell.Header("Bucket"), Cell.Header("Blockers"), Cell.Header("Warnings"),
            Cell.Header("Info"), Cell.Header("Files"), Cell.Header("Lines"),
            Cell.Header("Effort low (d)"), Cell.Header("Effort high (d)"));

        foreach (var p in report.Projects)
        {
            sheet.AddRow(
                Cell.S(p.Name),
                Cell.S(p.Path),
                Cell.S(p.Style.ToString()),
                Cell.S(string.Join(", ", p.TargetFrameworks)),
                Cell.S(string.Join(", ", p.Kinds)),
                Cell.S(p.Bucket.ToString()),
                Cell.N(p.Hits.Count(h => h.Detector.Severity == Severity.Blocker)),
                Cell.N(p.Hits.Count(h => h.Detector.Severity == Severity.Warning)),
                Cell.N(p.Hits.Count(h => h.Detector.Severity == Severity.Info)),
                Cell.N(p.SourceFiles),
                Cell.N(p.SourceLines),
                Cell.N(p.Effort.LowDays),
                Cell.N(p.Effort.HighDays));
        }

        return sheet;
    }

    private static XlsxSheet FindingsSheet(ScanReport report)
    {
        var sheet = new XlsxSheet
        {
            Name = "Findings",
            ColumnWidths = { 24, 10, 10, 14, 10, 34, 8, 46, 60 },
        };

        sheet.AddRow(
            Cell.Header("Project"), Cell.Header("Code"), Cell.Header("Severity"), Cell.Header("Category"),
            Cell.Header("Origin"), Cell.Header("Finding"), Cell.Header("Count"),
            Cell.Header("First location"), Cell.Header("Recommendation"));

        foreach (var project in report.Projects)
        {
            foreach (var hit in project.Hits
                         .OrderByDescending(h => h.Detector.Severity)
                         .ThenByDescending(h => h.Count))
            {
                var first = hit.Samples.FirstOrDefault();
                sheet.AddRow(
                    Cell.S(project.Name),
                    Cell.S(hit.Detector.Code),
                    Cell.S(hit.Detector.Severity.ToString()),
                    Cell.S(hit.Detector.Category),
                    Cell.S(hit.Detector.Origin),
                    Cell.S(hit.Detector.Title),
                    Cell.N(hit.Count),
                    Cell.S(first is null ? "" : $"{first.File}:{first.Line}"),
                    Cell.S(hit.Detector.Recommendation));
            }
        }

        foreach (var hit in report.PortfolioFindings)
        {
            sheet.AddRow(
                Cell.S("(portfolio)"), Cell.S(hit.Detector.Code), Cell.S(hit.Detector.Severity.ToString()),
                Cell.S(hit.Detector.Category), Cell.S(hit.Detector.Origin), Cell.S(hit.Detector.Title),
                Cell.N(hit.Count), Cell.S(""), Cell.S(hit.Detector.Recommendation));
        }

        return sheet;
    }

    private static XlsxSheet SequenceSheet(ScanReport report)
    {
        var sheet = new XlsxSheet { Name = "Sequence", ColumnWidths = { 7, 26, 58, 70 } };

        sheet.AddRow(Cell.Header("Order"), Cell.Header("Project"), Cell.Header("Reason"), Cell.Header("Action"));

        foreach (var step in report.Sequence)
        {
            sheet.AddRow(Cell.N(step.Order), Cell.S(step.Project), Cell.S(step.Reason), Cell.S(step.Action));
        }

        return sheet;
    }

    private static XlsxSheet BaselineSheet(Baseline.BaselineDiff diff)
    {
        var sheet = new XlsxSheet { Name = "Baseline", ColumnWidths = { 24, 10, 10, 10, 10, 9, 10, 10 } };

        sheet.AddRow(
            Cell.Header("Project"), Cell.Header("Code"), Cell.Header("Severity"), Cell.Header("Category"),
            Cell.Header("Baseline"), Cell.Header("Now"), Cell.Header("New"), Cell.Header("Resolved"));

        foreach (var delta in diff.Deltas
                     .OrderByDescending(d => d.NewCount)
                     .ThenByDescending(d => d.Severity)
                     .ThenBy(d => d.Project, StringComparer.OrdinalIgnoreCase))
        {
            sheet.AddRow(
                Cell.S(delta.Project), Cell.S(delta.Detector), Cell.S(delta.Severity.ToString()),
                Cell.S(delta.Category), Cell.N(delta.BaselineCount), Cell.N(delta.CurrentCount),
                Cell.N(delta.NewCount), Cell.N(delta.ResolvedCount));
        }

        return sheet;
    }

    private static XlsxSheet SuppressedSheet(ScanReport report)
    {
        var sheet = new XlsxSheet { Name = "Suppressed", ColumnWidths = { 24, 10, 60, 14 } };

        sheet.AddRow(Cell.Header("Project"), Cell.Header("Code"), Cell.Header("Reason"), Cell.Header("Entry"));

        foreach (var item in report.Suppressed)
        {
            sheet.AddRow(Cell.S(item.Project), Cell.S(item.Detector), Cell.S(item.Reason), Cell.S(item.Entry));
        }

        return sheet;
    }

    // -- estate --------------------------------------------------------------------------------

    private static XlsxSheet EstateSummarySheet(EstateReport estate)
    {
        var s = estate.Summary;
        var sheet = new XlsxSheet { Name = "Summary", ColumnWidths = { 34, 46 } };

        sheet.AddRow(Cell.Header("Metric"), Cell.Header("Value"));
        sheet.AddRow(Cell.S("Tool version"), Cell.S(estate.ToolVersion));
        sheet.AddRow(Cell.S("Taken"), Cell.S(estate.GeneratedAt.ToString("yyyy-MM-dd HH:mm 'UTC'")));
        sheet.AddRow(Cell.S("Estate root"), Cell.S(estate.Root));
        sheet.AddRow(Cell.S("Applications"), Cell.N(s.AppCount));
        sheet.AddRow(Cell.S("Projects"), Cell.N(s.Projects));
        sheet.AddRow(Cell.S("Source lines"), Cell.N(s.SourceLines));
        sheet.AddRow(Cell.S("Blocker findings"), Cell.N(s.Blockers));
        sheet.AddRow(Cell.S("Warning findings"), Cell.N(s.Warnings));
        sheet.AddRow(Cell.S("Applications with red projects"), Cell.N(s.RedApps));
        sheet.AddRow(Cell.S("Applications on an unsupported runtime"), Cell.N(s.AppsOutOfSupport));
        sheet.AddRow(Cell.S("Effort low (engineer-days)"), Cell.N(s.EffortLowDays));
        sheet.AddRow(Cell.S("Effort high (engineer-days)"), Cell.N(s.EffortHighDays));
        sheet.AddRow(Cell.S("End-of-support targets"), Cell.S(string.Join(", ", s.EolTargetFrameworks)));

        if (estate.Trend is { } trend)
        {
            sheet.AddRow(Cell.Empty, Cell.Empty);
            sheet.AddRow(Cell.S("Trend since"), Cell.S(trend.PreviousTakenAt.ToString("yyyy-MM-dd HH:mm 'UTC'")));
            sheet.AddRow(Cell.S("Net blocker change"), Cell.N(trend.NetBlockerChange));
            sheet.AddRow(Cell.S("Applications getting worse"), Cell.N(trend.Worsening.Count()));
            sheet.AddRow(Cell.S("Applications improving"), Cell.N(trend.Improving.Count()));
        }

        return sheet;
    }

    private static XlsxSheet ApplicationsSheet(EstateReport estate)
    {
        var sheet = new XlsxSheet
        {
            Name = "Applications",
            ColumnWidths = { 7, 26, 18, 34, 10, 11, 10, 10, 10, 12, 12, 11, 18 },
        };

        sheet.AddRow(
            Cell.Header("Rank"), Cell.Header("Application"), Cell.Header("Id"), Cell.Header("Path"),
            Cell.Header("Projects"), Cell.Header("Lines"), Cell.Header("Blockers"), Cell.Header("Warnings"),
            Cell.Header("Red projects"), Cell.Header("Effort low (d)"), Cell.Header("Effort high (d)"),
            Cell.Header("Risk score"), Cell.Header("End-of-support targets"));

        var rank = 1;
        foreach (var app in estate.Ranked)
        {
            sheet.AddRow(
                Cell.N(rank++),
                Cell.S(app.Name),
                Cell.S(app.Id),
                Cell.S(app.Path),
                Cell.N(app.Report.Summary.ProjectCount),
                Cell.N(app.Report.Summary.TotalSourceLines),
                Cell.N(app.Report.Summary.BlockerFindings),
                Cell.N(app.Report.Summary.WarningFindings),
                Cell.N(app.Report.Summary.Red),
                Cell.N(app.Report.Summary.EffortLowDays),
                Cell.N(app.Report.Summary.EffortHighDays),
                Cell.N(Math.Round(app.RiskScore, 1)),
                Cell.S(string.Join(", ", app.Report.Summary.EolTargetFrameworks)));
        }

        return sheet;
    }

    private static XlsxSheet EstateFindingsSheet(EstateReport estate)
    {
        var sheet = new XlsxSheet
        {
            Name = "Findings",
            ColumnWidths = { 22, 24, 10, 10, 12, 34, 8, 44 },
        };

        sheet.AddRow(
            Cell.Header("Application"), Cell.Header("Project"), Cell.Header("Code"), Cell.Header("Severity"),
            Cell.Header("Category"), Cell.Header("Finding"), Cell.Header("Count"), Cell.Header("First location"));

        foreach (var app in estate.Ranked)
        {
            foreach (var project in app.Report.Projects)
            {
                foreach (var hit in project.Hits
                             .OrderByDescending(h => h.Detector.Severity)
                             .ThenByDescending(h => h.Count))
                {
                    var first = hit.Samples.FirstOrDefault();
                    sheet.AddRow(
                        Cell.S(app.Name), Cell.S(project.Name), Cell.S(hit.Detector.Code),
                        Cell.S(hit.Detector.Severity.ToString()), Cell.S(hit.Detector.Category),
                        Cell.S(hit.Detector.Title), Cell.N(hit.Count),
                        Cell.S(first is null ? "" : $"{first.File}:{first.Line}"));
                }
            }
        }

        return sheet;
    }

    private static XlsxSheet TrendSheet(EstateTrend trend)
    {
        var sheet = new XlsxSheet { Name = "Trend", ColumnWidths = { 26, 13, 13, 9, 13, 13, 11 } };

        sheet.AddRow(
            Cell.Header("Application"), Cell.Header("Blockers before"), Cell.Header("Blockers now"),
            Cell.Header("Change"), Cell.Header("Effort before (d)"), Cell.Header("Effort now (d)"),
            Cell.Header("Effort change"));

        foreach (var entry in trend.Entries
                     .OrderByDescending(e => e.BlockerDelta)
                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            sheet.AddRow(
                Cell.S(entry.Name),
                Cell.N(entry.BlockersBefore),
                Cell.N(entry.BlockersNow),
                Cell.N(entry.BlockerDelta),
                Cell.N(entry.EffortHighBefore),
                Cell.N(entry.EffortHighNow),
                Cell.N(Math.Round(entry.EffortDelta, 1)));
        }

        return sheet;
    }
}
