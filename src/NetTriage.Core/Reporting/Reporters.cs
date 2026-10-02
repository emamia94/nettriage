using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetTriage.Core.Analysis;

namespace NetTriage.Core.Reporting;

public interface IReporter
{
    string Render(ScanReport report);
    string FileExtension { get; }
}

public static class ReporterFactory
{
    public static IReporter Create(string format, bool color) => format.ToLowerInvariant() switch
    {
        "json" => new JsonReporter(),
        "markdown" or "md" => new MarkdownReporter(),
        "html" => new HtmlReporter(),
        _ => new ConsoleReporter(color),
    };

    public static readonly string[] SupportedFormats = { "console", "json", "markdown", "html" };
}

// ---------------------------------------------------------------------------------------------
// Console
// ---------------------------------------------------------------------------------------------

public sealed class ConsoleReporter : IReporter
{
    private readonly bool _color;
    private readonly int _maxFindings;

    public ConsoleReporter(bool color, int maxFindings = 6)
    {
        _color = color;
        _maxFindings = maxFindings;
    }

    public string FileExtension => ".txt";

    private string Paint(string text, string code) => _color ? $"\u001b[{code}m{text}\u001b[0m" : text;
    private string Bold(string t) => Paint(t, "1");
    private string Dim(string t) => Paint(t, "2");
    private string Red(string t) => Paint(t, "31;1");
    private string Yellow(string t) => Paint(t, "33;1");
    private string Green(string t) => Paint(t, "32;1");
    private string Cyan(string t) => Paint(t, "36");

    private string BucketLabel(Bucket bucket) => bucket switch
    {
        Bucket.Red => Red("RED   "),
        Bucket.Yellow => Yellow("YELLOW"),
        _ => Green("GREEN "),
    };

