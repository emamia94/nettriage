using System.Reflection;

namespace NetTriage.Core;

public static class ToolInfo
{
    public const string Name = "nettriage";

    /// <summary>
    /// Read from the assembly's informational version, which MSBuild fills from the single
    /// <c>&lt;Version&gt;</c> in <c>Directory.Build.props</c>. There is deliberately no second copy of
    /// the version string in the source: the package, the binary and <c>nettriage version</c>
    /// cannot disagree if only one of them exists.
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    public const string Tagline = "deterministic .NET modernization triage";

    private static string ResolveVersion()
    {
        var informational = typeof(ToolInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational)) return "0.0.0";

        // SourceLink appends "+<commit sha>"; the version is what comes before it.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
