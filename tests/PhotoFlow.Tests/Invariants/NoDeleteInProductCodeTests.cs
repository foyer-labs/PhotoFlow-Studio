// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Invariants;

/// <summary>
/// I4 at the source level: product code contains no file or directory deletion, except
/// <c>InternalFileCleaner</c>, the single place allowed to delete PhotoFlow's own internal files (decision B1).
/// </summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed partial class NoDeleteInProductCodeTests
{
    [Fact]
    public void Product_code_never_deletes_files_or_directories()
    {
        var offenders = Directory.EnumerateFiles(Repository.PathOf("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.EndsWith($"{Path.DirectorySeparatorChar}Paths{Path.DirectorySeparatorChar}InternalFileCleaner.cs", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => DeleteCall().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "Deletion found in product code (invariant I4):\n" + string.Join('\n', offenders));
    }

    // File.Delete, Directory.Delete, FileInfo/DirectoryInfo .Delete(), FileSystem.DeleteFile...
    [GeneratedRegex(@"\b(File|Directory)\.Delete\s*\(|\.Delete\s*\(\s*(true|false|recursive)?\s*\)|DeleteFile|DeleteDirectory|RemoveDirectory")]
    private static partial Regex DeleteCall();
}
