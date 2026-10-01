namespace NetTriage.Core;

/// <summary>How much a finding hurts a migration.</summary>
public enum Severity
{
    Info = 0,
    Warning = 1,
    Blocker = 2,
}

/// <summary>Triage bucket for a project: green is straightforward, red is a rewrite.</summary>
public enum Bucket
{
    Green = 0,
    Yellow = 1,
    Red = 2,
}

public enum ProjectStyle
{
    Unknown = 0,
    Legacy = 1,
    Sdk = 2,
}

/// <summary>A single place in the code where something was found.</summary>
public sealed record Occurrence(string File, int Line, string Text);

/// <summary>A deterministic check performed against project files, project XML and source.</summary>
public sealed record Detector(
    string Code,
    string Title,
    Severity Severity,
    string Category,
    string Recommendation,
    double EffortDays,
    IReadOnlyList<string> Names)
{
    public override string ToString() => Code + " " + Title;
}

/// <summary>The aggregated result of one detector firing inside one project.</summary>
public sealed record DetectorHit(
    Detector Detector,
    int Count,
    IReadOnlyList<Occurrence> Samples);

public sealed record PackageRef(string Id, string? Version, string Source);

/// <summary>A heuristic effort range. Never a quote.</summary>
public sealed record EffortEstimate(double LowDays, double HighDays, string Rationale);

public sealed class ProjectReport
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public ProjectStyle Style { get; set; }
    public List<string> TargetFrameworks { get; set; } = new();
    /// <summary>Where the target framework was declared: the project file, Directory.Build.props, or an import.</summary>
    public string TargetFrameworkSource { get; set; } = "";
    public string OutputType { get; set; } = "";
    public List<string> Kinds { get; set; } = new();
    public int SourceFiles { get; set; }
    public int SourceLines { get; set; }
    public List<string> ProjectReferences { get; set; } = new();
    public List<PackageRef> Packages { get; set; } = new();
    public List<DetectorHit> Hits { get; set; } = new();
    public Bucket Bucket { get; set; }
    public EffortEstimate Effort { get; set; } = new(0, 0, "");
    public List<string> EolNotes { get; set; } = new();
    public List<string> MarkerFiles { get; set; } = new();

    public Severity Worst => Hits.Count == 0 ? Severity.Info : Hits.Max(h => h.Detector.Severity);
    public int BlockerCount => Hits.Count(h => h.Detector.Severity == Severity.Blocker);
    public int WarningCount => Hits.Count(h => h.Detector.Severity == Severity.Warning);

    /// <summary>True when the project targets a runtime that is out of support.</summary>
    public bool IsOutOfSupport { get; set; }
}

public sealed record SequenceStep(int Order, string Project, string Reason, string Action);

public sealed class ScanSummary
{
    public int ProjectCount { get; set; }
    public int ProjectsOnDotNetFramework { get; set; }
    public int ProjectsOnModernDotNet { get; set; }
    public int ProjectsOnUnknownRuntime { get; set; }
    public int TotalSourceFiles { get; set; }
    public int TotalSourceLines { get; set; }
    public int BlockerFindings { get; set; }
    public int WarningFindings { get; set; }
    public int InfoFindings { get; set; }
    public int Green { get; set; }
    public int Yellow { get; set; }
    public int Red { get; set; }
    public int ProjectsOutOfSupport { get; set; }
    public double EffortLowDays { get; set; }
    public double EffortHighDays { get; set; }
    public List<string> EolTargetFrameworks { get; set; } = new();
}

