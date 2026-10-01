using System.Text;

namespace NetTriage.Core.Analysis;

public enum Language
{
    CSharp,
    VisualBasic,
}

/// <summary>
/// Lexical source preparation. Comments and string/char literal contents are blanked out in place,
/// so a type name that only appears in a comment or a log message never produces a finding.
///
/// Blanking preserves every byte offset and every newline, which means a regex match position maps
/// straight back to the original line number.
///
/// This is deliberately lexical rather than semantic. For presence-of-API detection it is exact
/// enough, fully deterministic, and keeps the tool dependency-free. Semantic (Roslyn) analysis is
/// on the roadmap, not in this version.
/// </summary>
public static class SourceText
{
    public static Language DetectLanguage(string path) =>
        path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
            ? Language.VisualBasic
            : Language.CSharp;

    public static string Strip(string text, Language language)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        int n = text.Length;

        while (i < n)
        {
            char c = text[i];
            char d = i + 1 < n ? text[i + 1] : '\0';

            int start = i;
            int end;

            if (language == Language.CSharp && c == '/' && d == '/')
            {
                i = SkipToLineEnd(text, i);
                end = i;
            }
            else if (language == Language.VisualBasic && c == '\'')
            {
                i = SkipToLineEnd(text, i);
                end = i;
            }
            else if (language == Language.CSharp && c == '/' && d == '*')
            {
                i = SkipBlockComment(text, i);
                end = i;
            }
            else if (language == Language.CSharp && c == '"' && d == '"' && i + 2 < n && text[i + 2] == '"')
            {
                i = SkipRawString(text, i);
                end = i;
            }
            else if (language == Language.CSharp && (c == '@' || c == '$'))
            {
                int p = i;
                bool verbatim = false;
                while (p < n && (text[p] == '@' || text[p] == '$'))
                {
                    if (text[p] == '@') verbatim = true;
                    p++;
                }
                if (p < n && text[p] == '"')
                {
                    i = verbatim ? SkipVerbatimString(text, p) : SkipRegularString(text, p);
                    end = i;
                }
                else
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
            }
            else if (c == '"')
            {
                i = language == Language.VisualBasic ? SkipVbString(text, i) : SkipRegularString(text, i);
                end = i;
            }
            else if (language == Language.CSharp && c == '\'')
            {
                i = SkipCharLiteral(text, i);
                end = i;
            }
            else
            {
                sb.Append(c);
                i++;
                continue;
            }

            // Blank the removed span in place, keeping newlines so line numbers stay exact.
            for (int k = start; k < end; k++)
            {
                sb.Append(text[k] == '\n' ? '\n' : ' ');
            }
        }

        return sb.ToString();
    }

    private static int SkipToLineEnd(string t, int i)
    {
        while (i < t.Length && t[i] != '\n') i++;
        return i;
    }

    private static int SkipBlockComment(string t, int i)
    {
        int n = t.Length;
        i += 2;
        while (i < n && !(t[i] == '*' && i + 1 < n && t[i + 1] == '/')) i++;
        return Math.Min(i + 2, n);
    }

    private static int SkipRawString(string t, int i)
    {
        int n = t.Length;
        int open = 0;
        while (i + open < n && t[i + open] == '"') open++;
        i += open;
        while (i < n)
        {
            if (t[i] == '"')
            {
                int run = 0;
                while (i + run < n && t[i + run] == '"') run++;
                if (run >= open) return i + open;
                i += run;
            }
            else
            {
                i++;
            }
        }
        return n;
    }

    private static int SkipRegularString(string t, int i)
    {
        int n = t.Length;
        i++;
        while (i < n)
        {
            if (t[i] == '\\') { i += 2; continue; }
            if (t[i] == '"') return i + 1;
            i++;
        }
        return n;
    }

    private static int SkipVbString(string t, int i)
    {
        int n = t.Length;
        i++;
        while (i < n)
        {
            if (t[i] == '"')
            {
                if (i + 1 < n && t[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return n;
    }

    private static int SkipVerbatimString(string t, int i)
    {
        int n = t.Length;
        i++;
        while (i < n)
        {
            if (t[i] == '"')
            {
                if (i + 1 < n && t[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return n;
    }

    private static int SkipCharLiteral(string t, int i)
    {
        int n = t.Length;
        i++;
        while (i < n && t[i] != '\'')
        {
            if (t[i] == '\\') i++;
            i++;
        }
        return Math.Min(i + 1, n);
    }
}

/// <summary>Maps a byte offset back to a 1-based line number.</summary>
public sealed class LineIndex
{
    private readonly int[] _starts;

    public LineIndex(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }
        _starts = starts.ToArray();
    }

    public int Count => _starts.Length;

    public int LineOf(int offset)
    {
        int lo = 0, hi = _starts.Length - 1, best = 0;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_starts[mid] <= offset) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best + 1;
    }
}
