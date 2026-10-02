using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NetTriage.Core.Rules;

/// <summary>Reads, validates and compiles a customer rule file.</summary>
public static class RuleLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Regex CodePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]{1,23}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RuleSet? Load(string path, out List<string> errors)
    {
        errors = new List<string>();

        if (!File.Exists(path))
        {
            errors.Add($"Rules file not found: {path}");
            return null;
        }

        RuleSet? rules;
        try
        {
            rules = JsonSerializer.Deserialize<RuleSet>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            errors.Add($"'{path}' is not valid JSON: {ex.Message}");
            return null;
        }

        if (rules is null)
        {
            errors.Add($"'{path}' is empty.");
            return null;
        }

        rules.SeverityOverrides = new Dictionary<string, string>(rules.SeverityOverrides, StringComparer.OrdinalIgnoreCase);

        var validation = Validate(rules);
        errors.AddRange(validation.Errors);
        return validation.IsValid ? rules : null;
    }

    public static RuleValidationResult Validate(RuleSet rules)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!string.IsNullOrWhiteSpace(rules.Format)
            && !string.Equals(rules.Format, RuleSet.CurrentVersion, StringComparison.Ordinal))
        {
            errors.Add($"Unknown rules format '{rules.Format}'. Expected '{RuleSet.CurrentVersion}'.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rules.Rules.Count; i++)
        {
            var rule = rules.Rules[i];
            var label = string.IsNullOrWhiteSpace(rule.Code) ? $"rules[{i}]" : rule.Code;

            if (string.IsNullOrWhiteSpace(rule.Code))
            {
                errors.Add($"{label}: 'code' is required.");
            }
            else if (!CodePattern.IsMatch(rule.Code))
            {
                errors.Add($"{label}: 'code' must be 2-24 characters, start with a letter, and contain only letters, digits or underscores.");
            }
            else
            {
                if (DetectorCatalog.ById(rule.Code) is not null)
                {
                    errors.Add($"{label}: this code is already used by a built-in detector. Pick a different one (a prefix such as ORG is a good habit).");
                }

                if (!seen.Add(rule.Code))
                {
                    errors.Add($"{label}: duplicate rule code in this file.");
                }
            }

            if (string.IsNullOrWhiteSpace(rule.Title))
            {
                errors.Add($"{label}: 'title' is required - it is what the reader sees.");
            }

            if (ParseSeverity(rule.Severity) is null)
            {
                errors.Add($"{label}: 'severity' must be one of info, warning, blocker (got '{rule.Severity}').");
            }

            if (rule.Names.Count == 0 || rule.Names.All(string.IsNullOrWhiteSpace))
            {
                errors.Add($"{label}: 'names' must list at least one name to look for, for example \"System.Web.UI.Page\".");
            }

            foreach (var name in rule.Names.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                if (name.Length < 2)
                {
                    errors.Add($"{label}: name '{name}' is too short to be a meaningful match.");
                }
            }

            if (rule.EffortDays < 0)
            {
                errors.Add($"{label}: 'effortDays' cannot be negative.");
            }

            if (string.IsNullOrWhiteSpace(rule.Recommendation))
            {
                warnings.Add($"{label}: no 'recommendation'. The report will say what was found but not what to do about it.");
            }
        }

        foreach (var (code, severity) in rules.SeverityOverrides)
        {
            if (DetectorCatalog.ById(code) is null
                && !rules.Rules.Any(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"severityOverrides: '{code}' is not a detector this build knows about.");
            }

            if (ParseSeverity(severity) is null)
            {
                errors.Add($"severityOverrides: '{code}' maps to '{severity}', which is not one of info, warning, blocker.");
            }
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        for (var i = 0; i < rules.Allowlist.Count; i++)
        {
            var entry = rules.Allowlist[i];
            var label = $"allowlist[{i}]";

            if (string.IsNullOrWhiteSpace(entry.Detector))
            {
                errors.Add($"{label}: 'detector' is required. Use a detector code, or \"*\" for all of them.");
            }
            else if (entry.Detector != "*"
                     && DetectorCatalog.ById(entry.Detector) is null
                     && !rules.Rules.Any(r => string.Equals(r.Code, entry.Detector, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{label}: unknown detector '{entry.Detector}'.");
            }

            if (string.IsNullOrWhiteSpace(entry.File) && string.IsNullOrWhiteSpace(entry.Project))
            {
                warnings.Add($"{label}: no 'file' or 'project' glob, so it suppresses this detector everywhere.");
            }

            if (string.IsNullOrWhiteSpace(entry.Reason))
            {
                warnings.Add($"{label}: no 'reason'. An unexplained exception is indistinguishable from an accident six months later.");
            }

            if (entry.Expires is not null)
            {
                if (!DateOnly.TryParseExact(entry.Expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
                {
                    errors.Add($"{label}: 'expires' must be an ISO date such as 2027-01-31.");
                }
                else if (expiry < today)
                {
                    warnings.Add($"{label}: expired on {expiry:yyyy-MM-dd} and is no longer applied. Remove it, or move the date.");
                }
            }
        }

        return new RuleValidationResult(errors.Count == 0, errors, warnings);
    }

    /// <summary>Compiles the customer rules into detectors the scanner can run.</summary>
    public static List<Detector> ToDetectors(RuleSet rules)
    {
        var detectors = new List<Detector>();

        foreach (var rule in rules.Rules)
        {
            var severity = ParseSeverity(rule.Severity) ?? Severity.Warning;

            detectors.Add(new Detector(
                rule.Code,
                rule.Title,
                severity,
                string.IsNullOrWhiteSpace(rule.Category) ? "Custom" : rule.Category,
                string.IsNullOrWhiteSpace(rule.Recommendation)
                    ? "No recommendation was supplied with this rule."
                    : rule.Recommendation,
                rule.EffortDays,
                rule.Names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList())
            {
                Origin = Detector.OriginCustom,
                FileGlob = rule.FileGlob,
                ProjectGlob = rule.ProjectGlob,
            });
        }

        return detectors;
    }

    public static Dictionary<string, Severity> ToOverrides(RuleSet rules)
    {
        var overrides = new Dictionary<string, Severity>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, severity) in rules.SeverityOverrides)
        {
            if (ParseSeverity(severity) is { } parsed) overrides[code] = parsed;
        }
        return overrides;
    }

    public static Severity? ParseSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "info" or "informational" => Severity.Info,
        "warning" or "warn" => Severity.Warning,
        "blocker" or "error" or "critical" => Severity.Blocker,
        _ => null,
    };

    /// <summary>A starter file, so nobody has to guess the schema.</summary>
    public static string Template() => """
        {
          "format": "nettriage-rules/1",
          "rules": [
            {
              "code": "ORG001",
              "title": "Banned logging call",
              "severity": "warning",
              "category": "House rules",
              "recommendation": "Use ILogger. Log4Net writes to a file share that is not available in containers.",
              "effortDays": 2,
              "names": [ "log4net.Config.XmlConfigurator", "log4net.LogManager" ],
              "fileGlob": "src/**/*.cs"
            }
          ],
          "severityOverrides": {
            "NT1024": "warning"
          },
          "allowlist": [
            {
              "detector": "NT1010",
              "file": "src/Printing/**",
              "reason": "System.Drawing is used to drive label printers on Windows only; that code is out of scope.",
              "expires": "2027-06-30"
            }
          ]
        }
        """ + "\n";
}
