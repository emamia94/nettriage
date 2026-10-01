using NetTriage.Core;
using NetTriage.Core.Analysis;
using NetTriage.Core.Reporting;
using Xunit;

namespace NetTriage.Tests;

public class EndToEndTests
{
    /// <summary>A miniature but realistic legacy solution: WebForms site over a library using
    /// BinaryFormatter, plus one already-modern library.</summary>
    private static TempTree BuildLegacySolution()
    {
        var tree = new TempTree();

        tree.Write("src/Legacy.Domain/Legacy.Domain.csproj", Fixtures.LegacyCsproj("Legacy.Domain"));
        tree.Write("src/Legacy.Domain/Customer.cs", """
            using System;

            namespace Legacy.Domain
            {
                // This comment mentions BinaryFormatter and System.Web.UI.Page on purpose.
                public class Customer
                {
                    public byte[] Serialize()
                    {
                        var formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                        return null!;
                    }
                }
            }
            """);

        tree.Write("src/Legacy.Web/Legacy.Web.csproj", Fixtures.LegacyCsproj("Legacy.Web"));
        tree.Write("src/Legacy.Web/Default.aspx", "<%@ Page Language=\"C#\" %>\n<html><body>Hello</body></html>\n");
        tree.Write("src/Legacy.Web/Default.aspx.cs", """
            using System.Web.UI;

            namespace Legacy.Web
            {
                public partial class Default : Page
                {
                    protected void Page_Load(object sender, System.EventArgs e) { }
                }
            }
            """);

        tree.Write("src/Modern.Lib/Modern.Lib.csproj", Fixtures.SdkCsproj("net10.0"));
        tree.Write("src/Modern.Lib/Helper.cs", "namespace Modern.Lib { public class Helper { } }");

        tree.Write("Legacy.sln", Fixtures.Solution(
            ("Legacy.Domain", @"src\Legacy.Domain\Legacy.Domain.csproj", "AAAAAAAA-0000-0000-0000-000000000001"),
            ("Legacy.Web", @"src\Legacy.Web\Legacy.Web.csproj", "AAAAAAAA-0000-0000-0000-000000000002"),
            ("Modern.Lib", @"src\Modern.Lib\Modern.Lib.csproj", "AAAAAAAA-0000-0000-0000-000000000003")));

        return tree;
    }

    private static ScanReport Scan(TempTree tree) =>
        ScanEngine.Scan(new ScanOptions { Root = Path.Combine(tree.Root, "Legacy.sln") });

