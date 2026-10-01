using NetTriage.Core;
using NetTriage.Core.Analysis;
using NetTriage.Core.Reporting;

namespace NetTriage.Cli;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitThresholdMet = 2;

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

    private static int RunScan(string[] args)
    {
        string? root = null;
        var format = "console";
        string? output = null;
        var excludes = new List<string>();
        var failOn = "none";
        bool noColor = false, quiet = false;
        int maxFindings = 6;

        for (int i = 0; i < args.Length; i++)
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

        if (!ReporterFactory.SupportedFormats.Contains(format))
        {
            Console.Error.WriteLine($"Unsupported format '{format}'. Use one of: {string.Join(", ", ReporterFactory.SupportedFormats)}.");
            return ExitError;
        }

        if (failOn is not ("none" or "red" or "yellow"))
        {
            Console.Error.WriteLine("--fail-on accepts: none, red, yellow.");
            return ExitError;
        }

        var useColor = !noColor
                       && format == "console"
                       && output is null
                       && !Console.IsOutputRedirected
                       && Environment.GetEnvironmentVariable("NO_COLOR") is null or "";

        var options = new ScanOptions { Root = root, Exclude = excludes };
        var report = ScanEngine.Scan(options);

        var reporter = format == "console"
            ? new ConsoleReporter(useColor, maxFindings)
            : ReporterFactory.Create(format, useColor);

        var rendered = reporter.Render(report);

        if (output is not null)
        {
            var full = Path.GetFullPath(output);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(full, rendered);

            if (!quiet)
            {
                Console.WriteLine($"{ToolInfo.Name}: report written to {full}");
                PrintOneLineSummary(report);
            }
        }
        else if (!quiet)
        {
            Console.Write(rendered);
        }

        if (report.Summary.ProjectCount == 0)
        {
            if (!quiet) Console.Error.WriteLine($"{ToolInfo.Name}: no projects found - nothing to report.");
            return ExitError;
        }

        return failOn switch
        {
            "red" when report.Summary.Red > 0 => ExitThresholdMet,
            "yellow" when report.Summary.Red > 0 || report.Summary.Yellow > 0 => ExitThresholdMet,
            _ => ExitOk,
        };
    }

    private static void PrintOneLineSummary(ScanReport report)
    {
        var s = report.Summary;
        Console.WriteLine(
            $"  {s.ProjectCount} project(s) · red {s.Red} / yellow {s.Yellow} / green {s.Green} · " +
            $"blockers {s.BlockerFindings} · effort {s.EffortLowDays:N0}-{s.EffortHighDays:N0} engineer-days");
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
          {ToolInfo.Name} scan <path> [options]
          {ToolInfo.Name} detectors
          {ToolInfo.Name} version

        <path> may be a .sln, a .slnx, a .csproj, a .vbproj, or a directory to walk.
        Everything runs locally. No network calls, no telemetry, no account.

        OPTIONS
          -f, --format <console|json|markdown|html>   Output format (default: console)
          -o, --output <file>                         Write the report to a file
          -x, --exclude <pattern>                     Skip projects whose path matches
                                                      (substring or * wildcard; repeatable)
              --fail-on <none|red|yellow>             Exit 2 when the threshold is met,
                                                      for use as a CI gate (default: none)
              --max-findings <n>                      Findings shown per project in console (default: 6)
              --no-color                              Disable ANSI colour
          -q, --quiet                                 Suppress the report body
          -h, --help                                  Show this help

        EXIT CODES
          0  scan completed
          1  invalid input, path not found, or no projects found
          2  --fail-on threshold met

        EXAMPLES
          {ToolInfo.Name} scan ./MySolution.sln
          {ToolInfo.Name} scan . --format html -o triage.html
          {ToolInfo.Name} scan . --fail-on red          # CI gate
          {ToolInfo.Name} scan . -x "**/Legacy/**" -x Samples
        """;
}
