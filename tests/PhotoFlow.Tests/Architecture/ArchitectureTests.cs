// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Architecture;

/// <summary>Project boundaries of CLAUDE.md §3: Core has no dependencies, each external library lives in one project.</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class ArchitectureTests
{
    // Allowed package references per project (prefix match). Anything else is a boundary violation.
    private static readonly Dictionary<string, string[]> AllowedPackages = new()
    {
        ["PhotoFlow.Core"] = [],
        ["PhotoFlow.Sources"] = ["System.IO.Hashing"],
        ["PhotoFlow.Metadata"] = ["System.IO.Hashing"],
        ["PhotoFlow.Analysis"] = [],
        ["PhotoFlow.Faces"] = ["System.Security.Cryptography.ProtectedData"],
        ["PhotoFlow.Tagging"] = [],
        ["PhotoFlow.Data"] = ["Microsoft.Data.Sqlite", "Dapper"],
        ["PhotoFlow.Imaging"] = ["SkiaSharp", "Magick.NET"],
        ["PhotoFlow.Inference"] = ["Microsoft.ML.OnnxRuntime"],
        ["PhotoFlow.App"] = ["Avalonia", "CommunityToolkit.Mvvm", "Microsoft.Extensions.", "Serilog", "Velopack"],

        // Level D captions in a process of their own (phase 5b, exception U4 of CLAUDE.md §3).
        ["PhotoFlow.Vlm"] = ["Microsoft.ML.OnnxRuntimeGenAI"],
    };

    /// <summary>Projects that run on their own and share nothing with the application but a protocol.</summary>
    private static readonly HashSet<string> Standalone = ["PhotoFlow.Vlm"];

    public static TheoryData<string> Projects => new(AllowedPackages.Keys);

    [Theory]
    [MemberData(nameof(Projects))]
    public void Project_references_only_allowed_packages(string project)
    {
        var packages = ReadItems(project, "PackageReference");
        var allowed = AllowedPackages[project];
        var violations = packages.Where(p => !allowed.Any(a => p.StartsWith(a, StringComparison.Ordinal))).ToList();
        Assert.True(violations.Count == 0, $"{project} references packages outside its boundary: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Core_has_no_project_or_package_references()
    {
        Assert.Empty(ReadItems("PhotoFlow.Core", "ProjectReference"));
        Assert.Empty(ReadItems("PhotoFlow.Core", "PackageReference"));
    }

    [Fact]
    public void Core_assembly_references_only_the_base_class_library()
    {
        var references = typeof(IAppPaths).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        var external = references.Where(n => !(n.StartsWith("System", StringComparison.Ordinal) || n is "netstandard" or "mscorlib")).ToList();
        Assert.True(external.Count == 0, $"PhotoFlow.Core references: {string.Join(", ", external)}");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Every_project_except_core_references_core(string project)
    {
        if (project == "PhotoFlow.Core" || Standalone.Contains(project))
        {
            return;
        }

        Assert.Contains(ReadItems(project, "ProjectReference"), r => r.EndsWith("PhotoFlow.Core.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void All_source_projects_are_covered_by_the_boundary_table()
    {
        var onDisk = Directory.GetDirectories(Repository.PathOf("src")).Select(Path.GetFileName).ToHashSet();
        Assert.Equal(AllowedPackages.Keys.Order(), onDisk.Order()!);
    }

    private static List<string> ReadItems(string project, string itemName)
    {
        var path = Repository.PathOf("src", project, project + ".csproj");
        var document = XDocument.Load(path);
        return document.Descendants(itemName)
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Select(v => v.Replace('\\', '/'))
            .ToList();
    }
}
