using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NetTriage.Core.Analysis;

/// <summary>Finds the projects to analyse, from a solution, a project, or a directory tree.</summary>
public static class ProjectDiscovery
{
    private static readonly string[] SkippedDirectories =
    {
        "bin", "obj", ".git", ".vs", ".vscode", "node_modules", "packages",
        "TestResults", ".idea", "artifacts", "publish",
    };

    private static readonly Regex SlnProjectLine = new(
        @"Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Discover(string root, List<string> warnings)
    {
        var found = new List<string>();

        if (File.Exists(root))
        {
            var ext = Path.GetExtension(root).ToLowerInvariant();
            if (ext is ".csproj" or ".vbproj")
            {
                found.Add(Path.GetFullPath(root));
            }
            else if (ext is ".sln" or ".slnx")
            {
                found.AddRange(ReadSolution(root, warnings));
            }
            else
            {
                warnings.Add($"Unsupported input '{root}'. Expected a .sln, .slnx, .csproj, .vbproj or a directory.");
            }
            return Normalize(found);
        }

        if (!Directory.Exists(root))
        {
            warnings.Add($"Path not found: {root}");
            return Array.Empty<string>();
        }

        // Prefer a solution file at the top level when there is exactly one.
        var solutions = Directory.EnumerateFiles(root, "*.sln*")
            .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (solutions.Count == 1)
        {
            var fromSolution = ReadSolution(solutions[0], warnings);
            if (fromSolution.Count > 0) return Normalize(fromSolution);
        }

        foreach (var file in EnumerateProjectFiles(root))
        {
            found.Add(file);
        }

        if (found.Count == 0)
        {
            warnings.Add($"No .csproj or .vbproj files found under {root}.");
        }

        return Normalize(found);
    }

    private static List<string> ReadSolution(string solutionPath, List<string> warnings)
    {
        var result = new List<string>();
        var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? ".";

        try
        {
            var text = File.ReadAllText(solutionPath);

            if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                var doc = XDocument.Parse(text);
                foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "Project"))
                {
                    var path = element.Attribute("Path")?.Value;
                    if (path is null) continue;
                    AddIfProject(Path.Combine(directory, path), result);
                }
            }
            else
            {
                foreach (Match m in SlnProjectLine.Matches(text))
                {
                    AddIfProject(Path.Combine(directory, m.Groups[1].Value), result);
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not read solution '{solutionPath}': {ex.Message}");
        }

        if (result.Count == 0)
        {
            warnings.Add($"Solution '{Path.GetFileName(solutionPath)}' listed no .csproj or .vbproj files; falling back to a directory scan.");
            foreach (var file in EnumerateProjectFiles(directory)) result.Add(file);
        }

        return result;
    }

    private static void AddIfProject(string candidate, List<string> sink)
    {
        // .sln files store Windows-style separators regardless of where they are opened.
        var normalized = candidate.Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(normalized);
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext is ".csproj" or ".vbproj" && File.Exists(full)) sink.Add(full);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push(sub);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.?sproj");
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".csproj" or ".vbproj") yield return Path.GetFullPath(file);
            }
        }
    }

    private static IReadOnlyList<string> Normalize(List<string> paths) =>
        paths.Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
             .ToList();

    public static bool IsSkippedDirectory(string name) =>
        SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase);
}