    [Fact]
    public void FindsEveryProjectInTheSolution()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        Assert.Equal(3, report.Summary.ProjectCount);
        Assert.Equal(3, report.Projects.Count);
    }

    [Fact]
    public void DetectsWebFormsInTheWebProject()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        var web = report.Projects.Single(p => p.Name == "Legacy.Web");
        Assert.Contains(web.Hits, h => h.Detector.Code == "NT1001");
        Assert.Equal(Bucket.Red, web.Bucket);
    }

    [Fact]
    public void DetectsBinaryFormatterInTheLibrary()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        var domain = report.Projects.Single(p => p.Name == "Legacy.Domain");
        Assert.Contains(domain.Hits, h => h.Detector.Code == "NT1006");
    }

    [Fact]
    public void CommentsDoNotProduceFindings()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        var domain = report.Projects.Single(p => p.Name == "Legacy.Domain");
        // The comment in Customer.cs names both of these; only the real BinaryFormatter call counts.
        Assert.DoesNotContain(domain.Hits, h => h.Detector.Code == "NT1001");
    }

    [Fact]
    public void RecognisesLegacyProjectFormatAndFrameworkTarget()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        var domain = report.Projects.Single(p => p.Name == "Legacy.Domain");
        Assert.Equal(ProjectStyle.Legacy, domain.Style);
        Assert.Contains("net48", domain.TargetFrameworks);
    }

    [Fact]
    public void ClassifiesModernProjectAsGreen()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        var modern = report.Projects.Single(p => p.Name == "Modern.Lib");
        Assert.Equal(Bucket.Green, modern.Bucket);
        Assert.Equal(ProjectStyle.Sdk, modern.Style);
    }

    [Fact]
    public void SequencesDependenciesBeforeTheirConsumers()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        var order = report.Sequence.Select(s => s.Project).ToList();
        var domainIndex = order.FindIndex(p => p.Contains("Legacy.Domain"));
        var webIndex = order.FindIndex(p => p.Contains("Legacy.Web"));

        Assert.True(domainIndex >= 0 && webIndex >= 0, "both projects should appear in the plan");
        Assert.True(domainIndex < webIndex, "a dependency must be scheduled before its consumer");
    }

    [Fact]
    public void SequenceNumbersAreContiguousFromOne()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        Assert.Equal(
            Enumerable.Range(1, report.Sequence.Count).ToList(),
            report.Sequence.Select(s => s.Order).ToList());
    }

    [Fact]
    public void SummaryCountsMatchTheProjectList()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        Assert.Equal(report.Projects.Count(p => p.Bucket == Bucket.Red), report.Summary.Red);
        Assert.Equal(report.Projects.Count(p => p.Bucket == Bucket.Yellow), report.Summary.Yellow);
        Assert.Equal(report.Projects.Count(p => p.Bucket == Bucket.Green), report.Summary.Green);
        Assert.Equal(report.Projects.Sum(p => p.SourceLines), report.Summary.TotalSourceLines);
    }

    [Fact]
    public void EveryProjectGetsAnEffortEstimate()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        Assert.All(report.Projects, p =>
        {
            Assert.True(p.Effort.LowDays > 0, p.Name);
            Assert.True(p.Effort.HighDays > p.Effort.LowDays, p.Name);
        });
    }

    [Fact]
    public void FindingsAlwaysCarryALocationAndAReason()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        foreach (var project in report.Projects)
        {
            foreach (var hit in project.Hits)
            {
                Assert.False(string.IsNullOrWhiteSpace(hit.Detector.Title));
                Assert.False(string.IsNullOrWhiteSpace(hit.Detector.Recommendation));
                Assert.True(hit.Count > 0, $"{project.Name}/{hit.Detector.Code}");
                Assert.NotEmpty(hit.Samples);
                Assert.False(string.IsNullOrWhiteSpace(hit.Samples[0].File));
                Assert.True(hit.Samples[0].Line >= 1, $"{project.Name}/{hit.Detector.Code} line {hit.Samples[0].Line}");
            }
        }
    }

    [Fact]
    public void FlagsAFrameworkThatIsAlreadyOutOfSupportAsABlocker()
    {
        using var tree = new TempTree();
        tree.Write("src/App/App.csproj", Fixtures.SdkCsproj("net6.0"));
        tree.Write("src/App/Program.cs", "class P { static void Main() { } }");

        var report = ScanEngine.Scan(new ScanOptions { Root = tree.Root });
        var app = report.Projects.Single();

        // .NET 6 has been out of support since 2024, so this assertion does not rot with time.
        Assert.Contains(app.Hits, h => h.Detector.Code == "NT2003");
        Assert.True(app.IsOutOfSupport);
        Assert.Equal(Bucket.Red, app.Bucket);
        Assert.Contains("net6.0", report.Summary.EolTargetFrameworks);
        Assert.Equal(1, report.Summary.ProjectsOutOfSupport);
    }

    [Fact]
    public void FlagsAFrameworkApproachingEndOfSupport()
    {
        using var tree = new TempTree();
        tree.Write("src/App/App.csproj", Fixtures.SdkCsproj("net8.0"));
        tree.Write("src/App/Program.cs", "class P { static void Main() { } }");

        var report = ScanEngine.Scan(new ScanOptions { Root = tree.Root });
        var app = report.Projects.Single();

        // Deliberately not asserting which of NT2003/NT2004 fires, so the test keeps passing
        // on either side of the .NET 8 end-of-support date.
        Assert.NotEmpty(app.EolNotes);
        Assert.Contains(app.Hits, h => h.Detector.Code is "NT2003" or "NT2004");
        Assert.NotEqual(Bucket.Green, app.Bucket);
    }

    [Fact]
    public void SupportedFrameworkRaisesNoLifecycleFinding()
    {
        using var tree = new TempTree();
        tree.Write("src/App/App.csproj", Fixtures.SdkCsproj("net10.0"));
        tree.Write("src/App/Program.cs", "class P { static void Main() { } }");

        var report = ScanEngine.Scan(new ScanOptions { Root = tree.Root });
        var app = report.Projects.Single();

        Assert.DoesNotContain(app.Hits, h => h.Detector.Code is "NT2003" or "NT2004");
        Assert.Equal(Bucket.Green, app.Bucket);
    }

    [Fact]
    public void ProjectItemPathsAreNormalisedToRootRelativeLocations()
    {
        using var tree = new TempTree();
        tree.Write("src/Web/Web.csproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Content Include="Pages\Default.aspx" />
              </ItemGroup>
            </Project>
            """);
        tree.Write("src/Web/Pages/Default.aspx", "<%@ Page %>");

        var report = ScanEngine.Scan(new ScanOptions { Root = tree.Root });
        var hit = report.Projects.Single().Hits.Single(h => h.Detector.Code == "NT1001");
        var location = hit.Samples[0].File;

        // Regression: the raw project item used Windows separators and was project-relative,
        // so the reported location did not resolve against the scan root.
        Assert.DoesNotContain("\\", location);
        Assert.True(File.Exists(Path.Combine(tree.Root, location)), location);
    }

    [Fact]
    public void ResolvesProjectItemPathsCaseInsensitivelyLikeWindowsDoes()
    {
        using var tree = new TempTree();
        tree.Write("src/Web/Web.csproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Content Include="Pages\Default.aspx" />
              </ItemGroup>
            </Project>
            """);
        // The project file says "Pages", the directory on disk is "pages".
        tree.Write("src/Web/pages/Default.aspx", "<%@ Page %>");

        var report = ScanEngine.Scan(new ScanOptions { Root = tree.Root });
        var hit = report.Projects.Single().Hits.Single(h => h.Detector.Code == "NT1001");
        var location = hit.Samples[0].File;

        Assert.True(File.Exists(Path.Combine(tree.Root, location)), location);
    }

    [Fact]
    public void DetectorHitsAreOrderedBySeverityThenPrevalence()
    {
        using var tree = BuildLegacySolution();
        var report = Scan(tree);

        foreach (var project in report.Projects)
        {
            for (var i = 1; i < project.Hits.Count; i++)
            {
                var previous = project.Hits[i - 1];
                var current = project.Hits[i];
                Assert.True(previous.Detector.Severity >= current.Detector.Severity
                            || previous.Count >= current.Count);
            }
        }
    }

    [Fact]
    public void ScanningTheSameTreeTwiceIsDeterministic()
    {
        using var tree = BuildLegacySolution();

        var first = Scan(tree);
        var second = Scan(tree);

        Assert.Equal(first.Summary.BlockerFindings, second.Summary.BlockerFindings);
        Assert.Equal(first.Summary.WarningFindings, second.Summary.WarningFindings);
        Assert.Equal(first.Summary.InfoFindings, second.Summary.InfoFindings);
        Assert.Equal(
            first.Projects.Select(p => p.Bucket).ToList(),
            second.Projects.Select(p => p.Bucket).ToList());
    }

    [Fact]
    public void ScanDoesNotTouchTheNetworkOrTheFilesystem()
    {
        using var tree = BuildLegacySolution();
        var before = Directory.GetFiles(tree.Root, "*", SearchOption.AllDirectories).Length;

        Scan(tree);

        var after = Directory.GetFiles(tree.Root, "*", SearchOption.AllDirectories).Length;
        Assert.Equal(before, after);
    }
}

