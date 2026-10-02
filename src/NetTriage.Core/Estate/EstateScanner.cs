using NetTriage.Core.Analysis;
using NetTriage.Core.Rules;

namespace NetTriage.Core.Estate;

public sealed class EstateOptions
{
    public required string Root { get; init; }
    public RuleSet Rules { get; init; } = RuleSet.Empty;
    public List<string> Exclude { get; init; } = new();
    /// <summary>How deep to look for application boundaries. Two is enough for most layouts.</summary>
    public int MaxDepth { get; init; } = 3;
}

/// <summary>
/// Scans a directory full of applications as one portfolio. An "application" is the topmost
/// directory that contains a solution, or failing that a project file - the boundary a team would
/// draw around it.
/// </summary>
public static class EstateScanner
{
    private static readonly string[] Skipped =
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages", "TestResults", "artifacts",
    };

    public static EstateReport Scan(EstateOptions options)
    {
        var root = Path.GetFullPath(options.Root);
        var report = new EstateReport
        {
            ToolVersion = ToolInfo.Version,
            Root = root,
            GeneratedAt = DateTimeOffset.UtcNow,
        };

        var appDirectories = DiscoverApps(root, options, report.Warnings);

        if (appDirectories.Count == 0)
        {
            report.Warnings.Add($"No applications found under '{root}'. An application is a directory containing a solution or a project file.");
            return report;
        }

        foreach (var directory in appDirectories)
        {
            try
            {
                var scan = ScanEngine.Scan(new ScanOptions
                {
                    Root = directory,
                    Rules = options.Rules,
                    // The same patterns filter projects inside an application too, so a rule like
                    // "**/Legacy.Tests/**" removes the tests as well as any directory that matches.
                    Exclude = options.Exclude,
                });

                var (id, source) = AppIdentity.Compute(directory, root);

                report.Apps.Add(new EstateApp
                {
                    Id = id,
                    Name = AppName(directory),
                    Path = Path.GetRelativePath(root, directory).Replace('\\', '/'),
                    IdentitySource = source,
                    Report = scan,
                });
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"Failed to scan '{directory}': {ex.Message}");
            }
        }

        report.Apps = report.Apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        report.Summary = Summarize(report);
        return report;
    }

    private static readonly string[] ContainerNames =
    {
        "src", "source", "sources", "code", "apps", "app", "projects", "repos", "repo", "lib", "libs", "main",
    };

    /// <summary>
    /// A repository often keeps its solution in a "src" folder, and calling the application "src"
    /// helps nobody. When the boundary directory is a generic container, name it after its parent.
    /// </summary>
    private static string AppName(string directory)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);

        if (!ContainerNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;

        var parent = Path.GetDirectoryName(trimmed);
        var parentName = parent is null ? null : Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar));

        return string.IsNullOrWhiteSpace(parentName) ? name : parentName;
    }

    private static List<string> DiscoverApps(string root, EstateOptions options, List<string> warnings)
    {
        var found = new List<string>();

        // The root itself may be a single application.
        if (ContainsSolutionOrProject(root))
        {
            return IsExcluded(root, root, options) ? new List<string>() : new List<string> { root };
        }

        Walk(root, root, 0, options, found, warnings);

        return found
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Exclusion is matched against the directory's root-relative path, both bare and with a trailing
    /// slash, so "**/Vendor/**" and "Vendor" both do what the person typing them expects.
    /// </summary>
    private static bool IsExcluded(string directory, string root, EstateOptions options)
    {
        if (options.Exclude.Count == 0) return false;

        var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');

        return GlobMatcher.IsMatchAny(relative, options.Exclude)
               || GlobMatcher.IsMatchAny(relative + "/", options.Exclude);
    }

    private static void Walk(string directory, string root, int depth, EstateOptions options, List<string> found, List<string> warnings)
    {
        if (depth > options.MaxDepth) return;

        List<string> children;
        try
        {
            children = Directory.GetDirectories(directory).ToList();
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not list '{directory}': {ex.Message}");
            return;
        }

        foreach (var child in children.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(child);
            if (Skipped.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (name.StartsWith('.')) continue;
            if (IsExcluded(child, root, options)) continue;

            if (ContainsSolutionOrProject(child))
            {
                // An application boundary: do not descend, so a nested test solution is not counted twice.
                found.Add(child);
                continue;
            }

            Walk(child, root, depth + 1, options, found, warnings);
        }
    }

    private static bool ContainsSolutionOrProject(string directory)
    {
        try
        {
            if (Directory.EnumerateFiles(directory, "*.sln").Any()) return true;
            if (Directory.EnumerateFiles(directory, "*.slnx").Any()) return true;
            if (Directory.EnumerateFiles(directory, "*.csproj").Any()) return true;
            if (Directory.EnumerateFiles(directory, "*.vbproj").Any()) return true;
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static EstateSummary Summarize(EstateReport report)
    {
        var summary = new EstateSummary
        {
            AppCount = report.Apps.Count,
            Projects = report.Apps.Sum(a => a.Report.Summary.ProjectCount),
            SourceLines = report.Apps.Sum(a => a.Report.Summary.TotalSourceLines),
            Blockers = report.Apps.Sum(a => a.Report.Summary.BlockerFindings),
            Warnings = report.Apps.Sum(a => a.Report.Summary.WarningFindings),
            AppsOutOfSupport = report.Apps.Count(a => a.AnyOutOfSupport),
            RedApps = report.Apps.Count(a => a.Report.Summary.Red > 0),
            EffortLowDays = Math.Round(report.Apps.Sum(a => a.Report.Summary.EffortLowDays), 1),
            EffortHighDays = Math.Round(report.Apps.Sum(a => a.Report.Summary.EffortHighDays), 1),
        };

        summary.EolTargetFrameworks = report.Apps
            .SelectMany(a => a.Report.Summary.EolTargetFrameworks)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return summary;
    }
}