    public string Render(ScanReport report)
    {
        var sb = new StringBuilder();
        var s = report.Summary;

        sb.AppendLine();
        sb.AppendLine(Bold($"{ToolInfo.Name} {report.ToolVersion}") + Dim($"  ·  {ToolInfo.Tagline}"));
        sb.AppendLine();
        sb.AppendLine($"  {Dim("Root      ")} {report.Root}");
        sb.AppendLine($"  {Dim("Scanned   ")} {report.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"  {Dim("Projects  ")} {s.ProjectCount}   ·   {s.TotalSourceLines:N0} source lines in {s.TotalSourceFiles:N0} files");
        sb.AppendLine($"  {Dim("Mode      ")} offline, deterministic (no network calls)");

        if (report.CustomDetectors.Count > 0)
        {
            sb.AppendLine($"  {Dim("Rules     ")} {report.CustomDetectors.Count} custom detector(s) applied");
        }

        sb.AppendLine();

        // -- portfolio ------------------------------------------------------------------
        sb.AppendLine(Bold("PORTFOLIO"));
        sb.AppendLine($"  {Dim("Runtime split".PadRight(20))} .NET Framework {s.ProjectsOnDotNetFramework}  ·  modern .NET {s.ProjectsOnModernDotNet}" +
                      (s.ProjectsOnUnknownRuntime > 0 ? $"  ·  unknown {s.ProjectsOnUnknownRuntime}" : ""));
        sb.AppendLine($"  {Dim("Triage".PadRight(20))} {Red("RED " + s.Red)}  ·  {Yellow("YELLOW " + s.Yellow)}  ·  {Green("GREEN " + s.Green)}");
        sb.AppendLine($"  {Dim("Findings".PadRight(20))} blockers {s.BlockerFindings}  ·  warnings {s.WarningFindings}  ·  info {s.InfoFindings}");

        if (s.ProjectsOutOfSupport > 0)
        {
            sb.AppendLine($"  {Dim("Out of support".PadRight(20))} {Red(s.ProjectsOutOfSupport.ToString())} project(s) target a runtime that receives no security patches");
        }

        if (s.EolTargetFrameworks.Count > 0)
        {
            sb.AppendLine($"  {Dim("End-of-support targets".PadRight(20))} {string.Join(", ", s.EolTargetFrameworks)}");
        }

        sb.AppendLine($"  {Dim("Effort (heuristic)".PadRight(20))} {s.EffortLowDays:N0} - {s.EffortHighDays:N0} engineer-days" +
                      $"   (≈ {EffortModel.Humanize(s.EffortLowDays)} to {EffortModel.Humanize(s.EffortHighDays)} for one engineer)");
        sb.AppendLine();

        // -- drift since the baseline -------------------------------------------------------
        if (report.BaselineDiff is { } diff)
        {
            sb.AppendLine(Bold("SINCE BASELINE") +
                          Dim($"  (recorded {diff.Baseline.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC)"));
            sb.AppendLine($"  {Dim("New".PadRight(20))} {Red(diff.NewBlockers + " blocker(s)")}  ·  " +
                          $"{Yellow(diff.NewWarnings + " warning(s)")}  ·  {diff.NewOccurrences} new occurrence(s)");
            sb.AppendLine($"  {Dim("Resolved".PadRight(20))} {Green(diff.ResolvedOccurrences + " occurrence(s)")}");
            sb.AppendLine();

            if (diff.Regressions.Any())
            {
                sb.AppendLine(Bold("REGRESSIONS") + Dim("  (absent from the baseline - this is what a drift gate fails on)"));
                foreach (var delta in diff.Regressions.Take(12))
                {
                    var marker = delta.Severity switch
                    {
                        Severity.Blocker => Red("B"),
                        Severity.Warning => Yellow("W"),
                        _ => Dim("i"),
                    };
                    sb.AppendLine($"  {marker} {Cyan(delta.Detector)}  {delta.Project}  " +
                                  $"{Yellow("+" + delta.NewCount)}  {Dim($"(baseline {delta.BaselineCount} -> now {delta.CurrentCount})")}");
                }

                if (diff.Regressions.Count() > 12)
                {
                    sb.AppendLine($"  {Dim($"… and {diff.Regressions.Count() - 12} more")}");
                }

                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"  {Green("No regressions")} {Dim("- nothing new since the baseline.")}");
                sb.AppendLine();
            }
        }

        // -- suppressed by rules -------------------------------------------------------------
        if (report.Suppressed.Count > 0)
        {
            sb.AppendLine(Bold("SUPPRESSED BY RULES") +
                          Dim($"  ({report.Suppressed.Count} finding(s) removed by the allowlist)"));
            foreach (var item in report.Suppressed.Take(10))
            {
                sb.AppendLine($"  {Dim("·")} {item.Detector}  {item.Project}  {Dim("- " + item.Reason)}");
            }

            if (report.Suppressed.Count > 10)
            {
                sb.AppendLine($"  {Dim($"… and {report.Suppressed.Count - 10} more")}");
            }

            sb.AppendLine();
        }

        // -- blockers across the estate ---------------------------------------------------
        var blockers = report.Projects
            .SelectMany(p => p.Hits.Select(h => (Project: p, Hit: h)))
            .Where(x => x.Hit.Detector.Severity == Severity.Blocker)
            .GroupBy(x => x.Hit.Detector)
            .OrderByDescending(g => g.Select(x => x.Project.Name).Distinct().Count())
            .ThenBy(g => g.Key.Code, StringComparer.Ordinal)
            .ToList();

        if (blockers.Count > 0)
        {
            sb.AppendLine(Bold("HARD BLOCKERS") + Dim("  (no supported equivalent - scope these as rewrites)"));
            foreach (var group in blockers)
            {
                var projects = group.Select(x => x.Project.Name).Distinct().Count();
                sb.AppendLine($"  {Cyan(group.Key.Code)}  {group.Key.Title.PadRight(46)} {Red(projects.ToString())} project(s)");
            }
            sb.AppendLine();
        }

        // -- projects ---------------------------------------------------------------------
        sb.AppendLine(Bold("PROJECTS"));
        foreach (var project in report.Projects.OrderBy(p => p.Bucket).ThenByDescending(p => p.BlockerCount).ThenBy(p => p.Path))
        {
            var tfm = project.TargetFrameworks.Count > 0 ? string.Join(", ", project.TargetFrameworks) : "?";
            var kinds = project.Kinds.Count > 0 ? string.Join(", ", project.Kinds) : "-";

            sb.AppendLine();
            sb.AppendLine($"  {BucketLabel(project.Bucket)}  {Bold(project.Name)}");
            sb.AppendLine($"          {Dim(project.Path)}");
            sb.AppendLine($"          {Dim(tfm)}  ·  {kinds}  ·  {project.SourceLines:N0} LOC  ·  " +
                          $"{project.Effort.LowDays:N0}-{project.Effort.HighDays:N0} engineer-days");

            if (project.TargetFrameworkSource is "Directory.Build.props" or "Directory.Build.targets")
            {
                sb.AppendLine($"          {Dim($"target framework inherited from {project.TargetFrameworkSource}")}");
            }

            if (project.Style == ProjectStyle.Legacy)
            {
                sb.AppendLine($"          {Dim("legacy project format")}");
            }

            foreach (var note in project.EolNotes)
            {
                var isBad = note.Contains("out of support", StringComparison.OrdinalIgnoreCase) ||
                            note.Contains("end of support ", StringComparison.OrdinalIgnoreCase);
                sb.AppendLine($"          {(isBad ? Red("!") : Dim("·"))} {note}");
            }

            foreach (var hit in project.Hits.Take(_maxFindings))
            {
                var marker = hit.Detector.Severity switch
                {
                    Severity.Blocker => Red("B"),
                    Severity.Warning => Yellow("W"),
                    _ => Dim("i"),
                };
                var where = hit.Samples.Count > 0 ? $"  {Dim(hit.Samples[0].File + ":" + hit.Samples[0].Line)}" : "";
                sb.AppendLine($"          {marker} {Cyan(hit.Detector.Code)} {hit.Detector.Title}  {Dim("×" + hit.Count)}{where}");
            }

            var hidden = project.Hits.Count - _maxFindings;
            if (hidden > 0) sb.AppendLine($"          {Dim($"… and {hidden} more finding(s)")}");
        }

        // -- sequence ---------------------------------------------------------------------
        if (report.Sequence.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Bold("SUGGESTED SEQUENCE") + Dim("  (shared libraries first, consumers after)"));
            foreach (var step in report.Sequence)
            {
                sb.AppendLine($"  {Dim(step.Order.ToString().PadLeft(2) + ".")} {Bold(step.Project)}");
                sb.AppendLine($"      {step.Reason}");
                sb.AppendLine($"      {Dim("→")} {step.Action}");
            }
        }

