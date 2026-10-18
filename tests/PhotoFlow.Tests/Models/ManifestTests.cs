// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;
using PhotoFlow.Core.Inference;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Models;

/// <summary>models/manifest.json rules (CLAUDE.md §6, spec §3 and §11).</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed partial class ManifestTests
{
    private static readonly Lazy<JsonDocument> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllText(Repository.PathOf("models", "manifest.json"))));

    private static IEnumerable<JsonElement> Models => Manifest.Value.RootElement.GetProperty("models").EnumerateArray();

    [Fact]
    public void Model_ids_are_unique()
    {
        var ids = Models.Select(m => m.GetProperty("id").GetString()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Every_model_has_the_required_fields()
    {
        foreach (var model in Models)
        {
            var id = model.GetProperty("id").GetString();
            foreach (var field in new[] { "function", "name", "version", "adapter" })
            {
                Assert.False(string.IsNullOrWhiteSpace(model.GetProperty(field).GetString()), $"{id}: '{field}' is empty");
            }

            Assert.True(Enum.TryParse<ModelFunction>(model.GetProperty("function").GetString(), out _), $"{id}: unknown function");
            Assert.InRange(model.GetProperty("phase").GetInt32(), 1, 5);
        }
    }

    [Fact]
    public void Every_file_is_pinned_and_verifiable()
    {
        foreach (var model in Models)
        {
            var id = model.GetProperty("id").GetString();
            var files = model.GetProperty("files").EnumerateArray().ToList();
            Assert.NotEmpty(files);
            foreach (var file in files)
            {
                var url = file.GetProperty("url").GetString()!;
                Assert.StartsWith("https://", url, StringComparison.Ordinal);
                Assert.True(PinnedUrl().IsMatch(url), $"{id}: URL not pinned to a commit or release: {url}");
                Assert.Matches(Sha256(), file.GetProperty("sha256").GetString()!);
                Assert.True(file.GetProperty("size").GetInt64() > 0, $"{id}: size missing");
                if (file.TryGetProperty("mirrors", out var mirrors))
                {
                    Assert.All(mirrors.EnumerateArray(), m => Assert.StartsWith("https://", m.GetString(), StringComparison.Ordinal));
                }
            }
        }
    }

    [Fact]
    public void Every_license_is_documented_and_non_commercial_requires_acceptance()
    {
        foreach (var model in Models)
        {
            var id = model.GetProperty("id").GetString();
            var license = model.GetProperty("license");
            Assert.False(string.IsNullOrWhiteSpace(license.GetProperty("spdx").GetString()), $"{id}: license missing");
            Assert.StartsWith("https://", license.GetProperty("source").GetString(), StringComparison.Ordinal);
            if (!license.GetProperty("commercialUse").GetBoolean())
            {
                Assert.True(license.GetProperty("requiresAcceptance").GetBoolean(), $"{id}: non-commercial model must require acceptance");
            }
        }
    }

    [Fact]
    public void Buffalo_l_requires_explicit_acceptance()
    {
        var buffalo = Models.Single(m => m.GetProperty("id").GetString() == "insightface-buffalo-l");
        Assert.False(buffalo.GetProperty("license").GetProperty("commercialUse").GetBoolean());
        Assert.True(buffalo.GetProperty("license").GetProperty("requiresAcceptance").GetBoolean());
    }

    [Fact]
    public void Model_files_are_never_in_the_repository()
    {
        string[] modelExtensions = [".onnx", ".pth", ".pt", ".safetensors", ".ckpt"];
        var found = Directory.EnumerateFiles(Repository.Root, "*", SearchOption.AllDirectories)
            .Where(f => modelExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}.venv{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.True(found.Count == 0, "Model files inside the repository:\n" + string.Join('\n', found));
    }

    // A 40-hex commit in the path, or an immutable GitHub release download.
    [GeneratedRegex("/[0-9a-f]{40}/|/releases/download/[^/]+/")]
    private static partial Regex PinnedUrl();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();
}
