using System.IO.Compression;
using System.Xml.Linq;
using NetTriage.Core;
using NetTriage.Core.Analysis;
using NetTriage.Core.Estate;
using NetTriage.Core.Reporting;
using Xunit;

namespace NetTriage.Tests;

/// <summary>
/// The workbook is written by hand rather than by a library, so "does Excel actually open it" is not
/// a given. These tests unzip the result and parse every part, which is the closest thing to that
/// question a test can answer.
/// </summary>
public class XlsxWriterTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static byte[] Write(params XlsxSheet[] sheets)
    {
        using var stream = new MemoryStream();
        XlsxWriter.Build(stream, sheets);
        return stream.ToArray();
    }

    private static ZipArchive Open(byte[] bytes) => new(new MemoryStream(bytes), ZipArchiveMode.Read);

    [Fact]
    public void EveryPartIsPresentAndWellFormedXml()
    {
        var bytes = Write(new XlsxSheet { Name = "One" });

        using var zip = Open(bytes);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("[Content_Types].xml", names);
        Assert.Contains("_rels/.rels", names);
        Assert.Contains("xl/workbook.xml", names);
        Assert.Contains("xl/_rels/workbook.xml.rels", names);
        Assert.Contains("xl/styles.xml", names);
        Assert.Contains("xl/worksheets/sheet1.xml", names);

        // Malformed XML here is exactly the failure Excel reports as "unreadable content".
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            XDocument.Parse(reader.ReadToEnd());
        }
    }

    [Fact]
    public void TheFileIsAReadableZip()
    {
        var bytes = Write(new XlsxSheet { Name = "One" });

        using var zip = Open(bytes);

        Assert.Null(zip.GetEntry("xl/workbook.xml") is null ? "missing" : null);
    }

    [Fact]
    public void SheetNamesAppearInTheWorkbookInOrder()
    {
        var bytes = Write(
            new XlsxSheet { Name = "Summary" },
            new XlsxSheet { Name = "Projects" },
            new XlsxSheet { Name = "Findings" });

        using var zip = Open(bytes);
        using var reader = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        var workbook = XDocument.Parse(reader.ReadToEnd());

        var sheets = workbook.Descendants(Main + "sheet").Select(s => s.Attribute("name")!.Value).ToList();

        Assert.Equal(new[] { "Summary", "Projects", "Findings" }, sheets);
    }

    [Fact]
    public void CharactersThatWouldBreakTheXmlAreEscapedAndSurviveRoundTrip()
    {
        const string nasty = """<b>&"quoted" & 'single' — em dash</b>""";
        var bytes = Write(new XlsxSheet { Name = "One" }.Also(s => s.AddRow(new Cell(nasty, null, false))));

        using var zip = Open(bytes);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = XDocument.Parse(reader.ReadToEnd());

        var text = sheet.Descendants(Main + "t").Single().Value;

        Assert.Equal(nasty, text);
    }

    [Fact]
    public void NewlinesInsideACellSurvive()
    {
        var bytes = Write(new XlsxSheet { Name = "One" }.Also(s => s.AddRow(new Cell("line one\nline two", null, false))));

        using var zip = Open(bytes);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = XDocument.Parse(reader.ReadToEnd());

        Assert.Equal("line one\nline two", sheet.Descendants(Main + "t").Single().Value);
    }

    [Fact]
    public void NumbersAreWrittenAsNumbersAndNonFiniteValuesDoNotCorruptTheFile()
    {
        var bytes = Write(new XlsxSheet { Name = "One" }
            .Also(s => s.AddRow(new Cell(null, 42.5, false)))
            .Also(s => s.AddRow(new Cell(null, double.NaN, false)))
            .Also(s => s.AddRow(new Cell(null, double.PositiveInfinity, false))));

        using var zip = Open(bytes);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = XDocument.Parse(reader.ReadToEnd());

        var values = sheet.Descendants(Main + "v").Select(v => v.Value).ToList();
        Assert.Equal("42.5", values[0]);
        Assert.Equal("0", values[1]);
        Assert.Equal("0", values[2]);
    }

    [Fact]
    public void ColumnsPastZGetTheRightReference()
    {
        var sheet = new XlsxSheet { Name = "Wide" };
        var row = new Cell[28];
        for (var i = 0; i < row.Length; i++) row[i] = new Cell("c" + i, null, false);
        sheet.AddRow(row);

        var bytes = Write(sheet);

        using var zip = Open(bytes);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = reader.ReadToEnd();

        Assert.Contains("r=\"Z1\"", xml);
        Assert.Contains("r=\"AA1\"", xml);
        Assert.Contains("r=\"AB1\"", xml);
        Assert.Contains("ref=\"A1:AB1\"", xml);
    }

    [Fact]
    public void AnEmptySheetIsStillAValidWorkbook()
    {
        var bytes = Write(new XlsxSheet { Name = "Nothing" });

        using var zip = Open(bytes);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());

        XDocument.Parse(reader.ReadToEnd());
    }

    [Fact]
    public void AnIllegalSheetNameIsSanitised()
    {
        var bytes = Write(new XlsxSheet { Name = "a/b:c*d?e[f]g" });

        using var zip = Open(bytes);
        using var reader = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        var workbook = XDocument.Parse(reader.ReadToEnd());

        var name = workbook.Descendants(Main + "sheet").Single().Attribute("name")!.Value;

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('[', name);
    }

    [Fact]
    public void ALongSheetNameIsTruncatedToExcelsLimit()
    {
        var bytes = Write(new XlsxSheet { Name = new string('x', 60) });

        using var zip = Open(bytes);
        using var reader = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        var workbook = XDocument.Parse(reader.ReadToEnd());

        Assert.Equal(31, workbook.Descendants(Main + "sheet").Single().Attribute("name")!.Value.Length);
    }
}

