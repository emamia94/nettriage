namespace NetTriage.Core.Rules;

/// <summary>A detector defined by the customer, using the same matching engine as the built-ins.</summary>
public sealed class CustomRule
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>info, warning or blocker.</summary>
    public string Severity { get; set; } = "warning";
    public string Category { get; set; } = "Custom";
    public string Recommendation { get; set; } = "";
    public double EffortDays { get; set; }
    /// <summary>Names to look for, exactly like the built-in detectors: System.Web.UI.Page, Foo.Bar.</summary>
    public List<string> Names { get; set; } = new();
    /// <summary>Optional. Only source files matching this glob are scanned for this rule.</summary>
    public string? FileGlob { get; set; }
    /// <summary>Optional. The rule only applies inside projects matching this glob.</summary>
    public string? ProjectGlob { get; set; }
}

/// <summary>
/// A deliberate exception. Suppression is recorded and reported, never silent, and an entry can
/// carry an expiry so that a temporary exemption comes back to bite on schedule.
/// </summary>
public sealed class AllowlistEntry
{
    public string Detector { get; set; } = "";
    public string? File { get; set; }
    public string? Project { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Optional ISO date. After this date the entry stops applying and a warning is raised.</summary>
    public string? Expires { get; set; }
}

/// <summary>The customer's rule file.</summary>
public sealed class RuleSet
{
    public const string CurrentVersion = "nettriage-rules/1";

    public string Format { get; set; } = CurrentVersion;
    public List<CustomRule> Rules { get; set; } = new();
    /// <summary>Detector code to the severity it should be reported as instead.</summary>
    public Dictionary<string, string> SeverityOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AllowlistEntry> Allowlist { get; set; } = new();

    public static RuleSet Empty => new();
}

/// <summary>What an allowlist entry removed, so the report can say so out loud.</summary>
public sealed record SuppressedFinding(string Project, string Detector, string Reason, string Entry);

public sealed record RuleValidationResult(bool IsValid, List<string> Errors, List<string> Warnings)
{
    public static RuleValidationResult Ok(List<string>? warnings = null) => new(true, new(), warnings ?? new());
}
