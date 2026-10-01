using NetTriage.Core.Analysis;
using Xunit;

namespace NetTriage.Tests;

/// <summary>
/// The comment/string stripper is the component most likely to produce false positives, so it is
/// tested directly rather than only through the end-to-end path.
/// </summary>
public class SourceTextTests
{
    [Fact]
    public void RemovesLineCommentsButKeepsCode()
    {
        var source = """
            var a = 1; // BinaryFormatter should not be seen here
            var b = 2;
            """;

        var stripped = SourceText.Strip(source, Language.CSharp);

        Assert.DoesNotContain("BinaryFormatter", stripped);
        Assert.Contains("var b = 2;", stripped);
    }

    [Fact]
    public void RemovesBlockCommentsAcrossLines()
    {
        var source = """
            var a = 1;
            /* System.Web.UI.Page
               AppDomain.CreateDomain */
            var b = 2;
            """;

        var stripped = SourceText.Strip(source, Language.CSharp);

        Assert.DoesNotContain("System.Web.UI.Page", stripped);
        Assert.DoesNotContain("AppDomain.CreateDomain", stripped);
        Assert.Contains("var b = 2;", stripped);
    }

    [Fact]
    public void RemovesStringLiteralContents()
    {
        var source = """var note = "BinaryFormatter and System.Web.UI.Page"; var after = 3;""";

        var stripped = SourceText.Strip(source, Language.CSharp);

        Assert.DoesNotContain("BinaryFormatter", stripped);
        Assert.DoesNotContain("System.Web.UI.Page", stripped);
        Assert.Contains("after", stripped);
    }

    [Fact]
    public void HandlesEscapedQuotesInsideStrings()
    {
        var source = """var s = "he said \"BinaryFormatter\" loudly"; var after = 1;""";

        var stripped = SourceText.Strip(source, Language.CSharp);

        Assert.DoesNotContain("BinaryFormatter", stripped);
        Assert.Contains("after", stripped);
    }

    [Fact]
    public void HandlesVerbatimStrings()
    {
        var source = """var p = @"C:\temp\BinaryFormatter"; var after = 1;""";

        var stripped = SourceText.Strip(source, Language.CSharp);

        Assert.DoesNotContain("BinaryFormatter", stripped);
        Assert.Contains("after", stripped);
    }

    [Fact]
    public void HandlesInterpolatedAndRawStrings()
    {
        var source = "var a = $\"x {value} BinaryFormatter\";\n" +
                     "var b = \"\"\"raw System.Web.UI.Page\"\"\";\n" +
                     "var after = 1;";

        var stripped = SourceText.Strip(source, Language.CSharp);

        Assert.DoesNotContain("BinaryFormatter", stripped);
        Assert.DoesNotContain("System.Web.UI.Page", stripped);
        Assert.Contains("after", stripped);
    }

    [Fact]
    public void PreservesLineCountAndOffsets()
    {
        var source = "line1 // comment\nline2\n/* multi\nline\ncomment */\nline6\n";

        var stripped = SourceText.Strip(source, Language.CSharp);

        Assert.Equal(source.Length, stripped.Length);
        Assert.Equal(source.Count(c => c == '\n'), stripped.Count(c => c == '\n'));
    }

    [Fact]
    public void TreatsSingleQuoteAsCommentInVisualBasic()
    {
        var source = """
            Dim a = 1 ' BinaryFormatter here
            Dim b = 2
            """;

        var stripped = SourceText.Strip(source, Language.VisualBasic);

        Assert.DoesNotContain("BinaryFormatter", stripped);
        Assert.Contains("Dim b = 2", stripped);
    }

    [Fact]
    public void HandlesVisualBasicDoubledQuoteEscape()
    {
        var source = """Dim s = "say ""BinaryFormatter"" now" : Dim after = 1""";

        var stripped = SourceText.Strip(source, Language.VisualBasic);

        Assert.DoesNotContain("BinaryFormatter", stripped);
        Assert.Contains("after", stripped);
    }

    [Fact]
    public void DetectsLanguageFromExtension()
    {
        Assert.Equal(Language.VisualBasic, SourceText.DetectLanguage("Foo.vb"));
        Assert.Equal(Language.CSharp, SourceText.DetectLanguage("Foo.cs"));
    }

    [Fact]
    public void LineIndexMapsOffsetsToLines()
    {
        var text = "a\nbb\nccc\n";
        var index = new LineIndex(text);

        Assert.Equal(1, index.LineOf(0));
        Assert.Equal(2, index.LineOf(2));
        Assert.Equal(3, index.LineOf(5));
    }
}
