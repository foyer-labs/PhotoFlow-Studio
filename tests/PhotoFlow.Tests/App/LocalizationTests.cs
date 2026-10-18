// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using System.Xml.Linq;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>Every UI text key used by the code and the views exists in English and Italian (CLAUDE.md §4).</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed partial class LocalizationTests
{
    /// <summary>Keys built at run time from enum values or ids.</summary>
    private static readonly string[] DynamicPrefixes =
    [
        "Outcome.", "Reason.", "Profile.", "JobType.", "Jobs.State.", "Jobs.Pause.", "Stage.", "Signal.Sharpness.", "Eye.",
        "Import.Faces.", "Library.Policy.", "Review.Origin.", "Review.Empty.", "Review.Mode.", "Import.Destination.", "Status.Faces.",
    ];

    [Fact]
    public void Every_used_key_exists_in_both_languages()
    {
        var root = RepositoryRoot();
        var app = Path.Combine(root, "src", "PhotoFlow.App");
        var english = Keys(Path.Combine(app, "Localization", "Strings.resx"));
        var italian = Keys(Path.Combine(app, "Localization", "Strings.it.resx"));
        Assert.Equal(english.Order(StringComparer.Ordinal), italian.Order(StringComparer.Ordinal));

        var used = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => UsedKey().Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
            .Where(k => !k.EndsWith('.'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(used.Where(k => !english.Contains(k)).Order(StringComparer.Ordinal));
        foreach (var prefix in DynamicPrefixes)
        {
            Assert.Contains(english, k => k.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    private static HashSet<string> Keys(string resx) =>
        XDocument.Load(resx).Root!.Elements("data").Select(e => e.Attribute("name")!.Value).ToHashSet(StringComparer.Ordinal);

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "PhotoFlow.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [GeneratedRegex("""(?:Strings\.(?:Get|Format)\("|\{l:Tr )(?<key>[A-Za-z0-9_.]+)""")]
    private static partial Regex UsedKey();
}
