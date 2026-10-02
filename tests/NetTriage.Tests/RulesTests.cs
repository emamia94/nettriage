using NetTriage.Core;
using NetTriage.Core.Analysis;
using NetTriage.Core.Rules;
using Xunit;

namespace NetTriage.Tests;

/// <summary>
/// Custom rules are the feature a customer points at their own house style, so a rule that silently
/// does nothing is worse than a rule that fails to load. Both halves are tested: the glob engine on
/// its own, and the effect on a real scan.
/// </summary>
public class GlobMatcherTests
{
    [Theory]
    // A trailing ** must match everything under the prefix, including a file directly in it.
    [InlineData("src/Printing/Label.cs", "src/Printing/**", true)]
    [InlineData("src/Printing/Deep/Label.cs", "src/Printing/**", true)]
    [InlineData("src/PrintingX/Label.cs", "src/Printing/**", false)]
    [InlineData("src/a/b.cs", "src/**", true)]
    // **/ means zero or more segments.
    [InlineData("src/a/b.cs", "src/**/*.cs", true)]
    [InlineData("src/b.cs", "src/**/*.cs", true)]
    [InlineData("b.cs", "**/*.cs", true)]
    [InlineData("a/b/c.cs", "**/*.cs", true)]
    [InlineData("a/b/c.txt", "**/*.cs", false)]
    // A single * never crosses a separator.
    [InlineData("src/a/b.cs", "src/*.cs", false)]
    [InlineData("src/b.cs", "src/*.cs", true)]
    [InlineData("Label.cs", "*.cs", true)]
    [InlineData("src/Label.cs", "*.cs", false)]
    // ? is exactly one character.
    [InlineData("a1.cs", "a?.cs", true)]
    [InlineData("a12.cs", "a?.cs", false)]
    // Literal text is matched literally, dots included.
    [InlineData("src/Nancy.csproj", "src/*.csproj", true)]
    [InlineData("src/NancyXcsproj", "src/*.csproj", false)]
    public void MatchesTheWayPeopleExpect(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(path, pattern));
    }

    [Fact]
    public void WindowsSeparatorsAreTreatedAsSeparators()
    {
        // Rule files get written on Windows by people who type backslashes.
        Assert.True(GlobMatcher.IsMatch("src/Printing/Label.cs", @"src\Printing\**"));
        Assert.True(GlobMatcher.IsMatch(@"src\Printing\Label.cs", "src/Printing/**"));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveLikeWindowsAndMsbuildAre()
    {
        Assert.True(GlobMatcher.IsMatch("Src/Printing/Label.cs", "src/printing/**"));
    }

    [Fact]
    public void ADoubleStarIsTheMatchEverythingWildcard()
    {
        Assert.True(GlobMatcher.IsMatchAny("anything/at/all.cs", new[] { "**" }));
    }

    [Fact]
    public void ASingleStarDeliberatelyStaysInsideOneSegment()
    {
        Assert.False(GlobMatcher.IsMatch("anything/at/all.cs", "*"));
        Assert.True(GlobMatcher.IsMatch("all.cs", "*"));
    }
}

