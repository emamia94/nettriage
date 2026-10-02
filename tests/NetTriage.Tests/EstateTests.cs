using NetTriage.Core;
using NetTriage.Core.Analysis;
using NetTriage.Core.Estate;
using Xunit;

namespace NetTriage.Tests;

/// <summary>
/// The estate roll-up is a ranking, and a ranking is only useful if the same application keeps its
/// identity between runs. Both are pinned down here.
/// </summary>
public class EstateTests
{
    private const string WebFormsPage = """
        namespace Legacy.Web
        {
            public class Default : System.Web.UI.Page
            {
            }
        }
        """;

    private const string CleanCode = """
        namespace Clean
        {
            public class Thing
            {
                public int Add(int a, int b) => a + b;
            }
        }
        """;

    private static void WriteApp(TempTree tree, string directory, string name, string source)
    {
        // The legacy project file declares Service.cs, so the source has to live there: a file the
        // project does not compile is deliberately not scanned.
        tree.Write($"{directory}/{name}.csproj", Fixtures.LegacyCsproj(name));
        tree.Write($"{directory}/Service.cs", source);
    }

    private static EstateReport Scan(TempTree tree) =>
        EstateScanner.Scan(new EstateOptions { Root = tree.Root });

    [Fact]
    public void EachDirectoryWithAProjectFileBecomesAnApplication()
    {
        using var tree = new TempTree();
        WriteApp(tree, "Alpha", "Alpha", WebFormsPage);
        WriteApp(tree, "Beta", "Beta", CleanCode);

        var estate = Scan(tree);

        Assert.Equal(2, estate.Apps.Count);
        Assert.Equal(new[] { "Alpha", "Beta" }, estate.Apps.Select(a => a.Name).OrderBy(n => n).ToArray());
        Assert.Equal(2, estate.Summary.AppCount);
    }

    [Fact]
    public void AnApplicationKeptInASrcFolderIsNamedAfterItsRepository()
    {
        using var tree = new TempTree();
        WriteApp(tree, "umbraco7/src", "Umbraco", WebFormsPage);

        var estate = Scan(tree);

        var app = Assert.Single(estate.Apps);
        Assert.Equal("umbraco7", app.Name);
        Assert.Equal("umbraco7/src", app.Path.Replace('\\', '/'));
    }

    [Fact]
    public void TheSameDirectoryProducesTheSameIdentityTwice()
    {
        using var tree = new TempTree();
        WriteApp(tree, "Alpha", "Alpha", WebFormsPage);

        Assert.Equal(Scan(tree).Apps[0].Id, Scan(tree).Apps[0].Id);
        Assert.False(string.IsNullOrWhiteSpace(Scan(tree).Apps[0].IdentitySource));
    }

    [Fact]
    public void TwoApplicationsDoNotShareAnIdentity()
    {
        using var tree = new TempTree();
        WriteApp(tree, "Alpha", "Alpha", WebFormsPage);
        WriteApp(tree, "Beta", "Beta", CleanCode);

        var ids = Scan(tree).Apps.Select(a => a.Id).ToList();

        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public void TheWorstApplicationRanksFirst()
    {
        using var tree = new TempTree();
        WriteApp(tree, "Clean", "Clean", CleanCode);
        WriteApp(tree, "Messy", "Messy", WebFormsPage);

        var estate = Scan(tree);

        Assert.Equal("Messy", estate.Ranked[0].Name);
        Assert.True(estate.Ranked[0].RiskScore > estate.Ranked[1].RiskScore);
    }

    [Fact]
    public void TheSummaryAddsUpTheApplications()
    {
        using var tree = new TempTree();
        WriteApp(tree, "Alpha", "Alpha", WebFormsPage);
        WriteApp(tree, "Beta", "Beta", WebFormsPage);

        var estate = Scan(tree);

        Assert.Equal(2, estate.Summary.AppCount);
        Assert.Equal(2, estate.Summary.Projects);
        Assert.Equal(estate.Apps.Sum(a => a.Blockers), estate.Summary.Blockers);
        Assert.Equal(estate.Apps.Sum(a => a.Warnings), estate.Summary.Warnings);
        Assert.True(estate.Summary.EffortHighDays >= estate.Summary.EffortLowDays);
    }

    [Fact]
    public void AnEmptyDirectoryReportsNoApplicationsRatherThanCrashing()
    {
        using var tree = new TempTree();
        tree.Dir("nothing/here");

        var estate = Scan(tree);

        Assert.Empty(estate.Apps);
        Assert.Equal(0, estate.Summary.AppCount);
    }

    [Fact]
    public void AnExcludedDirectoryIsLeftOutOfTheEstate()
    {
        using var tree = new TempTree();
        WriteApp(tree, "Alpha", "Alpha", WebFormsPage);
        WriteApp(tree, "Vendor/ThirdParty", "ThirdParty", WebFormsPage);

        var estate = EstateScanner.Scan(new EstateOptions { Root = tree.Root, Exclude = { "**/Vendor/**" } });

        Assert.Single(estate.Apps);
        Assert.Equal("Alpha", estate.Apps[0].Name);
    }

    [Fact]
    public void CustomRulesApplyToEveryApplicationInTheEstate()
    {
        using var tree = new TempTree();
        WriteApp(tree, "Alpha", "Alpha", WebFormsPage);
        WriteApp(tree, "Beta", "Beta", WebFormsPage);

        var rules = new Core.Rules.RuleSet
        {
            Rules =
            {
                new Core.Rules.CustomRule
                {
                    Code = "ORG001",
                    Title = "House rule",
                    Severity = "blocker",
                    Recommendation = "x",
                    Names = { "System.Web.UI.Page" },
                },
            },
        };

        var estate = EstateScanner.Scan(new EstateOptions { Root = tree.Root, Rules = rules });

        Assert.All(estate.Apps, app =>
            Assert.Contains(app.Report.Projects.SelectMany(p => p.Hits), h => h.Detector.Code == "ORG001"));
    }
}

public class EstateTrendTests
{
    private const string WebFormsPage = """
        namespace Legacy.Web
        {
            public class Default : System.Web.UI.Page
            {
            }
        }
        """;

