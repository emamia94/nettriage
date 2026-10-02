using System.Security.Cryptography;
using System.Text;

namespace NetTriage.Core.Estate;

/// <summary>One application in the estate, with its full scan result and a stable identity.</summary>
public sealed class EstateApp
{
    /// <summary>Stable across machines and moves: derived from the git remote, or the relative path.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string IdentitySource { get; set; } = "";
    public ScanReport Report { get; set; } = new();

    public int Blockers => Report.Summary.BlockerFindings;
    public int Warnings => Report.Summary.WarningFindings;
    public bool AnyOutOfSupport => Report.Summary.ProjectsOutOfSupport > 0;

    /// <summary>
    /// A single number for ranking, so a lead can answer "which one first". Deliberately simple and
    /// documented rather than clever: blockers weigh ten, warnings three, an unsupported runtime is
    /// an automatic fifteen, and effort contributes a capped amount.
    /// </summary>
    public double RiskScore =>
        Blockers * 10.0
        + Warnings * 3.0
        + (AnyOutOfSupport ? 15.0 : 0.0)
        + Math.Min(Report.Summary.EffortHighDays, 2000) / 20.0;
}

public sealed class EstateSummary
{
    public int AppCount { get; set; }
    public int Projects { get; set; }
    public int SourceLines { get; set; }
    public int Blockers { get; set; }
    public int Warnings { get; set; }
    public int AppsOutOfSupport { get; set; }
    public int RedApps { get; set; }
    public double EffortLowDays { get; set; }
    public double EffortHighDays { get; set; }
    public List<string> EolTargetFrameworks { get; set; } = new();
}

/// <summary>Change since the previous snapshot.</summary>
public sealed record EstateTrendEntry(
    string AppId,
    string Name,
    int BlockersBefore,
    int BlockersNow,
    double EffortHighBefore,
    double EffortHighNow)
{
    public int BlockerDelta => BlockersNow - BlockersBefore;
    public double EffortDelta => EffortHighNow - EffortHighBefore;
}

public sealed class EstateTrend
{
    public DateTimeOffset PreviousTakenAt { get; set; }
    public DateTimeOffset CurrentTakenAt { get; set; }
    public List<EstateTrendEntry> Entries { get; set; } = new();

    public IEnumerable<EstateTrendEntry> Worsening => Entries.Where(e => e.BlockerDelta > 0)
        .OrderByDescending(e => e.BlockerDelta);

    public IEnumerable<EstateTrendEntry> Improving => Entries.Where(e => e.BlockerDelta < 0)
        .OrderBy(e => e.BlockerDelta);

    public IEnumerable<EstateTrendEntry> NewApps => Entries.Where(e => e.BlockersBefore == 0 && e.BlockersNow > 0);

    public int NetBlockerChange => Entries.Sum(e => e.BlockerDelta);
}

public sealed class EstateReport
{
    public string ToolVersion { get; set; } = "";
    public string Root { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public List<EstateApp> Apps { get; set; } = new();
    public EstateSummary Summary { get; set; } = new();
    public EstateTrend? Trend { get; set; }
    public List<string> Warnings { get; set; } = new();

    /// <summary>Worst first: the ranking the whole feature exists to produce.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<EstateApp> Ranked => Apps
        .OrderByDescending(a => a.RiskScore)
        .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>
/// Derives an identity that survives a move, a different checkout directory, and a different
/// machine. The git remote is the strongest signal; the relative path is the fallback.
/// </summary>
public static class AppIdentity
{
    public static (string Id, string Source) Compute(string appDirectory, string estateRoot)
    {
        var remote = TryReadGitRemote(appDirectory);
        if (!string.IsNullOrWhiteSpace(remote))
        {
            return (Hash("git:" + NormalizeRemote(remote)), "git remote");
        }

        var relative = Path.GetRelativePath(estateRoot, appDirectory).Replace('\\', '/').ToLowerInvariant();
        return (Hash("path:" + relative), "relative path");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    /// <summary>Both SSH and HTTPS forms of the same remote must produce the same id.</summary>
    private static string NormalizeRemote(string remote)
    {
        var value = remote.Trim().TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];

        var at = value.IndexOf('@');
        if (at >= 0)
        {
            var colon = value.IndexOf(':', at);
            if (colon > at) value = value[(at + 1)..colon] + "/" + value[(colon + 1)..];
        }

        value = value.Replace("https://", "").Replace("http://", "").Replace("ssh://", "");
        var slash = value.IndexOf('/');
        if (slash >= 0 && value[..slash].Contains('.')) value = value[(slash + 1)..];

        return value.ToLowerInvariant();
    }

    private static string? TryReadGitRemote(string directory)
    {
        var current = new DirectoryInfo(directory);
        for (var depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
        {
            var config = Path.Combine(current.FullName, ".git", "config");
            if (!File.Exists(config)) continue;

            try
            {
                var inOrigin = false;
                foreach (var raw in File.ReadAllLines(config))
                {
                    var line = raw.Trim();
                    if (line.StartsWith('['))
                    {
                        inOrigin = line.Replace(" ", "").Equals("[remote\"origin\"]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (inOrigin && line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
                    {
                        var eq = line.IndexOf('=');
                        if (eq > 0) return line[(eq + 1)..].Trim();
                    }
                }
            }
            catch
            {
                // An unreadable .git/config is not worth failing an estate scan over.
            }
        }

        return null;
    }
}