public sealed class ScanReport
{
    public string Tool { get; set; } = "nettriage";
    public string ToolVersion { get; set; } = "";
    public string Root { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public bool Online { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<ProjectReport> Projects { get; set; } = new();
    public List<DetectorHit> PortfolioFindings { get; set; } = new();
    public List<SequenceStep> Sequence { get; set; } = new();
    public ScanSummary Summary { get; set; } = new();
}

/// <summary>Support lifecycle for a target framework moniker.</summary>
public sealed record FrameworkLifecycle(string Moniker, DateOnly? EndOfSupport, string Note);

public static class FrameworkLifecycles
{
    // Dates reflect the published Microsoft support policy. .NET Framework entries follow the
    // Windows component lifecycle, so several have no fixed end date - that is recorded as null
    // rather than guessed at.
    public static readonly IReadOnlyList<FrameworkLifecycle> All = new[]
    {
        new FrameworkLifecycle("net10.0", new DateOnly(2028, 11, 14), ".NET 10 (LTS)"),
        new FrameworkLifecycle("net9.0", new DateOnly(2026, 11, 10), ".NET 9 (STS)"),
        new FrameworkLifecycle("net8.0", new DateOnly(2026, 11, 10), ".NET 8 (LTS)"),
        new FrameworkLifecycle("net7.0", new DateOnly(2024, 5, 14), ".NET 7 (STS)"),
        new FrameworkLifecycle("net6.0", new DateOnly(2024, 11, 12), ".NET 6 (LTS)"),
        new FrameworkLifecycle("net5.0", new DateOnly(2022, 5, 8), ".NET 5 (STS)"),
        new FrameworkLifecycle("netcoreapp3.1", new DateOnly(2022, 12, 13), ".NET Core 3.1"),
        new FrameworkLifecycle("netcoreapp3.0", new DateOnly(2020, 3, 3), ".NET Core 3.0"),
        new FrameworkLifecycle("netcoreapp2.2", new DateOnly(2019, 12, 23), ".NET Core 2.2"),
        new FrameworkLifecycle("netcoreapp2.1", new DateOnly(2021, 8, 21), ".NET Core 2.1"),
        new FrameworkLifecycle("netcoreapp2.0", new DateOnly(2018, 10, 1), ".NET Core 2.0"),
        new FrameworkLifecycle("net481", null, ".NET Framework 4.8.1 - Windows component, security fixes only"),
        new FrameworkLifecycle("net48", null, ".NET Framework 4.8 - Windows component, security fixes only"),
        new FrameworkLifecycle("net472", null, ".NET Framework 4.7.2 - Windows component, security fixes only"),
        new FrameworkLifecycle("net471", null, ".NET Framework 4.7.1 - Windows component, security fixes only"),
        new FrameworkLifecycle("net47", null, ".NET Framework 4.7 - Windows component, security fixes only"),
        new FrameworkLifecycle("net462", new DateOnly(2027, 1, 13), ".NET Framework 4.6.2"),
        new FrameworkLifecycle("net461", null, ".NET Framework 4.6.1 - tied to the Windows version it ships with"),
        new FrameworkLifecycle("net46", null, ".NET Framework 4.6 - tied to the Windows version it ships with"),
        new FrameworkLifecycle("net452", null, ".NET Framework 4.5.2 - tied to the Windows version it ships with"),
        new FrameworkLifecycle("net451", null, ".NET Framework 4.5.1 - tied to the Windows version it ships with"),
        new FrameworkLifecycle("net45", null, ".NET Framework 4.5 - tied to the Windows version it ships with"),
        new FrameworkLifecycle("net40", null, ".NET Framework 4.0 - long out of mainstream support"),
        new FrameworkLifecycle("net35", new DateOnly(2029, 1, 10), ".NET Framework 3.5 SP1 - Windows component"),
        new FrameworkLifecycle("netstandard2.1", null, ".NET Standard 2.1 - a contract, not a runtime"),
        new FrameworkLifecycle("netstandard2.0", null, ".NET Standard 2.0 - a contract, not a runtime"),
    };

    private static readonly Dictionary<string, FrameworkLifecycle> ByMoniker =
        All.ToDictionary(f => f.Moniker, StringComparer.OrdinalIgnoreCase);

    public static FrameworkLifecycle? Lookup(string moniker) =>
        ByMoniker.TryGetValue(moniker.Trim(), out var f) ? f : null;

    /// <summary>Maps a legacy TargetFrameworkVersion value (v4.8) to a moniker (net48).</summary>
    public static string NormalizeLegacyVersion(string raw)
    {
        var v = raw.Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        return "net" + v.Replace(".", "");
    }

    public static bool IsDotNetFramework(string moniker) =>
        moniker.StartsWith("net4", StringComparison.OrdinalIgnoreCase) ||
        moniker.StartsWith("net3", StringComparison.OrdinalIgnoreCase);

    public static bool IsModernDotNet(string moniker) =>
        moniker.StartsWith("net5", StringComparison.OrdinalIgnoreCase) ||
        moniker.StartsWith("net6", StringComparison.OrdinalIgnoreCase) ||
        moniker.StartsWith("net7", StringComparison.OrdinalIgnoreCase) ||
        moniker.StartsWith("net8", StringComparison.OrdinalIgnoreCase) ||
        moniker.StartsWith("net9", StringComparison.OrdinalIgnoreCase) ||
        moniker.StartsWith("net10", StringComparison.OrdinalIgnoreCase) ||
        moniker.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase);
}
