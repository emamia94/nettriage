using System.Xml.Linq;

namespace NetTriage.Core.Analysis;

/// <summary>Reads a .csproj / .vbproj and produces a fully populated project report.</summary>
public static class ProjectReader
{
    private const long MaxScannedFileBytes = 4 * 1024 * 1024;
    private const int MaxSamples = 5;

    private static readonly string[] SourceExtensions = { ".cs", ".vb" };
    private static readonly string[] WebFormsExtensions = { ".aspx", ".ascx", ".master", ".asax" };
    private static readonly string[] AsmxExtensions = { ".asmx" };
    private static readonly string[] WcfExtensions = { ".svc" };

    public static ProjectReport Read(
        string projectPath, string rootPath, List<string> warnings, DetectorSet? detectors = null)
    {
        var set = detectors ?? DetectorSet.BuiltIn;

        var report = new ProjectReport
        {
            Name = Path.GetFileNameWithoutExtension(projectPath),
            Path = Relative(rootPath, projectPath),
        };

        var directory = Path.GetDirectoryName(projectPath) ?? ".";

        XDocument? doc = null;
        try
        {
            doc = XDocument.Load(projectPath);
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not parse '{report.Path}': {ex.Message}");
        }

        var hits = new Dictionary<string, HitAccumulator>(StringComparer.Ordinal);

        if (doc?.Root is { } root)
        {
            report.Style = DetectStyle(root);

            ReadTargetFrameworks(root, report, directory);
            ReadOutputType(root, report);
            ReadProjectTypeGuids(root, report);
            ReadReferences(root, report, hits, warnings);
            ReadPackages(root, report, directory);
            ReadProjectReferences(root, report);
            ReadMarkerItems(root, report, hits, directory, rootPath);
        }

        if (report.Style == ProjectStyle.Legacy)
        {
            Raise(hits, "NT2001", 1, new Occurrence(report.Path, 1, "non-SDK project file"));
        }

        var packagesConfig = Path.Combine(directory, "packages.config");
        if (File.Exists(packagesConfig))
        {
            report.Packages.AddRange(ReadPackagesConfig(packagesConfig, warnings));
            Raise(hits, "NT2002", 1, new Occurrence(Relative(rootPath, packagesConfig), 1, "packages.config"));
        }

        // --- target framework lifecycle -------------------------------------------------
        ApplyLifecycle(report, hits);

        // --- source scanning ------------------------------------------------------------
        var sourceFiles = ResolveSourceFiles(directory, projectPath);
        var bag = new ScanBag();

        foreach (var file in sourceFiles)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxScannedFileBytes)
                {
                    warnings.Add($"Skipped '{Relative(rootPath, file)}' ({info.Length / 1024 / 1024} MB) - larger than the scan limit.");
                    continue;
                }

                var text = File.ReadAllText(file);
                // Scan under the root-relative path so every reported location is consistent.
                var scan = SourceScanner.Scan(Relative(rootPath, file), text, SourceText.DetectLanguage(file));
                bag.Add(scan);