        // -- portfolio findings -----------------------------------------------------------
        if (report.PortfolioFindings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Bold("ESTATE-LEVEL"));
            foreach (var hit in report.PortfolioFindings)
            {
                sb.AppendLine($"  {Cyan(hit.Detector.Code)} {hit.Detector.Title}");
                sb.AppendLine($"      {hit.Detector.Recommendation}");
            }
        }

        // -- warnings ---------------------------------------------------------------------
        if (report.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Bold("SCAN NOTES"));
            foreach (var warning in report.Warnings.Distinct())
            {
                sb.AppendLine($"  {Dim("·")} {warning}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(Dim("  Effort figures are a disclosed heuristic, not a quote. Calibrate on a pilot before"));
        sb.AppendLine(Dim("  committing to dates. Analysis is lexical: it detects referenced APIs, not data flow."));
        sb.AppendLine();

        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------------------------
// JSON
// ---------------------------------------------------------------------------------------------

public sealed class JsonReporter : IReporter
{
    public string FileExtension => ".json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Render(ScanReport report) => JsonSerializer.Serialize(report, Options);
}

// ---------------------------------------------------------------------------------------------
// Markdown
// ---------------------------------------------------------------------------------------------

public sealed class MarkdownReporter : IReporter
{
    public string FileExtension => ".md";

    public string Render(ScanReport report)
    {
        var sb = new StringBuilder();
        var s = report.Summary;

        sb.AppendLine("# .NET modernization triage");
        sb.AppendLine();
        sb.AppendLine($"- **Root:** `{report.Root}`");
        sb.AppendLine($"- **Generated:** {report.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"- **Tool:** {ToolInfo.Name} {report.ToolVersion} (offline, deterministic)");
        sb.AppendLine($"- **Projects:** {s.ProjectCount} · **Source:** {s.TotalSourceLines:N0} lines across {s.TotalSourceFiles:N0} files");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Projects on .NET Framework | {s.ProjectsOnDotNetFramework} |");
        sb.AppendLine($"| Projects on modern .NET | {s.ProjectsOnModernDotNet} |");
        sb.AppendLine($"| Triage (red / yellow / green) | {s.Red} / {s.Yellow} / {s.Green} |");
        sb.AppendLine($"| Findings (blocker / warning / info) | {s.BlockerFindings} / {s.WarningFindings} / {s.InfoFindings} |");
        sb.AppendLine($"| Projects on an unsupported runtime | {s.ProjectsOutOfSupport} |");
        sb.AppendLine($"| End-of-support target frameworks | {string.Join(", ", s.EolTargetFrameworks.DefaultIfEmpty("-"))} |");
        sb.AppendLine($"| Effort (heuristic) | {s.EffortLowDays:N0} – {s.EffortHighDays:N0} engineer-days " +
                      $"(≈ {EffortModel.Humanize(s.EffortLowDays)} to {EffortModel.Humanize(s.EffortHighDays)} for one engineer) |");
        sb.AppendLine();

        if (report.Projects.Any(p => p.Hits.Any(h => h.Detector.Severity == Severity.Blocker)))
        {
            sb.AppendLine("## Hard blockers");
            sb.AppendLine();
            sb.AppendLine("| Code | Item | Projects affected |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var group in report.Projects
                         .SelectMany(p => p.Hits.Select(h => (Project: p, Hit: h)))
                         .Where(x => x.Hit.Detector.Severity == Severity.Blocker)
                         .GroupBy(x => x.Hit.Detector)
                         .OrderByDescending(g => g.Select(x => x.Project.Name).Distinct().Count()))
            {
                var count = group.Select(x => x.Project.Name).Distinct().Count();
                sb.AppendLine($"| `{group.Key.Code}` | {group.Key.Title} | {count} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Projects");
        sb.AppendLine();
        foreach (var project in report.Projects.OrderBy(p => p.Bucket).ThenByDescending(p => p.BlockerCount))
        {
            var tfm = project.TargetFrameworks.Count > 0 ? string.Join(", ", project.TargetFrameworks) : "unknown";
            sb.AppendLine($"### {project.Name} — {project.Bucket.ToString().ToUpperInvariant()}");
            sb.AppendLine();
            sb.AppendLine($"- `{project.Path}`");
            sb.AppendLine($"- Target: `{tfm}` · Kind: {string.Join(", ", project.Kinds.DefaultIfEmpty("-"))}");
            sb.AppendLine($"- {project.SourceLines:N0} lines in {project.SourceFiles} files · project format: {project.Style}");
            sb.AppendLine($"- Effort: **{project.Effort.LowDays:N0} – {project.Effort.HighDays:N0} engineer-days** — {project.Effort.Rationale}");
            sb.AppendLine();

            if (project.EolNotes.Count > 0)
            {
                sb.AppendLine("Lifecycle:");
                sb.AppendLine();
                foreach (var note in project.EolNotes) sb.AppendLine($"- {note}");
                sb.AppendLine();
            }

            if (project.Hits.Count > 0)
            {
                sb.AppendLine("| Severity | Code | Finding | Refs | First seen |");
                sb.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (var hit in project.Hits)
                {
                    var first = hit.Samples.Count > 0 ? $"`{hit.Samples[0].File}:{hit.Samples[0].Line}`" : "-";
                    sb.AppendLine($"| {hit.Detector.Severity} | `{hit.Detector.Code}` | {hit.Detector.Title} | {hit.Count} | {first} |");
                }
                sb.AppendLine();
                sb.AppendLine("Recommendations:");
                sb.AppendLine();
                foreach (var hit in project.Hits.Where(h => h.Detector.Severity != Severity.Info))
                {
                    sb.AppendLine($"- **`{hit.Detector.Code}` {hit.Detector.Title}** — {hit.Detector.Recommendation}");
                }
                sb.AppendLine();
            }
        }

        if (report.Sequence.Count > 0)
        {
            sb.AppendLine("## Suggested sequence");
            sb.AppendLine();
            foreach (var step in report.Sequence)
            {
                sb.AppendLine($"{step.Order}. **{step.Project}** — {step.Reason}");
                sb.AppendLine($"   - {step.Action}");
            }
            sb.AppendLine();
        }

        if (report.PortfolioFindings.Count > 0)
        {
            sb.AppendLine("## Estate-level");
            sb.AppendLine();
            foreach (var hit in report.PortfolioFindings)
            {
                sb.AppendLine($"- **`{hit.Detector.Code}` {hit.Detector.Title}** — {hit.Detector.Recommendation}");
            }
            sb.AppendLine();
        }

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("## Scan notes");
            sb.AppendLine();
            foreach (var warning in report.Warnings.Distinct()) sb.AppendLine($"- {warning}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Effort figures are a disclosed heuristic, not a quote. Analysis is lexical: it reports which APIs a project references, not how the code behaves.");
        sb.AppendLine();

        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------------------------
// HTML
// ---------------------------------------------------------------------------------------------

public sealed class HtmlReporter : IReporter
{
    public string FileExtension => ".html";

    public string Render(ScanReport report)
    {
        var s = report.Summary;
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine($"<title>.NET modernization triage — {E(report.Root)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(":root{--bg:#0f1115;--fg:#e6e8eb;--dim:#9aa3ad;--card:#171a21;--line:#262b34;--red:#ff6b6b;--yel:#ffc857;--grn:#5ddc7f;--acc:#7cc4ff}");
        sb.AppendLine("*{box-sizing:border-box}body{margin:0;padding:32px;background:var(--bg);color:var(--fg);font:15px/1.55 ui-sans-serif,system-ui,-apple-system,Segoe UI,Roboto,sans-serif}");
        sb.AppendLine("h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:32px 0 12px;text-transform:uppercase;letter-spacing:.08em;color:var(--dim)}");
        sb.AppendLine(".sub{color:var(--dim);margin-bottom:24px}code{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:.92em}");
        sb.AppendLine(".grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin-bottom:8px}");
        sb.AppendLine(".card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px}");
        sb.AppendLine(".card .k{color:var(--dim);font-size:12px;text-transform:uppercase;letter-spacing:.06em}.card .v{font-size:22px;font-weight:600;margin-top:6px}");
        sb.AppendLine(".proj{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px;margin-bottom:12px}");
        sb.AppendLine(".proj h3{margin:0 0 2px;font-size:16px}.path{color:var(--dim);font-size:13px;margin-bottom:8px}");
        sb.AppendLine(".meta{color:var(--dim);font-size:13px;margin-bottom:10px}");
        sb.AppendLine(".pill{display:inline-block;padding:2px 8px;border-radius:999px;font-size:11px;font-weight:700;letter-spacing:.05em}");
        sb.AppendLine(".p-red{background:rgba(255,107,107,.15);color:var(--red)}.p-yel{background:rgba(255,200,87,.15);color:var(--yel)}.p-grn{background:rgba(93,220,127,.15);color:var(--grn)}");
        sb.AppendLine(".sev-b{color:var(--red);font-weight:700}.sev-w{color:var(--yel);font-weight:700}.sev-i{color:var(--dim)}");
        sb.AppendLine("table{width:100%;border-collapse:collapse;font-size:13px}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line)}th{color:var(--dim);font-weight:500}");
        sb.AppendLine(".rec{color:var(--dim);font-size:13px;margin:8px 0 0}.eol{font-size:13px;color:var(--dim)}");
        sb.AppendLine("ol{padding-left:22px}li{margin-bottom:10px}.note{color:var(--dim);font-size:13px;margin-top:28px;border-top:1px solid var(--line);padding-top:14px}");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine($"<h1>.NET modernization triage</h1>");
        sb.AppendLine($"<div class=\"sub\"><code>{E(report.Root)}</code> · {report.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC · {ToolInfo.Name} {report.ToolVersion} (offline, deterministic)</div>");

        sb.AppendLine("<div class=\"grid\">");
        Card(sb, "Projects", s.ProjectCount.ToString(CultureInfo.InvariantCulture));
        Card(sb, "Source lines", s.TotalSourceLines.ToString("N0", CultureInfo.InvariantCulture));
        Card(sb, "On .NET Framework", s.ProjectsOnDotNetFramework.ToString(CultureInfo.InvariantCulture));
        Card(sb, "Unsupported runtime", s.ProjectsOutOfSupport.ToString(CultureInfo.InvariantCulture));
        Card(sb, "Red / Yellow / Green", $"{s.Red} / {s.Yellow} / {s.Green}");
        Card(sb, "Effort (heuristic)", $"{s.EffortLowDays:N0}–{s.EffortHighDays:N0} d");
        sb.AppendLine("</div>");

        var blockers = report.Projects
            .SelectMany(p => p.Hits.Select(h => (Project: p, Hit: h)))
            .Where(x => x.Hit.Detector.Severity == Severity.Blocker)
            .GroupBy(x => x.Hit.Detector)
            .OrderByDescending(g => g.Select(x => x.Project.Name).Distinct().Count())
            .ToList();

        if (blockers.Count > 0)
        {
            sb.AppendLine("<h2>Hard blockers</h2><table><tr><th>Code</th><th>Item</th><th>Projects</th></tr>");
            foreach (var group in blockers)
            {
                var count = group.Select(x => x.Project.Name).Distinct().Count();
                sb.AppendLine($"<tr><td><code>{E(group.Key.Code)}</code></td><td>{E(group.Key.Title)}</td><td>{count}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        sb.AppendLine("<h2>Projects</h2>");
        foreach (var project in report.Projects.OrderBy(p => p.Bucket).ThenByDescending(p => p.BlockerCount))
        {
            var pillClass = project.Bucket switch
            {
                Bucket.Red => "p-red",
                Bucket.Yellow => "p-yel",
                _ => "p-grn",
            };
            var tfm = project.TargetFrameworks.Count > 0 ? string.Join(", ", project.TargetFrameworks) : "unknown";

            sb.AppendLine("<div class=\"proj\">");
            sb.AppendLine($"<span class=\"pill {pillClass}\">{project.Bucket.ToString().ToUpperInvariant()}</span> <h3 style=\"display:inline;margin-left:8px\">{E(project.Name)}</h3>");
            sb.AppendLine($"<div class=\"path\">{E(project.Path)}</div>");
            sb.AppendLine($"<div class=\"meta\"><code>{E(tfm)}</code> · {E(string.Join(", ", project.Kinds.DefaultIfEmpty("-")))} · {project.SourceLines:N0} LOC · {project.Effort.LowDays:N0}–{project.Effort.HighDays:N0} engineer-days</div>");

            foreach (var note in project.EolNotes)
            {
                sb.AppendLine($"<div class=\"eol\">· {E(note)}</div>");
            }

            if (project.Hits.Count > 0)
            {
                sb.AppendLine("<table><tr><th>Sev</th><th>Code</th><th>Finding</th><th>Refs</th><th>First seen</th></tr>");
                foreach (var hit in project.Hits)
                {
                    var sevClass = hit.Detector.Severity switch
                    {
                        Severity.Blocker => "sev-b",
                        Severity.Warning => "sev-w",
                        _ => "sev-i",
                    };
                    var letter = hit.Detector.Severity switch
                    {
                        Severity.Blocker => "B",
                        Severity.Warning => "W",
                        _ => "i",
                    };
                    var first = hit.Samples.Count > 0
                        ? $"<code>{E(hit.Samples[0].File)}:{hit.Samples[0].Line}</code>"
                        : "—";
                    sb.AppendLine($"<tr><td class=\"{sevClass}\">{letter}</td><td><code>{E(hit.Detector.Code)}</code></td><td>{E(hit.Detector.Title)}</td><td>{hit.Count}</td><td>{first}</td></tr>");
                }
                sb.AppendLine("</table>");

                var recs = project.Hits.Where(h => h.Detector.Severity != Severity.Info).ToList();
                if (recs.Count > 0)
                {
                    sb.AppendLine("<div class=\"rec\">");
                    foreach (var hit in recs)
                    {
                        sb.AppendLine($"<div><strong>{E(hit.Detector.Code)} {E(hit.Detector.Title)}</strong> — {E(hit.Detector.Recommendation)}</div>");
                    }
                    sb.AppendLine("</div>");
                }
            }

            sb.AppendLine("</div>");
        }

        if (report.Sequence.Count > 0)
        {
            sb.AppendLine("<h2>Suggested sequence</h2><ol>");
            foreach (var step in report.Sequence)
            {
                sb.AppendLine($"<li><strong>{E(step.Project)}</strong> — {E(step.Reason)}<div class=\"rec\">{E(step.Action)}</div></li>");
            }
            sb.AppendLine("</ol>");
        }

        if (report.PortfolioFindings.Count > 0)
        {
            sb.AppendLine("<h2>Estate-level</h2>");
            foreach (var hit in report.PortfolioFindings)
            {
                sb.AppendLine($"<div class=\"proj\"><strong>{E(hit.Detector.Code)} {E(hit.Detector.Title)}</strong><div class=\"rec\">{E(hit.Detector.Recommendation)}</div></div>");
            }
        }

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("<h2>Scan notes</h2><ul>");
            foreach (var warning in report.Warnings.Distinct()) sb.AppendLine($"<li>{E(warning)}</li>");
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("<div class=\"note\">Effort figures are a disclosed heuristic, not a quote. Analysis is lexical: it reports which APIs a project references, not how the code behaves.</div>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }

    private static void Card(StringBuilder sb, string key, string value) =>
        sb.AppendLine($"<div class=\"card\"><div class=\"k\">{E(key)}</div><div class=\"v\">{E(value)}</div></div>");

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