public class RuleLoaderTests
{
    private static RuleSet? Load(string json, out List<string> errors)
    {
        var path = Path.Combine(Path.GetTempPath(), "nettriage-rules-" + Guid.NewGuid().ToString("N")[..10] + ".json");
        File.WriteAllText(path, json);
        try
        {
            return RuleLoader.Load(path, out errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AWellFormedRuleFileLoads()
    {
        var rules = Load("""
            {
              "format": "nettriage-rules/1",
              "rules": [
                { "code": "ORG001", "title": "House rule", "severity": "warning",
                  "recommendation": "Do not do this.", "effortDays": 2,
                  "names": ["System.Drawing.Color"], "fileGlob": "src/**/*.cs" }
              ],
              "severityOverrides": { "NT1024": "warning" },
              "allowlist": [ { "detector": "NT1010", "reason": "Windows only" } ]
            }
            """, out var errors);

        Assert.NotNull(rules);
        Assert.Empty(errors);
        Assert.Single(rules!.Rules);
        Assert.Equal("ORG001", rules.Rules[0].Code);
        Assert.Equal(2, rules.Rules[0].EffortDays);
    }

    [Fact]
    public void ACustomRuleCannotShadowABuiltInDetector()
    {
        var rules = Load("""
            { "rules": [ { "code": "NT1001", "title": "Mine now", "severity": "info",
                           "recommendation": "x", "names": ["System.Web.UI.Page"] } ] }
            """, out var errors);

        Assert.Null(rules);
        Assert.Contains(errors, e => e.Contains("already used by a built-in detector"));
    }

    [Fact]
    public void EveryProblemInAFileIsReportedAtOnce()
    {
        var rules = Load("""
            {
              "rules": [ { "code": "ORG001", "severity": "catastrofico", "names": [] } ],
              "severityOverrides": { "NOPE": "warning" }
            }
            """, out var errors);

        Assert.Null(rules);
        Assert.Contains(errors, e => e.Contains("severity"));
        Assert.Contains(errors, e => e.Contains("names"));
        Assert.Contains(errors, e => e.Contains("title"));
        Assert.Contains(errors, e => e.Contains("NOPE"));
    }

    [Fact]
    public void AMissingRecommendationIsAWarningNotAnError()
    {
        var rules = Load("""
            { "rules": [ { "code": "ORG001", "title": "T", "severity": "warning",
                           "names": ["System.Drawing.Color"] } ] }
            """, out var errors);

        Assert.NotNull(rules);
        Assert.Empty(errors);

        var validation = RuleLoader.Validate(rules!);
        Assert.True(validation.IsValid);
        Assert.Contains(validation.Warnings, w => w.Contains("recommendation"));
    }

    [Fact]
    public void ANegativeEffortIsRejected()
    {
        var rules = Load("""
            { "rules": [ { "code": "ORG001", "title": "T", "severity": "warning",
                           "recommendation": "x", "effortDays": -3, "names": ["System.Drawing.Color"] } ] }
            """, out var errors);

        Assert.Null(rules);
        Assert.Contains(errors, e => e.Contains("negative"));
    }

    [Fact]
    public void TheTemplateTheToolPrintsIsItselfValid()
    {
        var path = Path.Combine(Path.GetTempPath(), "nettriage-template-" + Guid.NewGuid().ToString("N")[..10] + ".json");
        File.WriteAllText(path, RuleLoader.Template());

        try
        {
            var rules = RuleLoader.Load(path, out var errors);

            Assert.NotNull(rules);
            Assert.Empty(errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ToDetectorsProducesTheSameKindOfDetectorAsTheBuiltIns()
    {
        var rules = Load("""
            { "rules": [ { "code": "ORG001", "title": "House rule", "severity": "blocker",
                           "recommendation": "x", "effortDays": 4,
                           "names": ["System.Drawing.Color"] } ] }
            """, out _);

        var detectors = RuleLoader.ToDetectors(rules!);

        var detector = Assert.Single(detectors);
        Assert.Equal("ORG001", detector.Code);
        Assert.Equal(Severity.Blocker, detector.Severity);
        Assert.Equal(4, detector.EffortDays);
    }
}

public class RuleApplicationTests
{
    private const string Source = """
        using System.Drawing;
        using System.Web.UI;

        namespace Legacy.Web
        {
            public class Page1 : System.Web.UI.Page
            {
                public Color Pick() => Color.Red;
            }
        }
        """;

    private static string BuildTree(TempTree tree)
    {
        // The legacy project file declares Service.cs, so the source has to live there: a file the
        // project does not compile is deliberately not scanned.
        tree.Write("Web/Web.csproj", Fixtures.LegacyCsproj("Web"));
        tree.Write("Web/Service.cs", Source);
        return tree.Root;
    }

    /// <summary>
    /// Scans with no rules at all, so the allowlist can be applied explicitly with a chosen date.
    /// A scan that already carries rules applies its own allowlist during the scan.
    /// </summary>
    private static ScanReport Unfiltered(string root) => ScanEngine.Scan(new ScanOptions { Root = root });

    private static ScanReport Scan(string root, RuleSet rules) =>
        ScanEngine.Scan(new ScanOptions { Root = root, Rules = rules });

    [Fact]
    public void ACustomDetectorFiresLikeABuiltInOne()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);

        var without = Unfiltered(root);
        Assert.DoesNotContain(without.Projects.SelectMany(p => p.Hits), h => h.Detector.Code == "ORG001");

        var rules = new RuleSet
        {
            Rules =
            {
                new CustomRule
                {
                    Code = "ORG001",
                    Title = "House rule: System.Drawing in web code",
                    Severity = "blocker",
                    Recommendation = "Move rendering out of the page.",
                    EffortDays = 5,
                    Names = { "System.Drawing.Color" },
                },
            },
        };

        var with = Scan(root, rules);

        var hit = Assert.Single(with.Projects.SelectMany(p => p.Hits), h => h.Detector.Code == "ORG001");
        Assert.Equal(Severity.Blocker, hit.Detector.Severity);
        Assert.True(hit.Count > 0);
    }

    [Fact]
    public void ASeverityOverrideChangesTheBucketAndTheEstimate()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);

        var before = Unfiltered(root);
        Assert.Equal(1, before.Summary.Red);

        var rules = new RuleSet { SeverityOverrides = { ["NT1001"] = "info" } };
        var after = Scan(root, rules);

        var hit = after.Projects.SelectMany(p => p.Hits).First(h => h.Detector.Code == "NT1001");
        Assert.Equal(Severity.Info, hit.Detector.Severity);

        // Red/Yellow/Green count projects by bucket, not findings: the one project leaves red.
        Assert.Equal(0, after.Summary.Red);
        Assert.Equal(1, after.Summary.Yellow);
    }

    [Fact]
    public void AnAllowlistEntryRemovesTheFindingAndSaysWhy()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var report = Unfiltered(root);

        var rules = new RuleSet
        {
            Allowlist =
            {
                new AllowlistEntry
                {
                    Detector = "NT1001",
                    File = "Web/**",
                    Reason = "Scheduled for the Q1 rewrite.",
                },
            },
        };

        var warnings = new List<string>();
        var suppressed = RuleApplication.Apply(report, rules, warnings);

        var entry = Assert.Single(suppressed);
        Assert.Equal("NT1001", entry.Detector);
        Assert.Equal("Scheduled for the Q1 rewrite.", entry.Reason);
        Assert.DoesNotContain(report.Projects.SelectMany(p => p.Hits), h => h.Detector.Code == "NT1001");
        Assert.Equal(0, report.Summary.Red);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AnAllowlistEntryScopedToAnotherFileDoesNotApply()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var report = Unfiltered(root);

        var rules = new RuleSet
        {
            Allowlist = { new AllowlistEntry { Detector = "NT1001", File = "Somewhere/Else/**", Reason = "n/a" } },
        };

        var suppressed = RuleApplication.Apply(report, rules, new List<string>());

        Assert.Empty(suppressed);
        Assert.Contains(report.Projects.SelectMany(p => p.Hits), h => h.Detector.Code == "NT1001");
    }

    [Fact]
    public void AnExpiredAllowlistEntryStopsSuppressingAndSaysSo()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var report = Unfiltered(root);

        var rules = new RuleSet
        {
            Allowlist = { new AllowlistEntry { Detector = "NT1001", Reason = "Temporary", Expires = "2020-01-01" } },
        };

        var warnings = new List<string>();
        var suppressed = RuleApplication.Apply(report, rules, warnings, new DateOnly(2026, 10, 2));

        Assert.Empty(suppressed);
        Assert.Contains(report.Projects.SelectMany(p => p.Hits), h => h.Detector.Code == "NT1001");
        Assert.Contains(warnings, w => w.Contains("expired"));
    }

