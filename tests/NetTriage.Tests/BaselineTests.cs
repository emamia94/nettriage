using NetTriage.Core;
using NetTriage.Core.Analysis;
using NetTriage.Core.Baseline;
using Xunit;

namespace NetTriage.Tests;

/// <summary>
/// The baseline exists to answer one question in CI: "did this change make things worse?". If it
/// fires on ordinary edits it gets switched off, so the tolerance for noise is tested explicitly.
/// </summary>
public class BaselineTests
{
    private const string Blocker = """
        using System.Runtime.Serialization.Formatters.Binary;

        namespace Legacy.Lib
        {
            public class Service
            {
                public object Load(byte[] data) =>
                    new BinaryFormatter().Deserialize(new System.IO.MemoryStream(data));
            }
        }
        """;

    private static string BuildTree(TempTree tree, string projectDirectory = "Legacy.Lib")
    {
        tree.Write($"{projectDirectory}/Legacy.Lib.csproj", Fixtures.LegacyCsproj("Legacy.Lib"));
        tree.Write($"{projectDirectory}/Service.cs", Blocker);
        return tree.Root;
    }

    private static ScanReport Scan(string root) => ScanEngine.Scan(new ScanOptions { Root = root });

    [Fact]
    public void TheSameTreeComparesCleanAgainstItsOwnBaseline()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);

        var baseline = BaselineComparer.Capture(Scan(root), "0.1.0");
        var diff = BaselineComparer.Compare(baseline, Scan(root));

        Assert.True(baseline.TotalFindings > 0);
        Assert.Equal(0, diff.NewOccurrences);
        Assert.Equal(0, diff.ResolvedOccurrences);
        Assert.Equal(0, diff.NewBlockers);
        Assert.False(diff.AnyNew);
    }

    [Fact]
    public void InsertingLinesDoesNotInvalidateTheBaseline()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var baseline = BaselineComparer.Capture(Scan(root), "0.1.0");

        // Move every finding down the file without changing any of them.
        tree.Write("Legacy.Lib/Service.cs", "// a comment\n// another\n" + Blocker);

        var diff = BaselineComparer.Compare(baseline, Scan(root));

        Assert.False(diff.AnyNew);
        Assert.Equal(0, diff.ResolvedOccurrences);
    }

    [Fact]
    public void OneMoreOccurrenceInAKnownProjectIsAGrowth()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var baseline = BaselineComparer.Capture(Scan(root), "0.1.0");

        tree.Write("Legacy.Lib/Service.cs", Blocker + """

            namespace Legacy.Lib
            {
                public class SecondService
                {
                    public object Load2(byte[] data) =>
                        new BinaryFormatter().Deserialize(new System.IO.MemoryStream(data));
                }
            }
            """);

        var diff = BaselineComparer.Compare(baseline, Scan(root));

        var grew = Assert.Single(diff.Deltas, d => d.Kind == DeltaKind.Grew);
        Assert.Equal("NT1006", grew.Detector);
        Assert.True(grew.NewCount > 0);
        Assert.Equal(grew.NewCount, diff.NewOccurrences);
        Assert.Equal(1, diff.NewBlockers);
        Assert.True(diff.AnyNew);
    }

    [Fact]
    public void RemovingTheOffendingCodeIsReportedAsResolved()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var baseline = BaselineComparer.Capture(Scan(root), "0.1.0");

        tree.Write("Legacy.Lib/Service.cs", """
            namespace Legacy.Lib
            {
                public class Service
                {
                    public int Add(int a, int b) => a + b;
                }
            }
            """);

        var diff = BaselineComparer.Compare(baseline, Scan(root));

        var resolved = Assert.Single(diff.Deltas, d => d.Kind == DeltaKind.Resolved);
        Assert.Equal("NT1006", resolved.Detector);
        Assert.True(diff.ResolvedOccurrences > 0);
        Assert.False(diff.AnyNew);
    }

    [Fact]
    public void AProjectThatWasNotInTheBaselineIsANewProject()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var baseline = BaselineComparer.Capture(Scan(root), "0.1.0");

        tree.Write("Extra/Extra.csproj", Fixtures.LegacyCsproj("Extra"));
        tree.Write("Extra/Service.cs", "namespace Extra { public class Thing { } }");

        var diff = BaselineComparer.Compare(baseline, Scan(root));

        var added = diff.Deltas.Where(d => d.Project == "Extra").ToList();
        Assert.NotEmpty(added);
        Assert.All(added, d => Assert.Equal(DeltaKind.NewProject, d.Kind));
        Assert.True(diff.AnyNew);
    }

    [Fact]
    public void CaptureRecordsCountsNotLocations()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);

        var baseline = BaselineComparer.Capture(Scan(root), "0.1.0");
        var project = Assert.Single(baseline.Projects);

        Assert.Equal("Legacy.Lib", project.Name);
        Assert.Equal("Legacy.Lib/Legacy.Lib.csproj", project.Path.Replace('\\', '/'));
        Assert.True(project.Findings["NT1006"] > 0);
        Assert.Equal("nettriage-baseline/1", baseline.Format);
        Assert.Equal("0.1.0", baseline.ToolVersion);
    }

    [Fact]
    public void SaveAndLoadPreserveEverythingThatMatters()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var original = BaselineComparer.Capture(Scan(root), "0.1.0");

        var path = Path.Combine(tree.Root, "baseline.json");
        BaselineStore.Save(original, path);
        var reloaded = BaselineStore.Load(path);

        Assert.NotNull(reloaded);
        Assert.Equal(original.Format, reloaded!.Format);
        Assert.Equal(original.ToolVersion, reloaded.ToolVersion);
        Assert.Equal(original.TotalFindings, reloaded.TotalFindings);
        Assert.Equal(original.Projects.Count, reloaded.Projects.Count);
        Assert.False(BaselineComparer.Compare(reloaded, Scan(root)).AnyNew);
    }

    [Fact]
    public void TwoRecordingsOfTheSameTreeDifferOnlyByTheirTimestamp()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);

        var first = BaselineStore.Serialize(BaselineComparer.Capture(Scan(root), "0.1.0"));
        var second = BaselineStore.Serialize(BaselineComparer.Capture(Scan(root), "0.1.0"));

        // A baseline that churns between runs produces a diff nobody reads. The recording time is
        // the one field that legitimately moves, so it is the only one allowed to.
        Assert.Equal(WithoutTimestamps(first), WithoutTimestamps(second));
        Assert.NotEqual(first, second);
    }

    private static string WithoutTimestamps(string json) =>
        string.Join('\n', json.Split('\n').Where(line => !line.Contains("\"createdAt\"")));

    [Fact]
    public void TryLoadExplainsAMissingFileInsteadOfThrowing()
    {
        using var tree = new TempTree();

        var ok = BaselineStore.TryLoad(Path.Combine(tree.Root, "nope.json"), out _, out var error);

        Assert.False(ok);
        Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryLoadRejectsAFileThatIsNotABaseline()
    {
        using var tree = new TempTree();
        var path = tree.Write("random.json", """{"hello":"world"}""");

        var ok = BaselineStore.TryLoad(path, out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void APathThatMovedDoesNotShowUpAsAllNew()
    {
        using var tree = new TempTree();
        var root = BuildTree(tree);
        var baseline = BaselineComparer.Capture(Scan(root), "0.1.0");

        // The report's project identity is the root-relative path, so re-rooting the whole tree
        // must not read as a rewrite of every project.
        var moved = Path.Combine(tree.Root, "moved");
        Directory.CreateDirectory(moved);
        Directory.Move(Path.Combine(root, "Legacy.Lib"), Path.Combine(moved, "Legacy.Lib"));
        tree.Write("moved/Legacy.Lib/Legacy.Lib.csproj", Fixtures.LegacyCsproj("Legacy.Lib"));

        var diff = BaselineComparer.Compare(baseline, Scan(moved));

        Assert.DoesNotContain(diff.Deltas, d => d.Kind == DeltaKind.NewProject);
    }
}