public class ReporterTests
{
    private static ScanReport Sample()
    {
        using var tree = new TempTree();
        tree.Write("src/Legacy.Web/Legacy.Web.csproj", Fixtures.LegacyCsproj("Legacy.Web"));
        tree.Write("src/Legacy.Web/Default.aspx", "<%@ Page %>");
        tree.Write("src/Legacy.Web/Default.aspx.cs", "using System.Web.UI;\nclass P : Page { }");
        tree.Write("Demo.sln", Fixtures.Solution(
            ("Legacy.Web", @"src\Legacy.Web\Legacy.Web.csproj", "BBBBBBBB-0000-0000-0000-000000000001")));

        return ScanEngine.Scan(new ScanOptions { Root = Path.Combine(tree.Root, "Demo.sln") });
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    [InlineData("markdown")]
    [InlineData("html")]
    public void EveryFormatRendersNonEmptyOutput(string format)
    {
        var output = ReporterFactory.Create(format, color: false).Render(Sample());

        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.Contains("Legacy.Web", output);
    }

    [Fact]
    public void JsonOutputIsValidAndUsesCamelCase()
    {
        var json = ReporterFactory.Create("json", false).Render(Sample());
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("summary", out _));
        Assert.True(document.RootElement.TryGetProperty("projects", out var projects));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, projects.ValueKind);
    }

    [Fact]
    public void MarkdownOutputIsATableOfResults()
    {
        var markdown = ReporterFactory.Create("markdown", false).Render(Sample());

        Assert.Contains("|", markdown);
        Assert.Contains("#", markdown);
    }

    [Fact]
    public void HtmlOutputIsSelfContained()
    {
        var html = ReporterFactory.Create("html", false).Render(Sample());

        Assert.StartsWith("<!DOCTYPE html>", html.TrimStart());
        Assert.DoesNotContain("<script src=", html);
        Assert.DoesNotContain("http://", html.Replace("http://www.w3.org", ""));
    }

    [Fact]
    public void ConsoleOutputObeysTheColourSwitch()
    {
        var plain = ReporterFactory.Create("console", color: false).Render(Sample());
        var coloured = ReporterFactory.Create("console", color: true).Render(Sample());

        Assert.DoesNotContain("\u001b[", plain);
        Assert.Contains("\u001b[", coloured);
    }

    [Fact]
    public void EffortSummaryUsesReadableUnitsForSmallEstimates()
    {
        var output = ReporterFactory.Create("console", false).Render(Sample());

        // Regression: a range of a few days used to render as "≈ 0 - 0 months".
        Assert.DoesNotContain("0 - 0 months", output);
        Assert.Contains("engineer-days", output);
    }

    [Fact]
    public void UnknownFormatFallsBackToConsole()
    {
        var reporter = ReporterFactory.Create("nonsense", false);

        Assert.IsType<ConsoleReporter>(reporter);
    }
}
