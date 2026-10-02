using NetTriage.Core.Baseline;
using NetTriage.Core.Rules;

namespace NetTriage.Core.Analysis;

public sealed class ScanOptions
{
    public required string Root { get; init; }
    public List<string> Exclude { get; init; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>Customer rules for this run: custom detectors, severity overrides, allowlist.</summary>
    public RuleSet Rules { get; init; } = RuleSet.Empty;

    /// <summary>When supplied, the report gains a comparison against this recorded state.</summary>
    public Baseline.Baseline? Baseline { get; init; }
}

/// <summary>Orchestrates discovery, per-project analysis, sequencing and roll-up.</summary>
public static class ScanEngine
{
    public static ScanReport Scan(ScanOptions options)
    {
        var absoluteRoot = Path.GetFullPath(options.Root);
        // Report paths relative to the directory being scanned, not to a solution file.
        var rootDirectory = File.Exists(absoluteRoot)
            ? Path.GetDirectoryName(absoluteRoot) ?? absoluteRoot
            : absoluteRoot;

        var report = new ScanReport
        {
            ToolVersion = ToolInfo.Version,
            Root = rootDirectory,
            GeneratedAt = DateTimeOffset.UtcNow,
            Online = false,
        };

        // Customer rules extend the detector set for this run, and their severity overrides are
        // applied before scanning so that effort estimates and buckets see the final severities.
        var customDetectors = RuleLoader.ToDetectors(options.Rules);
        var detectorSet = DetectorSet.BuiltIn
            .With(customDetectors)
            .WithOverrides(RuleLoader.ToOverrides(options.Rules));

        var projects = ProjectDiscovery.Discover(options.Root, options.Warnings)
            .Where(p => !IsExcluded(p, options))
            .ToList();

        foreach (var project in projects)
        {
            try
            {
                report.Projects.Add(ProjectReader.Read(project, report.Root, options.Warnings, detectorSet));
            }
            catch (Exception ex)
            {
                options.Warnings.Add($"Failed to analyse '{project}': {ex.Message}");
            }
        }

        report.Projects = report.Projects
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        report.CustomDetectors = customDetectors;

        // Suppression runs before the roll-up so every number in the report already reflects it.
        // Applying the allowlist also re-derives each project's bucket, so the summary below is right.
        report.Suppressed = RuleApplication.Apply(report, options.Rules, options.Warnings);

        AddPortfolioFindings(report);
        report.Sequence = Sequencer.Build(report.Projects);
        report.Summary = Summarize(report);
        report.Warnings = options.Warnings;

        if (options.Baseline is not null)
        {
            report.BaselineDiff = BaselineComparer.Compare(options.Baseline, report);
        }

        return report;
    }

    private static bool IsExcluded(string projectPath, ScanOptions options)
    {
        foreach (var pattern in options.Exclude)
        {
            if (Match(projectPath, pattern)) return true;
        }
        return false;
    }