    private static EstateReport Scan(string root) => EstateScanner.Scan(new EstateOptions { Root = root });

    private static string BuildTree(TempTree tree)
    {
        tree.Write("Alpha/Alpha.csproj", Fixtures.LegacyCsproj("Alpha"));
        tree.Write("Alpha/Service.cs", WebFormsPage);
        return tree.Root;
    }

    [Fact]
    public void AppendingASnapshotTwiceAndComparingShowsNoChange()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var history = Path.Combine(tree.Root, "history.json");

        EstateSnapshotStore.Append(EstateSnapshotStore.Capture(Scan(root)), history);
        EstateSnapshotStore.Append(EstateSnapshotStore.Capture(Scan(root)), history);

        var trend = EstateSnapshotStore.BuildTrend(history);

        Assert.NotNull(trend);
        Assert.Equal(0, trend!.NetBlockerChange);
    }

    [Fact]
    public void ANewBlockerBetweenSnapshotsShowsUpAsAWorseTrend()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var history = Path.Combine(tree.Root, "history.json");

        EstateSnapshotStore.Append(EstateSnapshotStore.Capture(Scan(root)), history);

        // A whole new application is the clearest kind of regression to show up in a trend.
        tree.Write("Beta/Beta.csproj", Fixtures.LegacyCsproj("Beta"));
        tree.Write("Beta/Service.cs", WebFormsPage);

        EstateSnapshotStore.Append(EstateSnapshotStore.Capture(Scan(root)), history);

        var trend = EstateSnapshotStore.BuildTrend(history);

        Assert.NotNull(trend);
        Assert.True(trend!.NetBlockerChange > 0);
        Assert.Contains(trend.Entries, e => e.BlockerDelta > 0);
    }

    [Fact]
    public void FixingAnApplicationBetweenSnapshotsShowsUpAsABetterTrend()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var history = Path.Combine(tree.Root, "history.json");

        EstateSnapshotStore.Append(EstateSnapshotStore.Capture(Scan(root)), history);
        tree.Write("Alpha/Service.cs", "namespace Alpha { public class Thing { } }");
        EstateSnapshotStore.Append(EstateSnapshotStore.Capture(Scan(root)), history);

        var trend = EstateSnapshotStore.BuildTrend(history);

        Assert.NotNull(trend);
        Assert.True(trend!.NetBlockerChange < 0);
    }

    [Fact]
    public void TheSnapshotKeepsOnlyAggregatesNotTheWholeReport()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);

        var snapshot = EstateSnapshotStore.Capture(Scan(root));

        var app = Assert.Single(snapshot.Apps);
        Assert.Equal("Alpha", app.Name);
        Assert.True(app.Blockers > 0);
        Assert.Equal("red", app.Bucket);

        // Nothing location-shaped should be in there: the history stays small and diffable.
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("hits", json);
        Assert.DoesNotContain("samples", json);
    }

    [Fact]
    public void ACorruptHistoryDoesNotStopAScan()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var history = tree.Write("history.json", "{ this is not json");

        // A broken file must not throw: it is replaced by a fresh history.
        EstateSnapshotStore.Append(EstateSnapshotStore.Capture(Scan(root)), history);
        EstateSnapshotStore.Append(EstateSnapshotStore.Capture(Scan(root)), history);

        var trend = EstateSnapshotStore.BuildTrend(history);

        Assert.NotNull(trend);
        Assert.Equal(0, trend!.NetBlockerChange);
    }

    [Fact]
    public void ASingleSnapshotHasNoTrendToReport()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var history = Path.Combine(tree.Root, "history.json");

        EstateSnapshotStore.Append(EstateSnapshotStore.Capture(Scan(root)), history);

        // One point is not a trend. Callers get null and render no trend section.
        Assert.Null(EstateSnapshotStore.BuildTrend(history));
    }

    [Fact]
    public void HistoryIsCappedSoItCannotGrowForever()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var history = Path.Combine(tree.Root, "history.json");

        for (var i = 0; i < 12; i++)
        {
            EstateSnapshotStore.Append(EstateSnapshotStore.Capture(Scan(root)), history, keepLast: 5);
        }

        var stored = EstateSnapshotStore.Load(history);

        Assert.Equal(5, stored.Snapshots.Count);
    }
}
