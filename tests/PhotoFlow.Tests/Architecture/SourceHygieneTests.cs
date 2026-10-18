// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Architecture;

/// <summary>SPDX headers everywhere (CLAUDE.md §4) and no hard-coded absolute paths in product code (§3, §10).</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed partial class SourceHygieneTests
{
    private const string SpdxLine = "SPDX-License-Identifier: Apache-2.0";

    [Fact]
    public void Every_csharp_file_starts_with_the_spdx_header()
    {
        var missing = SourceFiles("*.cs", "src", "tests")
            .Where(f => !File.ReadLines(f).First().Equals("// " + SpdxLine, StringComparison.Ordinal))
            .ToList();
        Assert.True(missing.Count == 0, "Missing SPDX header:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Every_xaml_file_declares_the_spdx_license()
    {
        var missing = SourceFiles("*.axaml", "src")
            .Where(f => !File.ReadLines(f).Take(3).Any(l => l.Contains(SpdxLine, StringComparison.Ordinal)))
            .ToList();
        Assert.True(missing.Count == 0, "Missing SPDX comment:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Product_code_contains_no_absolute_paths()
    {
        var offenders = SourceFiles("*.cs", "src")
            .Concat(SourceFiles("*.axaml", "src"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => AbsolutePath().IsMatch(l.Text) && !l.Text.Contains("path-hygiene: allowed", StringComparison.Ordinal))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "Absolute paths must go through IAppPaths:\n" + string.Join('\n', offenders));
    }

    // "C:\..." or "C:/..." inside a string literal, or Unix home/system roots.
    [GeneratedRegex("\"[A-Za-z]:[\\\\/]|\"/(home|Users|mnt|media|Volumes|var|tmp)/")]
    private static partial Regex AbsolutePath();

    private static IEnumerable<string> SourceFiles(string pattern, params string[] roots) =>
        roots.SelectMany(r => Directory.EnumerateFiles(Repository.PathOf(r), pattern, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