                report.SourceFiles++;
                report.SourceLines += scan.Lines;
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not scan '{Relative(rootPath, file)}': {ex.Message}");
            }
        }

        foreach (var detector in set.All)
        {
            if (detector.Names.Count == 0) continue;

            // A custom rule may be scoped to a subset of projects.
            if (detector.ProjectGlob is { Length: > 0 } projectGlob
                && !GlobMatcher.IsMatch(report.Path, projectGlob))
            {
                continue;
            }

            Func<string, bool>? fileFilter = detector.FileGlob is { Length: > 0 } fileGlob
                ? file => GlobMatcher.IsMatch(file, fileGlob)
                : null;

            var (count, first) = bag.ResolveAnyWhere(detector.Names, fileFilter);
            if (count > 0 && first is not null) Raise(hits, detector.Code, count, first, set);
        }

        // --- marker files discovered on disk (not only those listed in the project) ------
        ScanMarkerFilesOnDisk(directory, rootPath, hits);

        report.Hits = hits.Values
            .Select(h => set.ById(h.Detector.Code) is { } resolved && !ReferenceEquals(resolved, h.Detector)
                ? new DetectorHit(resolved, h.Count, h.Samples)
                : new DetectorHit(h.Detector, h.Count, h.Samples))
            .OrderByDescending(h => h.Detector.Severity)
            .ThenBy(h => h.Detector.Code, StringComparer.Ordinal)
            .ToList();

        report.Kinds = DeriveKinds(report);
        report.Bucket = DeriveBucket(report);
        report.Effort = EffortModel.Estimate(report);

        return report;
    }

    // -- XML helpers ---------------------------------------------------------------------

    private static IEnumerable<XElement> ByName(XContainer container, string localName) =>
        container.Descendants().Where(e => e.Name.LocalName == localName);

    private static string? Value(XContainer container, string localName) =>
        ByName(container, localName).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);

    private static ProjectStyle DetectStyle(XElement? root)
    {
        if (root is null) return ProjectStyle.Unknown;
        if (root.Attribute("Sdk") is not null) return ProjectStyle.Sdk;
        if (ByName(root, "Sdk").Any()) return ProjectStyle.Sdk;
        if (ByName(root, "Import").Any(e =>
                (e.Attribute("Sdk")?.Value ?? "").Length > 0 ||
                (e.Attribute("Project")?.Value ?? "").Contains("Sdk", StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectStyle.Sdk;
        }
        return ProjectStyle.Legacy;
    }

    private static void ReadTargetFrameworks(XElement root, ProjectReport report, string directory)
    {
        var fromProject = ExtractTargetFrameworks(root);
        if (fromProject.Count > 0)
        {
            report.TargetFrameworks = fromProject;
            report.TargetFrameworkSource = "project";
            return;
        }

        // Legacy solutions frequently declare the target framework once, in a shared props file
        // that every project imports. Walk up from the project directory the way MSBuild does.
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                var candidate = Path.Combine(current.FullName, name);
                if (!File.Exists(candidate)) continue;

                var inherited = ExtractTargetFrameworksFromFile(candidate);
                if (inherited.Count == 0) continue;

                report.TargetFrameworks = inherited;
                report.TargetFrameworkSource = name;
                return;
            }

            current = current.Parent;
        }
    }

    /// <summary>Collects every target framework value declared in a document, including conditional ones.</summary>
    private static List<string> ExtractTargetFrameworks(XContainer container)
    {
        var result = new List<string>();

        foreach (var value in ByName(container, "TargetFrameworks").Select(e => e.Value.Trim()))
        {
            if (value.Length == 0) continue;
            result.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        foreach (var value in ByName(container, "TargetFramework").Select(e => e.Value.Trim()))
        {
            if (value.Length > 0) result.Add(value);
        }

        foreach (var value in ByName(container, "TargetFrameworkVersion").Select(e => e.Value.Trim()))
        {
            if (value.Length > 0) result.Add(FrameworkLifecycles.NormalizeLegacyVersion(value));
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ExtractTargetFrameworksFromFile(string path)
    {
        try
        {
            return ExtractTargetFrameworks(XDocument.Load(path));
        }
        catch
        {
            return new List<string>();
        }
    }

    private static void ReadOutputType(XElement root, ProjectReport report) =>
        report.OutputType = Value(root, "OutputType") ?? "";

    private static readonly Dictionary<string, string> TypeGuidKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{349C5851-65DF-11DA-9384-00065B846F21}"] = "Web",
        ["{60DC8134-EBA5-43B8-BCC9-BB4BC16C2548}"] = "WPF",
        ["{3AC096D0-A1C2-E12C-1390-A8335801FDAB}"] = "Test",
        ["{E24C65DC-7377-472B-9ABA-BC803B73C61A}"] = "Web",
    };

    private static void ReadProjectTypeGuids(XElement root, ProjectReport report)
    {
        var guids = Value(root, "ProjectTypeGuids");
        if (string.IsNullOrWhiteSpace(guids)) return;

        foreach (var guid in guids.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TypeGuidKinds.TryGetValue(guid, out var kind) && !report.Kinds.Contains(kind))
            {
                report.Kinds.Add(kind);
            }
        }
    }

    private static void ReadReferences(
        XElement root, ProjectReport report,
        Dictionary<string, HitAccumulator> hits, List<string> warnings)
    {
        foreach (var reference in ByName(root, "Reference"))
        {
            var include = reference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include)) continue;

            var name = include.Split(',')[0].Trim();
            var hintPath = ByName(reference, "HintPath").Select(e => e.Value).FirstOrDefault();

            // Every finding must cite where it was seen. These come from the project file itself,
            // so the project file is the evidence.
            void RaiseReference(string code) => Raise(
                hits, code, 1,
                new Occurrence(report.Path, 1, $"<Reference Include=\"{name}\" />"));

            if (!string.IsNullOrWhiteSpace(hintPath))
            {
                Raise(hits, "NT2005", 1,
                    new Occurrence(report.Path, 1, $"<Reference Include=\"{name}\" /> with HintPath"));
            }

            // Framework assembly references that map onto a known detector.
            switch (name)
            {
                case "System.Web": RaiseReference("NT1016"); break;
                case "System.Web.Services": RaiseReference("NT1008"); break;
                case "System.ServiceModel": RaiseReference("NT1002"); break;
                case "System.Runtime.Remoting": RaiseReference("NT1003"); break;
                case "System.Activities":
                case "System.Workflow.Runtime": RaiseReference("NT1005"); break;
                case "System.Data.Entity": RaiseReference("NT1019"); break;
                case "System.Drawing": RaiseReference("NT1010"); break;
                case "System.EnterpriseServices": RaiseReference("NT1009"); break;
                case "System.Messaging": RaiseReference("NT1023"); break;
                case "System.Management": RaiseReference("NT1013"); break;
            }
        }
    }

    private static void ReadPackages(XElement root, ProjectReport report, string directory)
    {
        foreach (var package in ByName(root, "PackageReference"))
        {
            var id = package.Attribute("Include")?.Value
                     ?? ByName(package, "Include").Select(e => e.Value).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(id)) continue;

            var version = package.Attribute("Version")?.Value
                          ?? ByName(package, "Version").Select(e => e.Value).FirstOrDefault();

            report.Packages.Add(new PackageRef(id, version, "PackageReference"));
        }
    }

    private static void ReadProjectReferences(XElement root, ProjectReport report)
    {
        foreach (var reference in ByName(root, "ProjectReference"))
        {
            var include = reference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include)) continue;
            var name = Path.GetFileNameWithoutExtension(include.Replace('\\', Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(name)) report.ProjectReferences.Add(name);
        }
    }

    private static void ReadMarkerItems(
        XElement root, ProjectReport report, Dictionary<string, HitAccumulator> hits,
        string directory, string rootPath)
    {
        foreach (var item in ByName(root, "Content").Concat(ByName(root, "None")).Concat(ByName(root, "Compile")))
        {
            var include = item.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include)) continue;

            // Project items use Windows separators and are relative to the project directory.
            var full = Path.GetFullPath(
                Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)));
            ClassifyMarker(Relative(rootPath, ResolveCaseInsensitively(full)), report, hits);
        }
    }

    private static void ScanMarkerFilesOnDisk(string directory, string rootPath, Dictionary<string, HitAccumulator> hits)
    {
        var extensions = WebFormsExtensions.Concat(AsmxExtensions).Concat(WcfExtensions).ToArray();
        var stack = new Stack<string>();
        stack.Push(directory);
        int guard = 0;

        while (stack.Count > 0 && guard++ < 20000)
        {
            var dir = stack.Pop();

            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                if (ProjectDiscovery.IsSkippedDirectory(Path.GetFileName(sub))) continue;
                stack.Push(sub);
            }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (extensions.Contains(ext)) ClassifyMarker(Relative(rootPath, file), null, hits);
            }
        }
    }

    /// <summary>
    /// Resolves a path the way Windows would: if the exact path does not exist, each segment is
    /// retried case-insensitively. Project files are routinely written on Windows and scanned on
    /// Linux, where <c>Umbraco\ClientRedirect.aspx</c> and <c>umbraco/ClientRedirect.aspx</c> are
    /// different paths.
    /// </summary>
    private static string ResolveCaseInsensitively(string fullPath)
    {
        if (File.Exists(fullPath) || Directory.Exists(fullPath)) return fullPath;

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return fullPath;

        var current = root;
        var segments = fullPath[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var exact = Path.Combine(current, segment);
            if (Directory.Exists(exact) || File.Exists(exact))
            {
                current = exact;
                continue;
            }

            if (!Directory.Exists(current)) return fullPath;

            var match = Directory.EnumerateFileSystemEntries(current).FirstOrDefault(
                entry => string.Equals(Path.GetFileName(entry), segment, StringComparison.OrdinalIgnoreCase));

            if (match is null) return fullPath;
            current = match;
        }

        return current;
    }

    private static void ClassifyMarker(string path, ProjectReport? report, Dictionary<string, HitAccumulator> hits)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileName(path);

        string? code = ext switch
        {
            ".aspx" or ".ascx" or ".master" or ".asax" => "NT1001",
            ".asmx" => "NT1008",
            ".svc" => "NT1002",
            _ => null,
        };

        if (code is null) return;

        report?.MarkerFiles.Add(name);
        Raise(hits, code, 1, new Occurrence(path, 1, name));
    }

    // -- lifecycle -----------------------------------------------------------------------

    private static void ApplyLifecycle(ProjectReport report, Dictionary<string, HitAccumulator> hits)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var moniker in report.TargetFrameworks)
        {
            var lifecycle = FrameworkLifecycles.Lookup(moniker);
            if (lifecycle is null)
            {
                report.EolNotes.Add($"{moniker}: unknown target framework, no published support data in this build.");
                continue;
            }

            if (lifecycle.EndOfSupport is null)
            {
                report.EolNotes.Add($"{moniker}: {lifecycle.Note}.");
                continue;
            }

            var days = lifecycle.EndOfSupport.Value.DayNumber - today.DayNumber;

            if (days < 0)
            {
                report.IsOutOfSupport = true;
                report.EolNotes.Add($"{moniker}: {lifecycle.Note} - out of support since {lifecycle.EndOfSupport:yyyy-MM-dd} ({-days} days ago).");
                Raise(hits, "NT2003", 1, new Occurrence(report.Path, 1, moniker));
            }
            else if (days <= 180)
            {
                report.EolNotes.Add($"{moniker}: {lifecycle.Note} - end of support {lifecycle.EndOfSupport:yyyy-MM-dd}, in {days} days.");
                Raise(hits, "NT2004", 1, new Occurrence(report.Path, 1, moniker));
            }
            else
            {
                report.EolNotes.Add($"{moniker}: {lifecycle.Note} - supported until {lifecycle.EndOfSupport:yyyy-MM-dd}.");
            }
        }
    }

    // -- source file resolution ----------------------------------------------------------

    private static List<string> ResolveSourceFiles(string directory, string projectPath)
    {
        // Legacy projects list their files explicitly; trust that list when it is present.
        var explicitFiles = ByName(XDocument.Load(projectPath).Root!, "Compile")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Replace('\\', Path.DirectorySeparatorChar))
            .Where(v => SourceExtensions.Contains(Path.GetExtension(v).ToLowerInvariant()))
            .Select(v => Path.GetFullPath(Path.Combine(directory, v)))
            .Where(File.Exists)
            .ToList();

        if (explicitFiles.Count > 0) return explicitFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                if (ProjectDiscovery.IsSkippedDirectory(Path.GetFileName(sub))) continue;
                stack.Push(sub);
            }

            foreach (var file in files)
            {
                if (SourceExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) results.Add(file);
            }
        }

        return results;
    }

    private static List<PackageRef> ReadPackagesConfig(string path, List<string> warnings)
    {
        var result = new List<PackageRef>();
        try
        {
            var doc = XDocument.Load(path);
            foreach (var package in doc.Descendants().Where(e => e.Name.LocalName == "package"))
            {
                var id = package.Attribute("id")?.Value;
                var version = package.Attribute("version")?.Value;
                if (!string.IsNullOrWhiteSpace(id)) result.Add(new PackageRef(id, version, "packages.config"));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not read '{Path.GetFileName(path)}': {ex.Message}");
        }
        return result;
    }

    // -- classification ------------------------------------------------------------------

    private static List<string> DeriveKinds(ProjectReport report)
    {
        var kinds = new HashSet<string>(report.Kinds, StringComparer.OrdinalIgnoreCase);
        var codes = report.Hits.Select(h => h.Detector.Code).ToHashSet(StringComparer.Ordinal);

        if (codes.Contains("NT1001")) kinds.Add("WebForms");
        if (codes.Contains("NT1002")) kinds.Add("WCF service");
        if (codes.Contains("NT1008")) kinds.Add("ASMX");
        if (codes.Contains("NT1015")) kinds.Add("ASP.NET MVC/Web API");
        if (codes.Contains("NT1024")) kinds.Add("WPF");
        if (codes.Contains("NT1025")) kinds.Add("WinForms");
        if (codes.Contains("NT1009")) kinds.Add("COM+");

        var isTest = report.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)
                     || report.Packages.Any(p =>
                         p.Id.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) ||
                         p.Id.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase) ||
                         p.Id.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) ||
                         p.Id.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));

        if (isTest) kinds.Add("Test");
        else if (report.OutputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
                 report.OutputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
        {
            if (!kinds.Contains("WPF") && !kinds.Contains("WinForms")) kinds.Add("Executable");
        }
        else if (kinds.Count == 0)
        {
            kinds.Add("Library");
        }

        return kinds.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The bucket a project belongs in, given the findings it still has. Internal rather than private
    /// so the scan engine can re-derive it after an allowlist removes a finding.
    /// </summary>
    internal static Bucket DeriveBucket(ProjectReport report)
    {
        if (report.Hits.Any(h => h.Detector.Severity == Severity.Blocker)) return Bucket.Red;
        if (report.Hits.Any(h => h.Detector.Severity == Severity.Warning)) return Bucket.Yellow;
        return Bucket.Green;
    }

    /// <summary>
    /// Records a finding. The occurrence is required: a finding with no location cannot be acted
    /// on, so there is deliberately no way to raise one.
    /// </summary>
    private static void Raise(
        Dictionary<string, HitAccumulator> hits, string code, int count, Occurrence occurrence,
        DetectorSet? detectors = null)
    {
        var detector = (detectors ?? DetectorSet.BuiltIn).ById(code);
        if (detector is null) return;

        if (!hits.TryGetValue(code, out var accumulator))
        {
            accumulator = new HitAccumulator(detector);
            hits[code] = accumulator;
        }

        accumulator.Count += count;

        if (accumulator.Samples.Count < MaxSamples)
        {
            accumulator.Samples.Add(occurrence);
        }
    }

    private static string Relative(string root, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(root, path);
            return rel.StartsWith("..") ? path : rel;
        }
        catch
        {
            return path;
        }
    }

    private sealed class HitAccumulator
    {
        public HitAccumulator(Detector detector) => Detector = detector;
        public Detector Detector { get; }
        public int Count { get; set; }
        public List<Occurrence> Samples { get; } = new();
    }
}
