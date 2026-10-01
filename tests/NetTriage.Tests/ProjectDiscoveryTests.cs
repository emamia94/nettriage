using NetTriage.Core;
using NetTriage.Core.Analysis;
using Xunit;

namespace NetTriage.Tests;

/// <summary>A throwaway directory tree that deletes itself when the test finishes.</summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; }

    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "nettriage-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string Dir(string relativePath)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

public static class Fixtures
{
    /// <summary>A legacy, non-SDK project file targeting .NET Framework 4.8.</summary>
    public static string LegacyCsproj(string assemblyName = "Legacy.Lib", string tfm = "v4.8")
    {
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003" DefaultTargets="Build">
              <PropertyGroup>
                <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
                <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
                <ProjectGuid>{PLACEHOLDER_GUID}</ProjectGuid>
                <OutputType>Library</OutputType>
                <RootNamespace>PLACEHOLDER_NAME</RootNamespace>
                <AssemblyName>PLACEHOLDER_NAME</AssemblyName>
                <TargetFrameworkVersion>PLACEHOLDER_TFM</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="System" />
                <Reference Include="System.Web" />
              </ItemGroup>
              <ItemGroup>
                <Compile Include="Service.cs" />
              </ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
            </Project>
            """
            .Replace("PLACEHOLDER_GUID", Guid.NewGuid().ToString().ToUpperInvariant())
            .Replace("PLACEHOLDER_NAME", assemblyName)
            .Replace("PLACEHOLDER_TFM", tfm);
    }

    /// <summary>An SDK-style project, optionally multi-targeting.</summary>
    public static string SdkCsproj(string tfms) =>
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFrameworks>PLACEHOLDER_TFMS</TargetFrameworks>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """.Replace("PLACEHOLDER_TFMS", tfms);

    public static string Solution(params (string Name, string RelativePath, string Guid)[] projects)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");

        foreach (var (name, relative, guid) in projects)
        {
            sb.AppendLine($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{relative}\", \"{{{guid}}}\"");
            sb.AppendLine("EndProject");
        }

        sb.AppendLine("Global");
        sb.AppendLine("EndGlobal");
        return sb.ToString();
    }
}

public class ProjectDiscoveryTests
{
    [Fact]
    public void ReadsProjectsFromSolutionUsingWindowsStylePaths()
    {
        using var tree = new TempTree();
        tree.Write(@"src\Lib\Lib.csproj", Fixtures.LegacyCsproj());
        var sln = tree.Write("Demo.sln", Fixtures.Solution(
            ("Lib", @"src\Lib\Lib.csproj", "11111111-1111-1111-1111-111111111111")));

        var warnings = new List<string>();
        var projects = ProjectDiscovery.Discover(sln, warnings);

        Assert.Single(projects);
        Assert.EndsWith("Lib.csproj", projects[0]);
    }

    [Fact]
    public void ReadsProjectsFromSlnx()
    {
        using var tree = new TempTree();
        tree.Write("src/Lib/Lib.csproj", Fixtures.LegacyCsproj());
        var slnx = tree.Write("Demo.slnx", """
            <Solution>
              <Project Path="src/Lib/Lib.csproj" />
            </Solution>
            """);

        var projects = ProjectDiscovery.Discover(slnx, new List<string>());

        Assert.Single(projects);
    }

    [Fact]
    public void FallsBackToDirectoryScanAndSkipsBuildOutput()
    {
        using var tree = new TempTree();
        tree.Write("src/Lib/Lib.csproj", Fixtures.LegacyCsproj());
        tree.Write("src/App/App.csproj", Fixtures.SdkCsproj("net8.0"));
        // These must never be picked up.
        tree.Write("src/Lib/bin/Debug/Copy.csproj", Fixtures.LegacyCsproj());
        tree.Write("src/Lib/obj/Temp.csproj", Fixtures.LegacyCsproj());

        var warnings = new List<string>();
        var projects = ProjectDiscovery.Discover(tree.Root, warnings);

        Assert.Equal(2, projects.Count);
        Assert.DoesNotContain(projects, p => p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        Assert.DoesNotContain(projects, p => p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void WarnsWhenSolutionReferencesNoProjects()
    {
        using var tree = new TempTree();
        var sln = tree.Write("Empty.sln", "Microsoft Visual Studio Solution File, Format Version 12.00\n");

        var warnings = new List<string>();
        ProjectDiscovery.Discover(sln, warnings);

        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void WarnsWhenSolutionPathsCannotBeResolved()
    {
        using var tree = new TempTree();
        var sln = tree.Write("Broken.sln", Fixtures.Solution(
            ("Missing", @"src\Missing\Missing.csproj", "22222222-2222-2222-2222-222222222222")));

        var warnings = new List<string>();
        ProjectDiscovery.Discover(sln, warnings);

        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void SkipListCoversCommonBuildDirectories()
    {
        Assert.True(ProjectDiscovery.IsSkippedDirectory("bin"));
        Assert.True(ProjectDiscovery.IsSkippedDirectory("obj"));
        Assert.True(ProjectDiscovery.IsSkippedDirectory(".git"));
        Assert.True(ProjectDiscovery.IsSkippedDirectory("node_modules"));
        Assert.False(ProjectDiscovery.IsSkippedDirectory("src"));
    }
}
