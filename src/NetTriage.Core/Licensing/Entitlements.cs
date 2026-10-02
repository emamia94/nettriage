namespace NetTriage.Core.Licensing;

/// <summary>
/// Decides which features may run. Free features are always available: a paid licence unlocks
/// scale, never correctness. Nothing in the analysis itself is gated.
/// </summary>
public sealed class Entitlements
{
    public static readonly Entitlements Free = new(LicenseCheck.Missing(), new HashSet<string>(StringComparer.Ordinal));

    public LicenseCheck Check { get; }
    private readonly HashSet<string> _granted;

    private Entitlements(LicenseCheck check, HashSet<string> granted)
    {
        Check = check;
        _granted = granted;
    }

    public static Entitlements From(LicenseCheck check)
    {
        if (check.Status != LicenseStatus.Valid || check.License is null) return Free;
        return new Entitlements(check, new HashSet<string>(check.License.Features, StringComparer.Ordinal));
    }

    public bool IsLicensed => Check.Status == LicenseStatus.Valid;

    public bool Allows(string feature) => _granted.Contains(feature);

    public IReadOnlyList<string> Granted =>
        _granted.OrderBy(f => f, StringComparer.Ordinal).ToList();

    /// <summary>A short line for `license status`.</summary>
    public string Describe()
    {
        if (!IsLicensed) return Check.Message;
        var who = string.IsNullOrWhiteSpace(Check.License!.Organisation)
            ? Check.License.Holder
            : Check.License.Organisation;
        return $"{who} · {Check.License.Edition} · expires {Check.License.Expires:yyyy-MM-dd}";
    }

    /// <summary>
    /// The message shown when a licensed feature is used without a licence. It has to say what is
    /// missing, what still works, and how to fix it, because a dead end with no explanation is how
    /// a free tool loses the user it was trying to win.
    /// </summary>
    public string RefusalMessage(string feature)
    {
        var reason = Check.Status switch
        {
            LicenseStatus.Missing => "No licence was found.",
            LicenseStatus.Expired => Check.Message,
            LicenseStatus.NotYetValid => Check.Message,
            LicenseStatus.Invalid => Check.Message,
            _ => "This licence does not cover the feature.",
        };

        return
            $"{reason}{Environment.NewLine}" +
            $"{Environment.NewLine}" +
            $"'{Features.Describe(feature)}' needs a licence. Everything else - scanning, all " +
            $"{DetectorCatalog.All.Count} detectors, sequencing, effort estimates, console/json/markdown/html " +
            $"output - keeps working without one.{Environment.NewLine}" +
            $"{Environment.NewLine}" +
            $"To activate a licence file:{Environment.NewLine}" +
            $"  nettriage license install <path-to-license.json>{Environment.NewLine}" +
            $"or set {LicenseVerifier.EnvironmentVariable} to its path.";
    }
}
