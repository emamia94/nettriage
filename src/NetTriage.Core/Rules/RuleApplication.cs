using System.Globalization;
using NetTriage.Core.Analysis;

namespace NetTriage.Core.Rules;

/// <summary>
/// Applies the parts of a rule file that act on results rather than on scanning: the allowlist.
/// Severity overrides are applied earlier, to the detector set, so effort estimates see them too.
/// </summary>
public static class RuleApplication
{
    /// <summary>
    /// Removes allowlisted findings from every project and records what was removed.
    /// Suppression is at project + detector granularity: an entry with a file glob applies when any
    /// reported location for that finding matches. This is stated in the README because it is the
    /// kind of detail that decides whether people trust the output.
    /// </summary>
    public static List<SuppressedFinding> Apply(
        ScanReport report, RuleSet rules, List<string> warnings, DateOnly? today = null)
    {
        var suppressed = new List<SuppressedFinding>();
        if (rules.Allowlist.Count == 0) return suppressed;

        var now = today ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var active = new List<(AllowlistEntry Entry, string Label)>();
        for (var i = 0; i < rules.Allowlist.Count; i++)
        {
            var entry = rules.Allowlist[i];
            var label = $"allowlist[{i}]";

            if (entry.Expires is not null
                && DateOnly.TryParseExact(entry.Expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)
                && expiry < now)
            {
                warnings.Add(
                    $"{label} for {entry.Detector} expired on {expiry:yyyy-MM-dd} and was not applied. " +
                    "The finding is back in the report.");
                continue;
            }

            active.Add((entry, label));
        }

        foreach (var project in report.Projects)
        {
            var kept = new List<DetectorHit>();

            foreach (var hit in project.Hits)
            {
                var match = active.FirstOrDefault(a => Matches(a.Entry, hit, project));
                if (match.Entry is null)
                {
                    kept.Add(hit);
                    continue;
                }

                suppressed.Add(new SuppressedFinding(
                    project.Name,
                    hit.Detector.Code,
                    string.IsNullOrWhiteSpace(match.Entry.Reason) ? "(no reason given)" : match.Entry.Reason,
                    match.Label));
            }

            project.Hits = kept;
        }

        // The bucket was decided while the finding was still there. Re-deriving it here keeps the
        // report internally consistent for every caller, not just the scan engine.
        foreach (var project in report.Projects)
        {
            project.Bucket = ProjectReader.DeriveBucket(project);
        }

        if (suppressed.Count > 0) ScanEngine.Resummarize(report);

        return suppressed;
    }

    private static bool Matches(AllowlistEntry entry, DetectorHit hit, ProjectReport project)
    {
        if (entry.Detector != "*"
            && !string.Equals(entry.Detector, hit.Detector.Code, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.Project)
            && !GlobMatcher.IsMatch(project.Path, entry.Project))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.File))
        {
            // Match against every location we kept, not just the first one.
            if (!hit.Samples.Any(s => GlobMatcher.IsMatch(s.File, entry.File)))
            {
                return false;
            }
        }

        return true;
    }
}
