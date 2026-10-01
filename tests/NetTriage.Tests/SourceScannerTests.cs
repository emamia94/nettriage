using NetTriage.Core;
using NetTriage.Core.Analysis;
using Xunit;

namespace NetTriage.Tests;

public class SourceScannerTests
{
    [Fact]
    public void ResolvesSimpleTypeNameThroughImport()
    {
        // `Page` alone means nothing; with `using System.Web.UI;` it is a WebForms page.
        var source = """
            using System.Web.UI;

            namespace Demo
            {
                public partial class Default : Page { }
            }
            """;

        var bag = new ScanBag();
        bag.Add(SourceScanner.Scan("Default.aspx.cs", source, Language.CSharp));

        var (count, _) = bag.ResolveAny(new[] { "System.Web.UI.Page" });

        Assert.True(count > 0, "expected System.Web.UI.Page to resolve via the import");
    }

    [Fact]
    public void DoesNotResolveTypeNameWithoutImport()
    {
        var source = """
            namespace Demo
            {
                public class Page { }
            }
            """;

        var bag = new ScanBag();
        bag.Add(SourceScanner.Scan("Page.cs", source, Language.CSharp));

        var (count, _) = bag.ResolveAny(new[] { "System.Web.UI.Page" });

        Assert.Equal(0, count);
    }

    [Fact]
    public void MatchesQualifiedMemberAccess()
    {
        var source = """
            using System;

            class C
            {
                void M()
                {
                    var d = AppDomain.CreateDomain("x");
                    d.DoWork();
                }
            }
            """;

        var bag = new ScanBag();
        bag.Add(SourceScanner.Scan("C.cs", source, Language.CSharp));

        var (count, first) = bag.Resolve("AppDomain.CreateDomain");

        Assert.Equal(1, count);
        Assert.NotNull(first);
        Assert.Equal(7, first!.Line);
    }

    [Fact]
    public void RecordsPrefixedNamesForLongerChains()
    {
        var source = "class C { void M() { var x = System.Web.HttpContext.Current; } }";

        var bag = new ScanBag();
        bag.Add(SourceScanner.Scan("C.cs", source, Language.CSharp));

        Assert.True(bag.Resolve("System.Web.HttpContext").Count > 0);
    }

    [Fact]
    public void IgnoresLowercaseInstanceMemberChains()
    {
        var source = "class C { void M() { var x = customer.BinaryFormatter.Thing; } }";

        var bag = new ScanBag();
        bag.Add(SourceScanner.Scan("C.cs", source, Language.CSharp));

        Assert.Equal(0, bag.Resolve("customer.BinaryFormatter.Thing").Count);
    }

    [Fact]
    public void CommentsAndStringsProduceNoFindings()
    {
        var source = """
            // BinaryFormatter lives in this comment
            /* System.Web.UI.Page lives in this block comment */
            class C
            {
                const string Note = "AppDomain.CreateDomain and BinaryFormatter";
            }
            """;

        var bag = new ScanBag();
        bag.Add(SourceScanner.Scan("C.cs", source, Language.CSharp));

        Assert.Equal(0, bag.Resolve("BinaryFormatter").Count);
        Assert.Equal(0, bag.Resolve("System.Web.UI.Page").Count);
        Assert.Equal(0, bag.Resolve("AppDomain.CreateDomain").Count);
    }

    [Fact]
    public void ScansVisualBasicSources()
    {
        var source = """
            Imports System.Web.UI

            Public Class Default
                Inherits Page
            End Class
            """;

        var bag = new ScanBag();
        bag.Add(SourceScanner.Scan("Default.aspx.vb", source, Language.VisualBasic));

        Assert.True(bag.Resolve("System.Web.UI.Page").Count > 0);
    }

    [Fact]
    public void EveryDetectorHasAUniqueCode()
    {
        var codes = DetectorCatalog.All.Select(d => d.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void SourceDetectorsAllCarryAtLeastOneName()
    {
        // Project-level detectors legitimately carry no names; everything else must.
        var projectLevel = new[] { "NT2001", "NT2002", "NT2003", "NT2004", "NT2005", "NT2006" };

        foreach (var detector in DetectorCatalog.All.Where(d => !projectLevel.Contains(d.Code)))
        {
            Assert.True(detector.Names.Count > 0, $"{detector.Code} has no names to match on");
        }
    }
}
