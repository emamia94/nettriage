using System.Text;
using System.Text.RegularExpressions;

namespace NetTriage.Core.Analysis;

/// <summary>
/// A small glob matcher for the patterns users write in rule files and on the command line:
/// <c>*</c> matches within a path segment, <c>**</c> crosses segments, <c>?</c> matches one
/// character. Paths are compared with forward slashes regardless of platform.
/// </summary>
public static class GlobMatcher
{
    private static readonly Dictionary<string, Regex> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static bool IsMatch(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        var normalizedPath = Normalize(path);
        var normalizedPattern = Normalize(pattern);

        // A bare substring with no wildcard is treated as "contains", which is what people expect
        // when they type --exclude Samples or --exclude "**/Legacy/**".
        if (!normalizedPattern.Contains('*') && !normalizedPattern.Contains('?'))
        {
            return normalizedPath.Contains(normalizedPattern, StringComparison.OrdinalIgnoreCase);
        }

        return RegexFor(normalizedPattern).IsMatch(normalizedPath);
    }

    public static bool IsMatchAny(string path, IEnumerable<string> patterns) =>
        patterns.Any(p => IsMatch(path, p));

    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('/');

    private static Regex RegexFor(string pattern)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(pattern, out var cached)) return cached;

            var builder = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                switch (c)
                {
                    case '*':
                        if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                        {
                            i++;
                            if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                            {
                                // "**/" means zero or more whole segments, so it also matches nothing.
                                i++;
                                builder.Append("(?:.*/)?");
                            }
                            else
                            {
                                // A trailing "**" must match everything, including a single segment.
                                builder.Append(".*");
                            }
                        }
                        else
                        {
                            builder.Append("[^/]*");
                        }
                        break;

                    case '?':
                        builder.Append("[^/]");
                        break;

                    default:
                        builder.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }

            builder.Append('$');
            var regex = new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Cache[pattern] = regex;
            return regex;
        }
    }
}
