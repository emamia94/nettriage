namespace NetTriage.Core.Baseline;

/// <summary>One project's recorded findings: detector code to occurrence count.</summary>
public sealed class BaselineProject
{
    public string Name { get; set; } = "";
    /// <summary>Root-relative, so a baseline taken on one machine is valid on another.</summary>
    public string Path { get; set; } = "";
    public string TargetFrameworks { get; set; } = "";
    /// <summary>Detector code to how many times it fired.</summary>
    public Dictionary<string, int> Findings { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A recorded snapshot of what the scanner found, used to freeze existing debt so a CI gate can
/// fail on new problems only. Deliberately holds no line numbers: an edit anywhere above a finding
/// would otherwise invalidate the whole file.
/// </summary>
public sealed class Baseline
{
    public const string CurrentFormat = "nettriage-baseline/1";

    public string Format { get; set; } = CurrentFormat;
    public string ToolVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Informational only. Never compared, so the baseline survives a move.</summary>
    public string Root { get; set; } = "";
    public List<BaselineProject> Projects { get; set; } = new();

    public int TotalFindings => Projects.Sum(p => p.Findings.Values.Sum());
}

public enum DeltaKind
{
    Unchanged,
    New,
    Grew,
    Shrank,
    Resolved,
    NewProject,
    RemovedProject,
}

public sealed record FindingDelta(
    string Project,
    string Detector,
    Severity Severity,
    string Category,
    int BaselineCount,
    int CurrentCount,
    DeltaKind Kind)
{
    public int NewCount => Math.Max(0, CurrentCount - BaselineCount);
    public int ResolvedCount => Math.Max(0, BaselineCount - CurrentCount);
}

/// <summary>The comparison between a baseline and a fresh scan.</summary>
public sealed class BaselineDiff
{
    public required Baseline Baseline { get; init; }
    public List<FindingDelta> Deltas { get; init; } = new();

    public IEnumerable<FindingDelta> New => Deltas.Where(d => d.NewCount > 0);
    public IEnumerable<FindingDelta> Resolved => Deltas.Where(d => d.ResolvedCount > 0);

    public int NewOccurrences => Deltas.Sum(d => d.NewCount);
    public int ResolvedOccurrences => Deltas.Sum(d => d.ResolvedCount);

    public int NewBlockers => Deltas.Count(d => d.Severity == Severity.Blocker && d.NewCount > 0);
    public int NewWarnings => Deltas.Count(d => d.Severity == Severity.Warning && d.NewCount > 0);

    public bool AnyNew => NewOccurrences > 0;

    /// <summary>Findings present now but not in the baseline - what a drift gate acts on.</summary>
    public IEnumerable<FindingDelta> Regressions => Deltas
        .Where(d => d.NewCount > 0)
        .OrderByDescending(d => d.Severity)
        .ThenByDescending(d => d.NewCount)
        .ThenBy(d => d.Project, StringComparer.OrdinalIgnoreCase)
        .ThenBy(d => d.Detector, StringComparer.Ordinal);

    public IEnumerable<FindingDelta> Improvements => Deltas
        .Where(d => d.ResolvedCount > 0)
        .OrderByDescending(d => d.ResolvedCount)
        .ThenBy(d => d.Project, StringComparer.OrdinalIgnoreCase)
        .ThenBy(d => d.Detector, StringComparer.Ordinal);
}

public static class BaselineComparer
{
    /// <summary>
    /// Compares at project + detector granularity. Counts, not line numbers, so that ordinary edits
    /// do not churn the baseline, and moving a call between files is correctly a no-op for a gate
    /// that cares about how much of the problem exists.
    /// </summary>
    public static BaselineDiff Compare(Baseline baseline, ScanReport report)
    {
        var deltas = new List<FindingDelta>();
        var current = report.Projects.ToDictionary(p => p.Path, p => p, StringComparer.OrdinalIgnoreCase);
        var recorded = baseline.Projects.ToDictionary(p => p.Path, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var project in report.Projects)
        {
            recorded.TryGetValue(project.Path, out var before);

            if (before is null)
            {
                foreach (var hit in project.Hits)
                {
                    deltas.Add(new FindingDelta(project.Name, hit.Detector.Code, hit.Detector.Severity,
                        hit.Detector.Category, 0, hit.Count, DeltaKind.NewProject));
                }
                continue;
            }

            foreach (var hit in project.Hits)
            {
                before.Findings.TryGetValue(hit.Detector.Code, out var was);
                deltas.Add(new FindingDelta(project.Name, hit.Detector.Code, hit.Detector.Severity,
                    hit.Detector.Category, was, hit.Count, Classify(was, hit.Count)));
            }

            // Detectors that fired before and no longer do.
            foreach (var (code, was) in before.Findings)
            {
                if (project.Hits.Any(h => h.Detector.Code == code)) continue;

                var detector = DetectorCatalog.ById(code);
                deltas.Add(new FindingDelta(project.Name, code,
                    detector?.Severity ?? Severity.Info,
                    detector?.Category ?? "unknown",
                    was, 0, DeltaKind.Resolved));
            }
        }

        foreach (var before in baseline.Projects)
        {
            if (current.ContainsKey(before.Path)) continue;

            foreach (var (code, was) in before.Findings)
            {
                var detector = DetectorCatalog.ById(code);
                deltas.Add(new FindingDelta(before.Name, code,
                    detector?.Severity ?? Severity.Info,
                    detector?.Category ?? "unknown",
                    was, 0, DeltaKind.RemovedProject));
            }
        }

        return new BaselineDiff { Baseline = baseline, Deltas = deltas };
    }

    private static DeltaKind Classify(int before, int now)
    {
        if (now == 0) return before == 0 ? DeltaKind.Unchanged : DeltaKind.Resolved;
        if (now > before) return DeltaKind.Grew;
        if (now < before) return DeltaKind.Shrank;
        return DeltaKind.Unchanged;
    }

    /// <summary>Captures a scan as a baseline. Used by `baseline save` and by the estate snapshots.</summary>
    public static Baseline Capture(ScanReport report, string toolVersion)
    {
        var baseline = new Baseline
        {
            Format = Baseline.CurrentFormat,
            ToolVersion = toolVersion,
            CreatedAt = report.GeneratedAt,
            Root = report.Root,
        };

        foreach (var project in report.Projects.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            var entry = new BaselineProject
            {
                Name = project.Name,
                Path = project.Path,
                TargetFrameworks = string.Join(";", project.TargetFrameworks),
            };

            foreach (var hit in project.Hits)
            {
                entry.Findings[hit.Detector.Code] = hit.Count;
            }

            baseline.Projects.Add(entry);
        }

        return baseline;
    }
}
