using System.Text.RegularExpressions;

namespace NetTriage.Core.Analysis;

/// <summary>Names referenced by a single source file, after comments and literals were removed.</summary>
public sealed class FileScan
{
    public string Path { get; set; } = "";
    public int Lines { get; set; }
    public HashSet<string> Imports { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Names { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Occurrence> First { get; } = new(StringComparer.Ordinal);

    public void Add(string name, int line)
    {
        if (name.Length < 2 || name.Length > 200) return;
        Names[name] = Names.TryGetValue(name, out var c) ? c + 1 : 1;
        if (!First.ContainsKey(name))
        {
            First[name] = new Occurrence(Path, line, name.Length <= 120 ? name : name[..117] + "...");
        }
    }
}

/// <summary>
/// The set of names referenced across a project, with the per-file import context needed to resolve
/// a simple type name (Page) that was pulled in by an import (using System.Web.UI;).
/// </summary>
public sealed class ScanBag
{
    private readonly List<FileScan> _files = new();

    public IReadOnlyList<FileScan> Files => _files;

    public void Add(FileScan file) => _files.Add(file);

    /// <summary>
    /// Counts every reference to a qualified name. An exact match on the dotted name wins; only if
    /// there is none does it fall back to "the namespace is imported and the simple name appears".
    /// </summary>
    public (int Count, Occurrence? First) Resolve(string qualified) => ResolveInFiles(qualified, null);

    /// <summary>
    /// As <see cref="Resolve"/>, but only counts files accepted by <paramref name="fileFilter"/>.
    /// Custom rules use this to scope themselves to a file glob.
    /// </summary>
    public (int Count, Occurrence? First) ResolveInFiles(string qualified, Func<string, bool>? fileFilter)
    {
        int total = 0;
        Occurrence? first = null;

        string? ns = null;
        string simple = qualified;
        int dot = qualified.LastIndexOf('.');
        if (dot > 0)
        {
            ns = qualified[..dot];
            simple = qualified[(dot + 1)..];
        }

        foreach (var file in _files)
        {
            if (fileFilter is not null && !fileFilter(file.Path)) continue;

            int count = 0;
            Occurrence? occ = null;

            if (file.Names.TryGetValue(qualified, out var exact))
            {
                count = exact;
                file.First.TryGetValue(qualified, out occ);
            }
            else if (ns is not null
                     && file.Imports.Contains(ns)
                     && file.Names.TryGetValue(simple, out var viaImport))
            {
                count = viaImport;
                file.First.TryGetValue(simple, out occ);
            }

            if (count == 0) continue;
            total += count;
            first ??= occ;
        }

        return (total, first);
    }

    public (int Count, Occurrence? First) ResolveAny(IEnumerable<string> names) =>
        ResolveAnyWhere(names, null);

    public (int Count, Occurrence? First) ResolveAnyWhere(IEnumerable<string> names, Func<string, bool>? fileFilter)
    {
        int total = 0;
        Occurrence? first = null;
        foreach (var name in names)
        {
            var (count, occ) = ResolveInFiles(name, fileFilter);
            total += count;
            first ??= occ;
        }
        return (total, first);
    }
}

public static class SourceScanner
{
    private const int MaxSamplesPerDetector = 5;

    private static readonly Regex DottedName = new(
        @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SimpleTypeName = new(
        @"\b[A-Z][A-Za-z0-9_]*\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CsUsing = new(
        @"\busing\s+(?:static\s+)?([A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VbImports = new(
        @"(?m)^[ \t]*Imports[ \t]+([A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Scans one source file. Never throws on unparsable input - it just finds less.</summary>
    public static FileScan Scan(string path, string text, Language language)
    {
        var stripped = SourceText.Strip(text, language);
        var lines = new LineIndex(stripped);

        var file = new FileScan { Path = path, Lines = lines.Count };

        foreach (Match m in DottedName.Matches(stripped))
        {
            AddDotted(file, m.Value, lines.LineOf(m.Index));
        }

        foreach (Match m in SimpleTypeName.Matches(stripped))
        {
            file.Add(m.Value, lines.LineOf(m.Index));
        }

        var importRegex = language == Language.VisualBasic ? VbImports : CsUsing;
        foreach (Match m in importRegex.Matches(stripped))
        {
            file.Imports.Add(Regex.Replace(m.Groups[1].Value, @"\s+", ""));
        }

        return file;
    }

    /// <summary>
    /// Registers a dotted name plus each of its prefixes, so System.Web.HttpContext.Current also
    /// registers System.Web.HttpContext. Only chains whose first segment is capitalised are treated
    /// as type or namespace references, which keeps instance member access (customer.Name) out.
    /// </summary>
    private static void AddDotted(FileScan file, string dotted, int line)
    {
        if (dotted.Length == 0 || !char.IsUpper(dotted[0])) return;

        file.Add(dotted, line);

        int idx = dotted.IndexOf('.');
        while (idx > 0)
        {
            var prefix = dotted[..idx];
            if (prefix.Length >= 2) file.Add(prefix, line);
            idx = dotted.IndexOf('.', idx + 1);
        }
    }

    public static int MaxSamples => MaxSamplesPerDetector;
}
