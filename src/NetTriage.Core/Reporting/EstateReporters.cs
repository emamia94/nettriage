using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetTriage.Core.Analysis;
using NetTriage.Core.Estate;

namespace NetTriage.Core.Reporting;

internal static class Ansi
{
    public static string Wrap(string text, string code, bool enabled) => enabled ? $"\u001b[{code}m{text}\u001b[0m" : text;
    public static string Bold(string text, bool on) => Wrap(text, "1", on);
    public static string Dim(string text, bool on) => Wrap(text, "2", on);
    public static string Red(string text, bool on) => Wrap(text, "31", on);
    public static string Yellow(string text, bool on) => Wrap(text, "33", on);
    public static string Green(string text, bool on) => Wrap(text, "32", on);
    public static string Cyan(string text, bool on) => Wrap(text, "36", on);
}

/// <summary>The portfolio view: what to do first, across every application at once.</summary>
public sealed class EstateConsoleReporter
{
    private readonly bool _color;
    private readonly int _maxApps;

    public EstateConsoleReporter(bool color, int maxApps = 20)
    {
        _color = color;
        _maxApps = maxApps;
    }

    public string Render(EstateReport estate)
    {
        var sb = new StringBuilder();
        var s = estate.Summary;

        sb.AppendLine();
        sb.AppendLine(Ansi.Bold($"{ToolInfo.Name} {estate.ToolVersion}", _color) + Ansi.Dim("  ·  estate roll-up", _color));
        sb.AppendLine();
        sb.AppendLine($"  {Ansi.Dim("Root      ", _color)} {estate.Root}");
        sb.AppendLine($"  {Ansi.Dim("Scanned   ", _color)} {estate.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"  {Ansi.Dim("Estate    ", _color)} {s.AppCount} application(s)   ·   {s.Projects} project(s)   ·   {s.SourceLines:N0} source lines");
        sb.AppendLine();

        sb.AppendLine(Ansi.Bold("ESTATE", _color));
        sb.AppendLine($"  {Ansi.Dim("Blockers".PadRight(20), _color)} {Ansi.Red(s.Blockers.ToString(), _color)} across {Ansi.Red(s.RedApps.ToString(), _color)} application(s)");
        sb.AppendLine($"  {Ansi.Dim("Warnings".PadRight(20), _color)} {s.Warnings}");
        sb.AppendLine($"  {Ansi.Dim("Out of support".PadRight(20), _color)} {Ansi.Red(s.AppsOutOfSupport.ToString(), _color)} application(s) on a runtime that receives no security patches");

        if (s.EolTargetFrameworks.Count > 0)
        {
            sb.AppendLine($"  {Ansi.Dim("End-of-support targets".PadRight(20), _color)} {string.Join(", ", s.EolTargetFrameworks)}");
        }

        sb.AppendLine($"  {Ansi.Dim("Effort (heuristic)".PadRight(20), _color)} {s.EffortLowDays:N0} - {s.EffortHighDays:N0} engineer-days" +
                      $"   (≈ {EffortModel.Humanize(s.EffortLowDays)} to {EffortModel.Humanize(s.EffortHighDays)} for one engineer)");
        sb.AppendLine();

        sb.AppendLine(Ansi.Bold("RANKED", _color) + Ansi.Dim("  (worst first - this is the order to argue about)", _color));

        var rank = 1;
        foreach (var app in estate.Ranked.Take(_maxApps))
        {
            var verdict = app.Report.Summary.Red > 0
                ? Ansi.Red("REWRITE", _color)
                : app.Report.Summary.Yellow > 0
                    ? Ansi.Yellow("REFACTOR", _color)
                    : Ansi.Green("RETARGET", _color);

            sb.AppendLine();
            sb.AppendLine($"  {rank++,2}. {Ansi.Bold(app.Name, _color)}  {verdict}  {Ansi.Dim($"risk {app.RiskScore:N0}", _color)}");
            sb.AppendLine($"      {Ansi.Dim(app.Path, _color)}");
            sb.AppendLine($"      blockers {Ansi.Red(app.Blockers.ToString(), _color)}  ·  warnings {app.Warnings}  ·  " +
                          $"{app.Report.Summary.ProjectCount} project(s)  ·  {app.Report.Summary.TotalSourceLines:N0} lines  ·  " +
                          $"effort {app.Report.Summary.EffortLowDays:N0}-{app.Report.Summary.EffortHighDays:N0} d");
        }

        if (estate.Apps.Count > _maxApps)
        {
            sb.AppendLine();
            sb.AppendLine($"  {Ansi.Dim($"… and {estate.Apps.Count - _maxApps} more application(s)", _color)}");
        }

        sb.AppendLine();

        if (estate.Trend is { } trend)
        {
            sb.AppendLine(Ansi.Bold("TREND", _color) + Ansi.Dim($"  (since {trend.PreviousTakenAt.UtcDateTime:yyyy-MM-dd})", _color));
            var net = trend.NetBlockerChange;
            var netText = (net >= 0 ? "+" : "") + net;
            sb.AppendLine($"  {Ansi.Dim("Net blocker change".PadRight(20), _color)} " +
                          (net > 0 ? Ansi.Red(netText, _color) : net < 0 ? Ansi.Green(netText, _color) : netText));

            foreach (var entry in trend.Worsening.Take(5))
            {
                sb.AppendLine($"  {Ansi.Red("worse", _color)}  {entry.Name}  {Ansi.Dim($"{entry.BlockersBefore} -> {entry.BlockersNow} blocker(s)", _color)}");
            }

            foreach (var entry in trend.Improving.Take(5))
            {
                sb.AppendLine($"  {Ansi.Green("better", _color)} {entry.Name}  {Ansi.Dim($"{entry.BlockersBefore} -> {entry.BlockersNow} blocker(s)", _color)}");
            }

            sb.AppendLine();
        }

        foreach (var warning in estate.Warnings)
        {
            sb.AppendLine($"  {Ansi.Yellow("note", _color)} {warning}");
        }

        if (estate.Warnings.Count > 0) sb.AppendLine();

        sb.AppendLine(Ansi.Dim("  Risk score = blockers x10 + warnings x3 + 15 if a runtime is unsupported + effort/20 (capped).", _color));
        sb.AppendLine(Ansi.Dim("  Effort figures are a disclosed heuristic, not a quote.", _color));
        sb.AppendLine();

        return sb.ToString();
    }
}

public sealed class EstateMarkdownReporter
{
    public string Render(EstateReport estate)
    {
        var s = estate.Summary;
        var sb = new StringBuilder();

        sb.AppendLine("# Estate roll-up");
        sb.AppendLine();
        sb.AppendLine($"**{s.AppCount} application(s) · {s.Projects} project(s) · {s.SourceLines:N0} source lines**  ");
        sb.AppendLine($"Assessed {estate.GeneratedAt:yyyy-MM-dd HH:mm} UTC with {ToolInfo.Name} {estate.ToolVersion}.");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Applications | {s.AppCount} |");
        sb.AppendLine($"| Projects | {s.Projects} |");
        sb.AppendLine($"| Blocker findings | {s.Blockers} |");
        sb.AppendLine($"| Warning findings | {s.Warnings} |");
        sb.AppendLine($"| Applications with red projects | {s.RedApps} |");
        sb.AppendLine($"| Applications on an unsupported runtime | {s.AppsOutOfSupport} |");
        sb.AppendLine($"| End-of-support targets | {string.Join(", ", s.EolTargetFrameworks.DefaultIfEmpty("-"))} |");
        sb.AppendLine($"| Effort (heuristic) | {s.EffortLowDays:N0} – {s.EffortHighDays:N0} engineer-days |");
        sb.AppendLine();

        sb.AppendLine("## Ranked (worst first)");
        sb.AppendLine();
        sb.AppendLine("| # | Application | Blockers | Warnings | Projects | Lines | Effort (d) | Risk |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

        var rank = 1;
        foreach (var app in estate.Ranked)
        {
            sb.AppendLine($"| {rank++} | {app.Name} | {app.Blockers} | {app.Warnings} | " +
                          $"{app.Report.Summary.ProjectCount} | {app.Report.Summary.TotalSourceLines:N0} | " +
                          $"{app.Report.Summary.EffortLowDays:N0}–{app.Report.Summary.EffortHighDays:N0} | {app.RiskScore:N0} |");
        }

        sb.AppendLine();

        if (estate.Trend is { } trend)
        {
            sb.AppendLine("## Trend");
            sb.AppendLine();
            sb.AppendLine($"Compared with {trend.PreviousTakenAt:yyyy-MM-dd}. Net blocker change: **{(trend.NetBlockerChange >= 0 ? "+" : "")}{trend.NetBlockerChange}**.");
            sb.AppendLine();
            sb.AppendLine("| Application | Before | Now | Change |");
            sb.AppendLine("| --- | --- | --- | --- |");

            foreach (var entry in trend.Entries.OrderByDescending(e => e.BlockerDelta))
            {
                sb.AppendLine($"| {entry.Name} | {entry.BlockersBefore} | {entry.BlockersNow} | {(entry.BlockerDelta >= 0 ? "+" : "")}{entry.BlockerDelta} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Effort figures are a disclosed heuristic, not a quote. Analysis is lexical: it detects referenced APIs, not data flow.");

        return sb.ToString();
    }
}

public sealed class EstateJsonReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Render(EstateReport estate) => JsonSerializer.Serialize(estate, Options) + "\n";
}