    [Fact]
    public void AnAllowlistEntryThatHasNotExpiredYetStillApplies()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var report = Unfiltered(root);

        var rules = new RuleSet
        {
            Allowlist = { new AllowlistEntry { Detector = "NT1001", Reason = "Until the rewrite", Expires = "2027-06-30" } },
        };

        var warnings = new List<string>();
        var suppressed = RuleApplication.Apply(report, rules, warnings, new DateOnly(2026, 10, 2));

        Assert.Single(suppressed);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AWildcardDetectorSuppressesEverythingInScope()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var report = Unfiltered(root);

        var rules = new RuleSet
        {
            Allowlist = { new AllowlistEntry { Detector = "*", Project = "Web/**", Reason = "Archived application" } },
        };

        var suppressed = RuleApplication.Apply(report, rules, new List<string>());

        Assert.NotEmpty(suppressed);
        Assert.Empty(report.Projects.SelectMany(p => p.Hits));
    }

    [Fact]
    public void TheScanItselfAppliesTheAllowlistSoTheCliDoesNotHaveTo()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);

        var rules = new RuleSet
        {
            Allowlist = { new AllowlistEntry { Detector = "NT1001", Reason = "Scheduled for the Q1 rewrite." } },
        };

        var report = Scan(root, rules);

        Assert.Single(report.Suppressed);
        Assert.DoesNotContain(report.Projects.SelectMany(p => p.Hits), h => h.Detector.Code == "NT1001");
    }
}