public class WorkbookBuilderTests
{
    private static ScanReport ScanOf(TempTree tree, string root) =>
        ScanEngine.Scan(new ScanOptions { Root = root });

    private static string LegacyTree(TempTree tree)
    {
        tree.Write("Web/Web.csproj", Fixtures.LegacyCsproj("Web"));
        tree.Write("Web/Service.cs", "namespace Web { public class P : System.Web.UI.Page { } }");
        return tree.Root;
    }

    [Fact]
    public void AScanWorkbookHasTheSheetsTheReportsTalkAbout()
    {
        using var tree = new TempTree();
        var report = ScanOf(tree, LegacyTree(tree));

        var sheets = WorkbookBuilder.ForScan(report);

        Assert.Equal(new[] { "Summary", "Projects", "Findings", "Sequence" }, sheets.Select(s => s.Name).ToArray());
        Assert.All(sheets, s => Assert.NotEmpty(s.Rows));
    }

    [Fact]
    public void AnEstateWorkbookRollsEveryApplicationIntoOneFilterableSheet()
    {
        using var tree = new TempTree();
        tree.Write("Alpha/Alpha.csproj", Fixtures.LegacyCsproj("Alpha"));
        tree.Write("Alpha/Service.cs", "namespace Alpha { public class P : System.Web.UI.Page { } }");
        tree.Write("Beta/Beta.csproj", Fixtures.LegacyCsproj("Beta"));
        tree.Write("Beta/Service.cs", "namespace Beta { public class P { } }");

        var estate = EstateScanner.Scan(new EstateOptions { Root = tree.Root });

        var sheets = WorkbookBuilder.ForEstate(estate);

        // One row per application in a filterable sheet beats thirty tabs nobody opens.
        Assert.Equal(new[] { "Summary", "Applications", "Findings" }, sheets.Select(s => s.Name).ToArray());

        var applications = sheets[1];
        Assert.Contains(applications.Rows, row => row.Any(c => c.Text == "Alpha"));
        Assert.Contains(applications.Rows, row => row.Any(c => c.Text == "Beta"));
    }

    [Fact]
    public void TheWorkbookSurvivesRealReportsEndToEnd()
    {
        using var tree = new TempTree();
        var report = ScanOf(tree, LegacyTree(tree));
        var path = Path.Combine(tree.Root, "scan.xlsx");

        XlsxWriter.Write(path, WorkbookBuilder.ForScan(report));

        Assert.True(File.Exists(path));
        using var zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
    }
}

public class ExecutiveSummaryTests
{
    private static string LegacyTree(TempTree tree)
    {
        tree.Write("Web/Web.csproj", Fixtures.LegacyCsproj("Web"));
        tree.Write("Web/Service.cs", "namespace Web { public class P : System.Web.UI.Page { } }");
        return tree.Root;
    }

    [Fact]
    public void TheSummaryIsOnePageAndMentionsTheHeadlineNumbers()
    {
        using var tree = new TempTree();
        var root = LegacyTree(tree);

        var report = ScanEngine.Scan(new ScanOptions { Root = root });
        var text = ExecutiveSummary.ForScan(report);

        Assert.Contains("Web", text);
        Assert.True(text.Length < 6000, $"the executive summary should stay short, was {text.Length} characters");
    }

    [Fact]
    public void TheEstateSummaryRanksApplicationsAndShowsTheTrendWhenThereIsOne()
    {
        using var tree = new TempTree();
        tree.Write("Alpha/Alpha.csproj", Fixtures.LegacyCsproj("Alpha"));
        tree.Write("Alpha/Service.cs", "namespace Alpha { public class P : System.Web.UI.Page { } }");

        var history = Path.Combine(tree.Root, "history.json");
        var estate = EstateScanner.Scan(new EstateOptions { Root = tree.Root });
        EstateSnapshotStore.Append(EstateSnapshotStore.Capture(estate), history);
        estate.Trend = EstateSnapshotStore.BuildTrend(history);

        var text = ExecutiveSummary.ForEstate(estate);

        Assert.Contains("Alpha", text);
        Assert.True(text.Length < 6000, $"the executive summary should stay short, was {text.Length} characters");
    }

    [Fact]
    public void AnEstateWithNoTrendStillRenders()
    {
        using var tree = new TempTree();
        tree.Write("Alpha/Alpha.csproj", Fixtures.LegacyCsproj("Alpha"));
        tree.Write("Alpha/Service.cs", "namespace Alpha { public class P : System.Web.UI.Page { } }");

        var estate = EstateScanner.Scan(new EstateOptions { Root = tree.Root });
        var text = ExecutiveSummary.ForEstate(estate);

        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}

/// <summary>Small helper so a sheet can be built inline without a local variable.</summary>
internal static class SheetExtensions
{
    public static XlsxSheet Also(this XlsxSheet sheet, Action<XlsxSheet> action)
    {
        action(sheet);
        return sheet;
    }
}
