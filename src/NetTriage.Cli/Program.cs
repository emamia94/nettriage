using NetTriage.Core;
using NetTriage.Core.Analysis;
using NetTriage.Core.Baseline;
using NetTriage.Core.Estate;
using NetTriage.Core.Licensing;
using NetTriage.Core.Reporting;
using NetTriage.Core.Rules;

namespace NetTriage.Cli;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitThresholdMet = 2;
    private const int ExitLicenceRequired = 3;

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Some hosts refuse an encoding change; not fatal.
        }

        if (args.Length == 0)
        {
            Console.WriteLine(Usage());
            return ExitOk;
        }

        var command = args[0].ToLowerInvariant();

        switch (command)
        {
            case "help" or "-h" or "--help":
                Console.WriteLine(Usage());
                return ExitOk;

            case "version" or "--version" or "-v":
                Console.WriteLine($"{ToolInfo.Name} {ToolInfo.Version}");
                return ExitOk;

            case "detectors":
                PrintDetectors();
                return ExitOk;

            case "scan":
                return RunScan(args.Skip(1).ToArray());

            case "baseline":
                return RunBaseline(args.Skip(1).ToArray());

            case "estate":
                return RunEstate(args.Skip(1).ToArray());

            case "rules":
                return RunRules(args.Skip(1).ToArray());

            case "license" or "licence":
                return RunLicense(args.Skip(1).ToArray());

            default:
                // Shorthand: `nettriage ./src` behaves like `nettriage scan ./src`.
                if (Directory.Exists(args[0]) || File.Exists(args[0]))
                {
                    return RunScan(args);
                }

                Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                Console.Error.WriteLine();
                Console.Error.WriteLine(Usage());
                return ExitError;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // scan
    // ---------------------------------------------------------------------------------------------

    private static int RunScan(string[] args)
    {
        string? root = null;
        var format = "console";
        string? output = null;
        var excludes = new List<string>();
        var failOn = "none";
        var failOnNew = "none";
        string? rulesPath = null;
        string? baselinePath = null;
        bool noColor = false, quiet = false;
        int maxFindings = 6;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            string? Next(string name)
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine($"Option {name} needs a value.");
                    return null;
                }
                return args[++i];
            }

            switch (arg)
            {
                case "--format" or "-f":
                    var f = Next(arg);
                    if (f is null) return ExitError;
                    format = f.ToLowerInvariant();
                    break;

                case "--output" or "-o":
                    output = Next(arg);
                    if (output is null) return ExitError;
                    break;

                case "--exclude" or "-x":
                    var ex = Next(arg);
                    if (ex is null) return ExitError;
                    excludes.Add(ex);
                    break;

                case "--fail-on":
                    var fo = Next(arg);
                    if (fo is null) return ExitError;
                    failOn = fo.ToLowerInvariant();
                    break;

                case "--fail-on-new":
                    var fon = Next(arg);
                    if (fon is null) return ExitError;
                    failOnNew = fon.ToLowerInvariant();
                    break;

                case "--rules" or "-r":
                    rulesPath = Next(arg);
                    if (rulesPath is null) return ExitError;
                    break;

                case "--baseline" or "-b":
                    baselinePath = Next(arg);
                    if (baselinePath is null) return ExitError;
                    break;

                case "--max-findings":
                    var mf = Next(arg);
                    if (mf is null) return ExitError;
                    if (!int.TryParse(mf, out maxFindings) || maxFindings < 0)
                    {
                        Console.Error.WriteLine("--max-findings needs a non-negative integer.");
                        return ExitError;
                    }
                    break;

                case "--no-color":
                    noColor = true;
                    break;

                case "--quiet" or "-q":
                    quiet = true;
                    break;

                case "-h" or "--help":
                    Console.WriteLine(Usage());
                    return ExitOk;

                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option '{arg}'.");
                        return ExitError;
                    }
                    if (root is not null)
                    {
                        Console.Error.WriteLine("Only one path can be scanned at a time.");
                        return ExitError;
                    }
                    root = arg;
                    break;
            }
        }

        if (root is null)
        {
            Console.Error.WriteLine("No path given.");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage());
            return ExitError;
        }

        if (!File.Exists(root) && !Directory.Exists(root))
        {
            Console.Error.WriteLine($"Path not found: {root}");
            return ExitError;
        }

        var entitlements = LoadEntitlements();

        // -- licensed features ---------------------------------------------------------------
        if (rulesPath is not null && !RequireFeature(entitlements, Features.Rules)) return ExitLicenceRequired;
        if ((baselinePath is not null || failOnNew != "none")
            && !RequireFeature(entitlements, Features.Baseline)) return ExitLicenceRequired;
        if (format is "xlsx" or "summary" && !RequireFeature(entitlements, Features.Exports)) return ExitLicenceRequired;

        if (!SupportedFormats.Contains(format))
        {
            Console.Error.WriteLine($"Unsupported format '{format}'. Use one of: {string.Join(", ", SupportedFormats)}.");
            return ExitError;
        }

        if (failOn is not ("none" or "red" or "yellow"))
        {
            Console.Error.WriteLine("--fail-on accepts: none, red, yellow.");
            return ExitError;
        }

        if (failOnNew is not ("none" or "red" or "yellow" or "any"))
        {
            Console.Error.WriteLine("--fail-on-new accepts: none, red, yellow, any.");
            return ExitError;
        }

        if (format == "xlsx" && output is null)
        {
            Console.Error.WriteLine("--format xlsx writes a binary workbook; give it a path with --output.");
            return ExitError;
        }

        RuleSet rules = RuleSet.Empty;
        if (rulesPath is not null)
        {
            var loaded = RuleLoader.Load(rulesPath, out var ruleErrors);
            if (loaded is null)
            {
                Console.Error.WriteLine($"Could not load the rules file '{rulesPath}':");
                foreach (var error in ruleErrors) Console.Error.WriteLine($"  - {error}");
                return ExitError;
            }
            rules = loaded;
        }

        Baseline? baseline = null;
        if (baselinePath is not null)
        {
            if (!BaselineStore.TryLoad(baselinePath, out var loadedBaseline, out var baselineError))
            {
                Console.Error.WriteLine(baselineError);
                return ExitError;
            }
            baseline = loadedBaseline;
        }

        var options = new ScanOptions { Root = root, Exclude = excludes, Rules = rules, Baseline = baseline };
        var report = ScanEngine.Scan(options);

        if (format == "xlsx")
        {
            XlsxWriter.Write(output!, WorkbookBuilder.ForScan(report));
            if (!quiet)
            {
                Console.WriteLine($"{ToolInfo.Name}: workbook written to {Path.GetFullPath(output!)}");
                PrintOneLineSummary(report);
            }
        }
        else if (format == "summary")
        {
            WriteOrPrint(ExecutiveSummary.ForScan(report), output, quiet);
        }
        else
        {
            var useColor = !noColor
                           && format == "console"
                           && output is null
                           && !Console.IsOutputRedirected
                           && Environment.GetEnvironmentVariable("NO_COLOR") is null or "";

            var reporter = format == "console"
                ? new ConsoleReporter(useColor, maxFindings)
                : ReporterFactory.Create(format, useColor);

            WriteOrPrint(reporter.Render(report), output, quiet, report);
        }

        foreach (var warning in report.Warnings)
        {
            if (!quiet) Console.Error.WriteLine($"{ToolInfo.Name}: {warning}");
        }

        if (report.Summary.ProjectCount == 0)
        {
            if (!quiet) Console.Error.WriteLine($"{ToolInfo.Name}: no projects found - nothing to report.");
            return ExitError;
        }

        if (failOn switch
            {
                "red" when report.Summary.Red > 0 => true,
                "yellow" when report.Summary.Red > 0 || report.Summary.Yellow > 0 => true,
                _ => false,
            })
        {
            return ExitThresholdMet;
        }

        if (report.BaselineDiff is { } diff && failOnNew switch
            {
                "any" when diff.AnyNew => true,
                "red" when diff.NewBlockers > 0 => true,
                "yellow" when diff.NewBlockers > 0 || diff.NewWarnings > 0 => true,
                _ => false,
            })
        {
            return ExitThresholdMet;
        }

        return ExitOk;
    }

    // ---------------------------------------------------------------------------------------------
    // baseline
    // ---------------------------------------------------------------------------------------------

    private static int RunBaseline(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(BaselineUsage());
            return args.Length == 0 ? ExitError : ExitOk;
        }

        var action = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        return action switch
        {
            "save" => BaselineSave(rest),
            "show" => BaselineShow(rest),
            _ => Unknown($"baseline {action}"),
        };
    }

    private static int BaselineSave(string[] args)
    {
        string? root = null, output = null, rulesPath = null;
        var excludes = new List<string>();
        var quiet = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output" or "-o":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("-o needs a value."); return ExitError; }
                    output = args[++i];
                    break;

                case "--rules" or "-r":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("-r needs a value."); return ExitError; }
                    rulesPath = args[++i];
                    break;

                case "--exclude" or "-x":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("-x needs a value."); return ExitError; }
                    excludes.Add(args[++i]);
                    break;

                case "--quiet" or "-q":
                    quiet = true;
                    break;

                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option '{args[i]}'.");
                        return ExitError;
                    }
                    if (root is not null)
                    {
                        Console.Error.WriteLine("Only one path at a time.");
                        return ExitError;
                    }
                    root = args[i];
                    break;
            }
        }

        if (root is null)
        {
            Console.Error.WriteLine("No path given.");
            Console.Error.WriteLine(BaselineUsage());
            return ExitError;
        }

        if (!File.Exists(root) && !Directory.Exists(root))
        {
            Console.Error.WriteLine($"Path not found: {root}");
            return ExitError;
        }

        var entitlements = LoadEntitlements();
        if (!RequireFeature(entitlements, Features.Baseline)) return ExitLicenceRequired;

        var rules = LoadRulesOrFail(rulesPath, out var rulesExit);
        if (rules is null) return rulesExit;

        var report = ScanEngine.Scan(new ScanOptions { Root = root, Exclude = excludes, Rules = rules });
        if (report.Summary.ProjectCount == 0)
        {
            Console.Error.WriteLine("No projects found - nothing to record.");
            return ExitError;
        }

        var baseline = BaselineComparer.Capture(report, ToolInfo.Version);
        var target = output ?? "nettriage-baseline.json";
        BaselineStore.Save(baseline, target);

        if (!quiet)
        {
            Console.WriteLine($"{ToolInfo.Name}: baseline written to {Path.GetFullPath(target)}");
            Console.WriteLine($"  {baseline.Projects.Count} project(s), {baseline.TotalFindings} finding(s) recorded.");
            Console.WriteLine();
            Console.WriteLine("  Commit it, then gate on drift only:");
            Console.WriteLine($"    {ToolInfo.Name} scan {root} --baseline {target} --fail-on-new red");
        }

        return ExitOk;
    }

    private static int BaselineShow(string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (path is null)
        {
            Console.Error.WriteLine("baseline show needs a baseline file.");
            return ExitError;
        }

        if (!BaselineStore.TryLoad(path, out var baseline, out var error))
        {
            Console.Error.WriteLine(error);
            return ExitError;
        }

        Console.WriteLine();
        Console.WriteLine($"{ToolInfo.Name} baseline");
        Console.WriteLine();
        Console.WriteLine($"  File        {Path.GetFullPath(path)}");
        Console.WriteLine($"  Format      {baseline.Format}");
        Console.WriteLine($"  Recorded    {baseline.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"  Tool        {baseline.ToolVersion}");
        Console.WriteLine($"  Root        {baseline.Root}");
        Console.WriteLine($"  Projects    {baseline.Projects.Count}");
        Console.WriteLine($"  Findings    {baseline.TotalFindings}");
        Console.WriteLine();

        foreach (var project in baseline.Projects.OrderByDescending(p => p.Findings.Values.Sum()))
        {
            var total = project.Findings.Values.Sum();
            if (total == 0) continue;

            var top = project.Findings
                .OrderByDescending(kv => kv.Value)
                .Take(4)
                .Select(kv => $"{kv.Key} x{kv.Value}");

            Console.WriteLine($"  {project.Name,-30} {total,5}  {string.Join(", ", top)}");
        }

        Console.WriteLine();
        return ExitOk;
    }

    // ---------------------------------------------------------------------------------------------
    // estate
    // ---------------------------------------------------------------------------------------------

    private static int RunEstate(string[] args)
    {
        string? root = null, output = null, historyPath = null, rulesPath = null;
        var format = "console";
        var excludes = new List<string>();
        bool noColor = false, quiet = false;
        var maxApps = 20;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--format" or "-f":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("-f needs a value."); return ExitError; }
                    format = args[++i].ToLowerInvariant();
                    break;

                case "--output" or "-o":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("-o needs a value."); return ExitError; }
                    output = args[++i];
                    break;

                case "--history" or "-H":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("-H needs a value."); return ExitError; }
                    historyPath = args[++i];
                    break;

                case "--rules" or "-r":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("-r needs a value."); return ExitError; }
                    rulesPath = args[++i];
                    break;

                case "--exclude" or "-x":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("-x needs a value."); return ExitError; }
                    excludes.Add(args[++i]);
                    break;

                case "--max-apps":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out maxApps) || maxApps < 1)
                    {
                        Console.Error.WriteLine("--max-apps needs a positive integer.");
                        return ExitError;
                    }
                    break;

                case "--no-color":
                    noColor = true;
                    break;

                case "--quiet" or "-q":
                    quiet = true;
                    break;

                case "-h" or "--help":
                    Console.WriteLine(EstateUsage());
                    return ExitOk;

                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option '{args[i]}'.");
                        return ExitError;
                    }
                    if (root is not null)
                    {
                        Console.Error.WriteLine("Only one estate root at a time.");
                        return ExitError;
                    }
                    root = args[i];
                    break;
            }
        }

        if (root is null)
        {
            Console.Error.WriteLine("No estate root given.");
            Console.Error.WriteLine(EstateUsage());
            return ExitError;
        }

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Directory not found: {root}");
            return ExitError;
        }

        var entitlements = LoadEntitlements();
        if (!RequireFeature(entitlements, Features.Estate)) return ExitLicenceRequired;
        if (rulesPath is not null && !RequireFeature(entitlements, Features.Rules)) return ExitLicenceRequired;
        if (format is "xlsx" or "summary" && !RequireFeature(entitlements, Features.Exports)) return ExitLicenceRequired;

        if (format is not ("console" or "json" or "markdown" or "md" or "xlsx" or "summary"))
        {
            Console.Error.WriteLine("Estate --format accepts: console, json, markdown, xlsx, summary.");
            return ExitError;
        }

        if (format == "xlsx" && output is null)
        {
            Console.Error.WriteLine("--format xlsx writes a binary workbook; give it a path with --output.");
            return ExitError;
        }

        var rules = LoadRulesOrFail(rulesPath, out var rulesExit);
        if (rules is null) return rulesExit;

        var estate = EstateScanner.Scan(new EstateOptions { Root = root, Rules = rules, Exclude = excludes });

        if (estate.Apps.Count == 0)
        {
            foreach (var warning in estate.Warnings) Console.Error.WriteLine($"{ToolInfo.Name}: {warning}");
            Console.Error.WriteLine($"{ToolInfo.Name}: no applications found under {root}.");
            return ExitError;
        }

        // The snapshot is appended before rendering, so the trend shown is the one just recorded.
        if (historyPath is not null)
        {
            EstateSnapshotStore.Append(EstateSnapshotStore.Capture(estate), historyPath);
            estate.Trend = EstateSnapshotStore.BuildTrend(historyPath);
        }

        switch (format)
        {
            case "xlsx":
                XlsxWriter.Write(output!, WorkbookBuilder.ForEstate(estate));
                if (!quiet)
                {
                    Console.WriteLine($"{ToolInfo.Name}: workbook written to {Path.GetFullPath(output!)}");
                    PrintEstateOneLine(estate);
                }
                break;

            case "json":
                WriteOrPrint(new EstateJsonReporter().Render(estate), output, quiet, estate);
                break;

            case "markdown" or "md":
                WriteOrPrint(new EstateMarkdownReporter().Render(estate), output, quiet, estate);
                break;

            case "summary":
                WriteOrPrint(ExecutiveSummary.ForEstate(estate), output, quiet, estate);
                break;

            default:
                var useColor = !noColor && output is null && !Console.IsOutputRedirected
                               && Environment.GetEnvironmentVariable("NO_COLOR") is null or "";
                WriteOrPrint(new EstateConsoleReporter(useColor, maxApps).Render(estate), output, quiet, estate);
                break;
        }

        foreach (var warning in estate.Warnings)
        {
            if (!quiet) Console.Error.WriteLine($"{ToolInfo.Name}: {warning}");
        }

        return ExitOk;
    }

    // ---------------------------------------------------------------------------------------------
    // rules
    // ---------------------------------------------------------------------------------------------

    private static int RunRules(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(RulesUsage());
            return args.Length == 0 ? ExitError : ExitOk;
        }

        var action = args[0].ToLowerInvariant();

        if (action == "template")
        {
            var output = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;
            WriteOrPrint(RuleLoader.Template(), output, false);
            return ExitOk;
        }

        if (action != "validate")
        {
            return Unknown($"rules {action}");
        }

        var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (path is null)
        {
            Console.Error.WriteLine("rules validate needs a rules file.");
            return ExitError;
        }

        var entitlements = LoadEntitlements();
        if (!RequireFeature(entitlements, Features.Rules)) return ExitLicenceRequired;

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Rules file not found: {path}");
            return ExitError;
        }

        var rules = RuleLoader.Load(path, out var errors);
        var target = rules ?? new RuleSet();

        // Reload without the fatal gate so warnings can still be shown for an invalid file.
        if (rules is null)
        {
            try
            {
                var raw = System.Text.Json.JsonSerializer.Deserialize<RuleSet>(
                    File.ReadAllText(path),
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });
                if (raw is not null) target = raw;
            }
            catch
            {
                // Already reported by the loader.
            }
        }

        var validation = RuleLoader.Validate(target);

        Console.WriteLine();
        Console.WriteLine($"{ToolInfo.Name} rules · {Path.GetFullPath(path)}");
        Console.WriteLine();
        Console.WriteLine($"  Rules             {target.Rules.Count}");
        Console.WriteLine($"  Severity overrides{target.SeverityOverrides.Count,3}");
        Console.WriteLine($"  Allowlist entries {target.Allowlist.Count}");
        Console.WriteLine();

        foreach (var warning in validation.Warnings)
        {
            Console.WriteLine($"  warning  {warning}");
        }

        foreach (var error in validation.Errors.Count > 0 ? validation.Errors : errors)
        {
            Console.WriteLine($"  error    {error}");
        }

        Console.WriteLine();

        if (validation.IsValid)
        {
            Console.WriteLine("  Valid.");
            Console.WriteLine();
            return ExitOk;
        }

        Console.WriteLine($"  {validation.Errors.Count} error(s).");
        Console.WriteLine();
        return ExitError;
    }

    // ---------------------------------------------------------------------------------------------
    // license
    // ---------------------------------------------------------------------------------------------

    private static int RunLicense(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(LicenseUsage());
            return args.Length == 0 ? ExitError : ExitOk;
        }

        var action = args[0].ToLowerInvariant();
        var explicitPath = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));

        switch (action)
        {
            case "status":
            {
                var check = LicenseVerifier.Locate(explicitPath);
                var entitlements = Entitlements.From(check);

                Console.WriteLine();
                Console.WriteLine($"{ToolInfo.Name} licence status");
                Console.WriteLine();
                Console.WriteLine($"  Status      {check.Status}");
                Console.WriteLine($"  Detail      {check.Message}");

                if (check.License is { } licence)
                {
                    Console.WriteLine($"  Licensee    {(string.IsNullOrWhiteSpace(licence.Organisation) ? licence.Holder : licence.Organisation)}");
                    Console.WriteLine($"  Holder      {licence.Holder}");
                    Console.WriteLine($"  Edition     {licence.Edition}");
                    Console.WriteLine($"  Issued      {licence.Issued:yyyy-MM-dd}");
                    Console.WriteLine($"  Expires     {licence.Expires:yyyy-MM-dd}");
                    Console.WriteLine($"  Licence id  {licence.LicenseId}");
                }

                Console.WriteLine();
                Console.WriteLine("  Features");
                foreach (var feature in Features.All)
                {
                    var mark = entitlements.Allows(feature) ? "yes" : " no";
                    Console.WriteLine($"    [{mark}]  {Features.Describe(feature)}");
                }

                Console.WriteLine();
                Console.WriteLine($"  Free, always: scanning, all {DetectorCatalog.All.Count} built-in detectors, sequencing,");
                Console.WriteLine("                effort estimates, console/json/markdown/html output.");
                Console.WriteLine();

                // Informational: reporting the status always succeeds. Use `license show <file>`
                // when you want a non-zero exit for an invalid licence in a script.
                return ExitOk;
            }

            case "show":
            {
                if (explicitPath is null)
                {
                    Console.Error.WriteLine("license show needs a licence file.");
                    return ExitError;
                }

                var check = LicenseVerifier.VerifyFile(explicitPath);
                Console.WriteLine();
                Console.WriteLine($"  File        {Path.GetFullPath(explicitPath)}");
                Console.WriteLine($"  Status      {check.Status}");
                Console.WriteLine($"  Detail      {check.Message}");
                if (check.License is { } l)
                {
                    Console.WriteLine($"  Licence id  {l.LicenseId}");
                    Console.WriteLine($"  Holder      {l.Holder}");
                    Console.WriteLine($"  Edition     {l.Edition}");
                    Console.WriteLine($"  Expires     {l.Expires:yyyy-MM-dd}");
                    Console.WriteLine($"  Features    {string.Join(", ", l.Features)}");
                }
                Console.WriteLine();
                return check.Status is LicenseStatus.Valid ? ExitOk : ExitError;
            }

            case "install":
            {
                if (explicitPath is null)
                {
                    Console.Error.WriteLine("license install needs a licence file.");
                    return ExitError;
                }

                var check = LicenseVerifier.VerifyFile(explicitPath);
                if (check.Status is not LicenseStatus.Valid)
                {
                    Console.Error.WriteLine($"Refusing to install: {check.Message}");
                    return ExitError;
                }

                var target = LicenseVerifier.DefaultSearchPaths().First();
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.Copy(Path.GetFullPath(explicitPath), target, overwrite: true);

                Console.WriteLine($"{ToolInfo.Name}: licence installed to {target}");
                Console.WriteLine($"  {Entitlements.From(check).Describe()}");
                return ExitOk;
            }

            default:
                return Unknown($"license {action}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // shared helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly string[] SupportedFormats =
        { "console", "json", "markdown", "html", "xlsx", "summary" };

    private static Entitlements LoadEntitlements() => Entitlements.From(LicenseVerifier.Locate());

    /// <summary>
    /// Prints the refusal and returns false. The message names what is missing, what still works,
    /// and how to fix it - a gate that only says "no" loses the user.
    /// </summary>
    private static bool RequireFeature(Entitlements entitlements, string feature)
    {
        if (entitlements.Allows(feature)) return true;

        Console.Error.WriteLine();
        Console.Error.WriteLine(entitlements.RefusalMessage(feature));
        Console.Error.WriteLine();
        return false;
    }

    private static RuleSet? LoadRulesOrFail(string? path, out int exitCode)
    {
        exitCode = ExitOk;
        if (path is null) return RuleSet.Empty;

        var rules = RuleLoader.Load(path, out var errors);
        if (rules is not null) return rules;

        Console.Error.WriteLine($"Could not load the rules file '{path}':");
        foreach (var error in errors) Console.Error.WriteLine($"  - {error}");
        exitCode = ExitError;
        return null;
    }

    private static int Unknown(string what)
    {
        Console.Error.WriteLine($"Unknown command '{what}'.");
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage());
        return ExitError;
    }

    private static void WriteOrPrint(string content, string? output, bool quiet, ScanReport? report = null)
    {
        if (output is not null)
        {
            var full = Path.GetFullPath(output);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(full, content);

            if (!quiet)
            {
                Console.WriteLine($"{ToolInfo.Name}: report written to {full}");
                if (report is not null) PrintOneLineSummary(report);
            }
        }
        else if (!quiet)
        {
            Console.Write(content);
        }
    }

    private static void WriteOrPrint(string content, string? output, bool quiet, EstateReport estate)
    {
        if (output is not null)
        {
            var full = Path.GetFullPath(output);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(full, content);

            if (!quiet)
            {
                Console.WriteLine($"{ToolInfo.Name}: report written to {full}");
                PrintEstateOneLine(estate);
            }
        }
        else if (!quiet)
        {
            Console.Write(content);
        }
    }

    private static void PrintOneLineSummary(ScanReport report)
    {
        var s = report.Summary;
        Console.WriteLine(
            $"  {s.ProjectCount} project(s) · red {s.Red} / yellow {s.Yellow} / green {s.Green} · " +
            $"blockers {s.BlockerFindings} · effort {s.EffortLowDays:N0}-{s.EffortHighDays:N0} engineer-days");

        if (report.BaselineDiff is { } diff)
        {
            Console.WriteLine($"  since baseline: +{diff.NewOccurrences} new occurrence(s), -{diff.ResolvedOccurrences} resolved");
        }
    }

    private static void PrintEstateOneLine(EstateReport estate)
    {
        var s = estate.Summary;
        Console.WriteLine(
            $"  {s.AppCount} application(s) · {s.Projects} project(s) · blockers {s.Blockers} · " +
            $"effort {s.EffortLowDays:N0}-{s.EffortHighDays:N0} engineer-days");

        if (estate.Trend is { } trend)
        {
            Console.WriteLine($"  since last snapshot: net blocker change {(trend.NetBlockerChange >= 0 ? "+" : "")}{trend.NetBlockerChange}");
        }
    }

    private static void PrintDetectors()
    {
        Console.WriteLine();
        Console.WriteLine($"{ToolInfo.Name} {ToolInfo.Version} — detector catalog");
        Console.WriteLine();
        foreach (var detector in DetectorCatalog.All)
        {
            var scope = detector.Names.Count == 0 ? "project" : "source";
            Console.WriteLine($"  {detector.Code}  [{detector.Severity,-7}] [{scope,-7}] {detector.Title}");
        }
        Console.WriteLine();
        Console.WriteLine($"  {DetectorCatalog.All.Count} detectors. Exit code with --fail-on red|yellow is 2.");
        Console.WriteLine();
    }

    private static string Usage() => $"""
        {ToolInfo.Name} {ToolInfo.Version} — {ToolInfo.Tagline}

        USAGE
          {ToolInfo.Name} scan <path> [options]          Assess one solution, project or directory
          {ToolInfo.Name} baseline save <path> [options] Record the current state, to gate on drift
          {ToolInfo.Name} baseline show <file>           Summarise a recorded baseline
          {ToolInfo.Name} estate <dir> [options]         Roll up many applications into one portfolio
          {ToolInfo.Name} rules validate <file>          Check a rule file
          {ToolInfo.Name} rules template [file]          Print a starter rule file
          {ToolInfo.Name} license status                 Show licence state and features
          {ToolInfo.Name} license install <file>         Install a licence file
          {ToolInfo.Name} detectors                      List every detector
          {ToolInfo.Name} version

        <path> may be a .sln, a .slnx, a .csproj, a .vbproj, or a directory to walk.
        Everything runs locally. No network calls, no telemetry, no account.

        SCAN OPTIONS
          -f, --format <fmt>          console | json | markdown | html | xlsx | summary (default: console)
          -o, --output <file>         Write the report to a file
          -x, --exclude <pattern>     Skip projects whose path matches (repeatable)
              --fail-on <level>       none | red | yellow - exit 2 when met (default: none)
              --max-findings <n>      Findings shown per project in console (default: 6)
              --no-color              Disable ANSI colour
          -q, --quiet                 Suppress the report body

        LICENSED OPTIONS
          -r, --rules <file>          Apply a custom rule file (custom detectors, severity
                                      overrides, allowlist)
          -b, --baseline <file>       Compare against a recorded baseline
              --fail-on-new <level>   none | red | yellow | any - exit 2 on NEW findings only
          -f, --format xlsx           Organisation workbook
          -f, --format summary        Executive one-pager

        ESTATE OPTIONS
          -f, --format <fmt>          console | json | markdown | xlsx | summary
          -o, --output <file>         Write the report to a file
          -H, --history <file>        Append a snapshot and show the trend
          -r, --rules <file>          Apply a custom rule file
          -x, --exclude <pattern>     Skip applications matching (repeatable)
              --max-apps <n>          Applications listed in console (default: 20)
              --no-color, -q          As above

        EXIT CODES
          0  completed
          1  invalid input, path not found, or no projects found
          2  --fail-on or --fail-on-new threshold met
          3  a licensed feature was requested without a valid licence

        EXAMPLES
          {ToolInfo.Name} scan ./MySolution.sln
          {ToolInfo.Name} scan . --fail-on red                    # CI gate
          {ToolInfo.Name} baseline save . -o .nettriage/baseline.json
          {ToolInfo.Name} scan . --baseline .nettriage/baseline.json --fail-on-new red
          {ToolInfo.Name} estate ~/work --history .nettriage/history.json -o report.xlsx -f xlsx
          {ToolInfo.Name} scan . -r nettriage.rules.json --format summary -o exec.md
        """;

    private static string BaselineUsage() => $"""
        {ToolInfo.Name} baseline — freeze the current state so CI can fail on new problems only

        USAGE
          {ToolInfo.Name} baseline save <path> [-o <file>] [-r <rules>] [-x <pattern>] [-q]
          {ToolInfo.Name} baseline show <file>

        A baseline records how many times each detector fired in each project. It stores no line
        numbers, so ordinary edits do not invalidate it, and paths are root-relative, so it stays
        valid on another machine or in CI.

        TYPICAL USE
          {ToolInfo.Name} baseline save . -o .nettriage/baseline.json    # once, commit the file
          {ToolInfo.Name} scan . --baseline .nettriage/baseline.json --fail-on-new red
        """;

    private static string EstateUsage() => $"""
        {ToolInfo.Name} estate — roll up many applications into one ranked portfolio

        USAGE
          {ToolInfo.Name} estate <dir> [options]

        An application is the topmost directory under <dir> that contains a solution or a project
        file. Each application gets a stable id, derived from its git remote when there is one, so
        the same application keeps its identity across machines and directory moves.

        OPTIONS
          -f, --format <fmt>       console | json | markdown | xlsx | summary (default: console)
          -o, --output <file>      Write the report to a file
          -H, --history <file>     Append a snapshot to this file and show the trend since the last one
          -r, --rules <file>       Apply a custom rule file
          -x, --exclude <pattern>  Skip applications matching (repeatable)
              --max-apps <n>       Applications listed in console (default: 20)
              --no-color, -q       As above

        RISK SCORE
          blockers x10 + warnings x3 + 15 if a runtime is unsupported + effort/20 (capped at 100).
          Simple on purpose: a ranking people argue about is better than a model they distrust.
        """;

    private static string RulesUsage() => $$"""
        {{ToolInfo.Name}} rules — custom detectors, severity overrides and allowlist

        USAGE
          {{ToolInfo.Name}} rules validate <file>
          {{ToolInfo.Name}} rules template [file]

        A rule file is JSON. Each rule uses the same matching engine as the built-in detectors:
        you list the names to look for, optionally scoped by a file or project glob.

          {
            "format": "nettriage-rules/1",
            "rules": [
              { "code": "ORG001", "title": "...", "severity": "warning",
                "names": ["log4net.LogManager"], "fileGlob": "src/**/*.cs" }
            ],
            "severityOverrides": { "NT1024": "warning" },
            "allowlist": [
              { "detector": "NT1010", "file": "src/Printing/**",
                "reason": "Windows-only label printing", "expires": "2027-06-30" }
            ]
          }

        Suppression is at project + detector granularity: an entry with a file glob applies when
        any reported location for that finding matches. Suppressed findings are listed in the
        report, never dropped silently, and an entry past its expiry stops applying and says so.
        """;

    private static string LicenseUsage() => $"""
        {ToolInfo.Name} license — check and install a licence

        USAGE
          {ToolInfo.Name} license status [<file>]
          {ToolInfo.Name} license show <file>
          {ToolInfo.Name} license install <file>

        Licences are signed files, verified locally against a public key built into this binary.
        There is no activation server and no account, so it works air-gapped.

        A licence is looked for, in order, at:
          --license <file>  ·  ${LicenseVerifier.EnvironmentVariable}  ·  ~/.nettriage/license.json  ·  ./nettriage-license.json

        Without a licence, scanning, all {DetectorCatalog.All.Count} built-in detectors, sequencing, effort
        estimates and console/json/markdown/html output keep working.
        """;
}
