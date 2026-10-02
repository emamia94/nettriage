using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace NetTriage.Core.Reporting;

/// <summary>A spreadsheet cell: either text, a number, or empty. Numbers stay numbers so Excel can sort and sum them.</summary>
public readonly record struct Cell(string? Text, double? Number, bool Bold)
{
    public static Cell S(string? value) => new(value, null, false);
    public static Cell N(double value) => new(null, value, false);
    public static Cell Header(string value) => new(value, null, true);
    public static readonly Cell Empty = new(null, null, false);
}

public sealed class XlsxSheet
{
    public required string Name { get; init; }
    public List<List<Cell>> Rows { get; init; } = new();
    /// <summary>Column widths in Excel's character units. Index 0 is column A.</summary>
    public List<double> ColumnWidths { get; init; } = new();
    public bool FreezeHeader { get; init; } = true;
    public bool AutoFilter { get; init; } = true;

    public void AddRow(params Cell[] cells) => Rows.Add(cells.ToList());
}

/// <summary>
/// Writes a real .xlsx with no third-party dependency. An .xlsx is a zip of OOXML parts, so the
/// whole writer is a few hundred lines of XML - which is a better trade than taking a dependency
/// on a spreadsheet library for a tool whose selling point is that it has none.
/// </summary>
public static class XlsxWriter
{
    public static void Write(string path, IReadOnlyList<XlsxSheet> sheets)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = File.Create(full);
        Build(stream, sheets);
    }

    public static void Build(Stream stream, IReadOnlyList<XlsxSheet> sheets)
    {
        if (sheets.Count == 0) throw new ArgumentException("A workbook needs at least one sheet.", nameof(sheets));

        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        AddEntry(archive, "[Content_Types].xml", ContentTypes(sheets.Count));
        AddEntry(archive, "_rels/.rels", RootRelationships());
        AddEntry(archive, "xl/workbook.xml", Workbook(sheets));
        AddEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships(sheets.Count));
        AddEntry(archive, "xl/styles.xml", Styles());

        for (var i = 0; i < sheets.Count; i++)
        {
            AddEntry(archive, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i]));
        }
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // -- parts ---------------------------------------------------------------------------------

    private static string ContentTypes(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""");
        sb.Append("""<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>""");
        sb.Append("""<Default Extension="xml" ContentType="application/xml"/>""");
        sb.Append("""<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");
        sb.Append("""<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append($"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        }
        sb.Append("</Types>");
        return sb.ToString();
    }

    private static string RootRelationships() =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
        """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
        """<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>""" +
        "</Relationships>";

    private static string Workbook(IReadOnlyList<XlsxSheet> sheets)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
        sb.Append("<sheets>");
        for (var i = 0; i < sheets.Count; i++)
        {
            var name = Escape(SafeSheetName(sheets[i].Name, i));
            sb.Append($"""<sheet name="{name}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
        }
        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    private static string WorkbookRelationships(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append($"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>""");
        }
        sb.Append($"""<Relationship Id="rId{sheetCount + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string Styles() =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
        """<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""" +
        """<fonts count="2">""" +
        """<font><sz val="11"/><name val="Calibri"/></font>""" +
        """<font><b/><sz val="11"/><name val="Calibri"/></font>""" +
        "</fonts>" +
        """<fills count="2">""" +
        """<fill><patternFill patternType="none"/></fill>""" +
        """<fill><patternFill patternType="gray125"/></fill>""" +
        "</fills>" +
        """<borders count="1"><border/></borders>""" +
        """<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>""" +
        """<cellXfs count="2">""" +
        """<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>""" +
        """<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>""" +
        "</cellXfs>" +
        "</styleSheet>";

    private static string Sheet(XlsxSheet sheet)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");

        if (sheet.FreezeHeader && sheet.Rows.Count > 0)
        {
            sb.Append("""<sheetViews><sheetView workbookViewId="0">""");
            sb.Append("""<pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/>""");
            sb.Append("</sheetView></sheetViews>");
        }

        var columnCount = sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(r => r.Count);

        if (columnCount > 0)
        {
            sb.Append("<cols>");
            for (var c = 0; c < columnCount; c++)
            {
                var width = c < sheet.ColumnWidths.Count ? sheet.ColumnWidths[c] : 18.0;
                sb.Append($"""<col min="{c + 1}" max="{c + 1}" width="{width.ToString("0.##", CultureInfo.InvariantCulture)}" customWidth="1"/>""");
            }
            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            sb.Append($"""<row r="{r + 1}">""");

            for (var c = 0; c < sheet.Rows[r].Count; c++)
            {
                var cell = sheet.Rows[r][c];
                var reference = ColumnName(c) + (r + 1).ToString(CultureInfo.InvariantCulture);

                if (cell.Bold)
                {
                    sb.Append($"""<c r="{reference}" s="1" t="inlineStr"><is><t xml:space="preserve">{Escape(cell.Text)}</t></is></c>""");
                }
                else if (cell.Number is { } number)
                {
                    if (double.IsNaN(number) || double.IsInfinity(number)) number = 0;
                    sb.Append($"""<c r="{reference}"><v>{number.ToString("0.####", CultureInfo.InvariantCulture)}</v></c>""");
                }
                else if (!string.IsNullOrEmpty(cell.Text))
                {
                    sb.Append($"""<c r="{reference}" t="inlineStr"><is><t xml:space="preserve">{Escape(cell.Text)}</t></is></c>""");
                }
            }

            sb.Append("</row>");
        }

        sb.Append("</sheetData>");

        if (sheet.AutoFilter && sheet.Rows.Count > 0 && columnCount > 0)
        {
            sb.Append($"""<autoFilter ref="A1:{ColumnName(columnCount - 1)}{sheet.Rows.Count}"/>""");
        }

        sb.Append("</worksheet>");
        return sb.ToString();
    }

    // -- helpers -------------------------------------------------------------------------------

    /// <summary>Excel limits sheet names to 31 characters and forbids a handful of symbols.</summary>
    private static string SafeSheetName(string name, int index)
    {
        var cleaned = new string(name.Select(c => ":\\/?*[]".Contains(c) ? '-' : c).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = $"Sheet{index + 1}";
        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }

    internal static string ColumnName(int zeroBased)
    {
        var name = "";
        var value = zeroBased;
        do
        {
            name = (char)('A' + value % 26) + name;
            value = value / 26 - 1;
        } while (value >= 0);
        return name;
    }

    /// <summary>XML-escapes text and drops control characters, which make Excel refuse the file.</summary>
    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    if (c == '\t' || c == '\n' || c == '\r' || !char.IsControl(c)) sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