    private static bool Match(string path, string pattern)
    {
        // Supports plain substring and a simple * wildcard.
        if (!pattern.Contains('*'))
        {
            return path.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        var parts = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
        int index = 0;
        foreach (var part in parts)
        {
            var found = path.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            index = found + part.Length;
        }
        return true;
    }

    private static void AddPortfolioFindings(ScanReport report)
    {
        var hasTestProject = report.Projects.Any(p => p.Kinds.Contains("Test"));
        if (report.Projects.Count > 1 && !hasTestProject)
        {
            var detector = DetectorCatalog.ById("NT2006");
            if (detector is not null)
            {
                report.PortfolioFindings.Add(new DetectorHit(detector, 1, Array.Empty<Occurrence>()));
            }
        }
    }

    /// <summary>
    /// Recomputes the roll-up from the projects as they now stand. Callers that change findings
    /// after a scan - applying an allowlist does - must call this, or the headline numbers in the
    /// report will contradict the findings list underneath them.
    /// </summary>
    public static void Resummarize(ScanReport report) => report.Summary = Summarize(report);

    private static ScanSummary Summarize(ScanReport report)
    {
        var summary = new ScanSummary
        {
            ProjectCount = report.Projects.Count,
            Green = report.Projects.Count(p => p.Bucket == Bucket.Green),
            Yellow = report.Projects.Count(p => p.Bucket == Bucket.Yellow),
            Red = report.Projects.Count(p => p.Bucket == Bucket.Red),
            ProjectsOutOfSupport = report.Projects.Count(p => p.IsOutOfSupport),
        };

        foreach (var project in report.Projects)
        {
            summary.TotalSourceFiles += project.SourceFiles;
            summary.TotalSourceLines += project.SourceLines;

            if (project.TargetFrameworks.Count == 0)
            {
                summary.ProjectsOnUnknownRuntime++;
            }
            else if (project.TargetFrameworks.Any(FrameworkLifecycles.IsDotNetFramework))
            {
                summary.ProjectsOnDotNetFramework++;
            }
            else if (project.TargetFrameworks.Any(FrameworkLifecycles.IsModernDotNet))
            {
                summary.ProjectsOnModernDotNet++;
            }
            else
            {
                summary.ProjectsOnUnknownRuntime++;
            }

            foreach (var hit in project.Hits)
            {
                switch (hit.Detector.Severity)
                {
                    case Severity.Blocker: summary.BlockerFindings++; break;
                    case Severity.Warning: summary.WarningFindings++; break;
                    default: summary.InfoFindings++; break;
                }
            }
        }

        foreach (var hit in report.PortfolioFindings)
        {
            switch (hit.Detector.Severity)
            {
                case Severity.Blocker: summary.BlockerFindings++; break;
                case Severity.Warning: summary.WarningFindings++; break;
                default: summary.InfoFindings++; break;
            }
        }

        summary.EffortLowDays = Math.Round(report.Projects.Sum(p => p.Effort.LowDays), 1);
        summary.EffortHighDays = Math.Round(report.Projects.Sum(p => p.Effort.HighDays), 1);

        // Only the monikers that are actually at or past end of support belong here - not every
        // target framework of a project that happens to have one expired target.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        summary.EolTargetFrameworks = report.Projects
            .SelectMany(p => p.TargetFrameworks)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(m => FrameworkLifecycles.Lookup(m)?.EndOfSupport is { } end
                        && end.DayNumber - today.DayNumber <= 180)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return summary;
    }
}

/// <summary>
/// Orders the work: shared libraries before their consumers, blockers surfaced early.
/// The ordering rule is "leaves first" - a project is migrated after everything it depends on.
/// </summary>
public static class Sequencer
{
    public static List<SequenceStep> Build(IReadOnlyList<ProjectReport> projects)
    {
        var byName = new Dictionary<string, ProjectReport>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects) byName.TryAdd(project.Name, project);

        var dependsOn = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var referencedByCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in project.ProjectReferences)
            {
                if (byName.ContainsKey(reference) && !reference.Equals(project.Name, StringComparison.OrdinalIgnoreCase))
                {
                    deps.Add(reference);
                    referencedByCount[reference] = referencedByCount.GetValueOrDefault(reference) + 1;
                }
            }
            dependsOn[project.Name] = deps;
        }

        // Kahn's algorithm, dependencies first. Cycles are broken deterministically.
        var ordered = new List<string>();
        var remaining = new HashSet<string>(dependsOn.Keys, StringComparer.OrdinalIgnoreCase);
        var guard = 0;

        while (remaining.Count > 0 && guard++ < projects.Count * 4 + 16)
        {
            var ready = remaining
                .Where(name => dependsOn[name].All(d => !remaining.Contains(d)))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ready.Count == 0)
            {
                // Circular reference: take the alphabetically first and flag it.
                ready = remaining.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(1).ToList();
            }

            foreach (var name in ready)
            {
                ordered.Add(name);
                remaining.Remove(name);
            }
        }

        var steps = new List<SequenceStep>();
        int order = 1;

        foreach (var name in ordered)
        {
            if (!byName.TryGetValue(name, out var project)) continue;

            var consumers = referencedByCount.GetValueOrDefault(name);
            var reason = consumers switch
            {
                0 => "No project references it - safe to move without breaking consumers.",
                1 => "Referenced by 1 project - migrate it before that consumer.",
                _ => $"Referenced by {consumers} projects - shared library, migrate it first so consumers can follow.",
            };

            if (project.BlockerCount > 0)
            {
                reason += $" Has {project.BlockerCount} hard blocker(s), so scope it separately rather than bundling it with the easy work.";
            }

            var action = project.Bucket switch
            {
                Bucket.Green => "Mechanical retarget and package upgrade. Good pilot candidate - use it to calibrate the estimate.",
                Bucket.Yellow => "Retarget plus targeted refactors. Keep behaviour parity as the success criterion.",
                _ => "Scope as a rewrite of the affected layers, not a migration. Run it as its own project with its own plan.",
            };

            if (consumers > 1)
            {
                action += " Consider multi-targeting (current framework + net10.0) so old and new consumers can coexist during the transition.";
            }

            steps.Add(new SequenceStep(order++, project.Name, reason, action));
        }

        return steps;
    }
}
