// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PhotoFlow.Core.Analysis;

namespace PhotoFlow.Analysis.Profiles;

/// <summary>
/// The scene profiles in use (spec §6, board 5e): the built-in ones, read-only, and the custom ones, duplicated from
/// another profile and saved as versioned JSON files in the profiles folder of the configuration
/// (<c>{id}.v{version}.json</c>; every save writes a new version and keeps the previous files). A profile file that
/// cannot be read is skipped and reported, never deleted.
/// </summary>
public sealed partial class ProfileLibrary
{
    public const string CustomPrefix = "custom-";
    public const string FallbackId = "events";

    private readonly string _directory;
    private readonly object _gate = new();
    private IReadOnlyList<SceneProfile> _all = BuiltInProfiles.All;

    public ProfileLibrary(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Reload();
    }

    /// <summary>Built-in profiles in their order, then the custom ones by name.</summary>
    public IReadOnlyList<SceneProfile> All => Volatile.Read(ref _all);

    /// <summary>Files that could not be read at the last load (name and reason).</summary>
    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    public SceneProfile? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The profile with this id; a job or a result that names a profile no longer available (a custom file moved away)
    /// is decided with the Events profile rather than failing.
    /// </summary>
    public SceneProfile Get(string id) => Find(id) ?? BuiltInProfiles.Get(FallbackId);

    public void Reload()
    {
        var errors = new List<string>();
        var custom = new Dictionary<string, SceneProfile>(StringComparer.Ordinal);
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    var profile = BuiltInProfiles.Parse(File.ReadAllText(file), isBuiltIn: false);
                    if (!profile.Id.StartsWith(CustomPrefix, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"Profile '{profile.Id}': custom ids start with '{CustomPrefix}'.");
                    }

                    if (!custom.TryGetValue(profile.Id, out var current) || current.Version < profile.Version)
                    {
                        custom[profile.Id] = profile;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                {
                    errors.Add($"{Path.GetFileName(file)}: {e.Message}");
                }
            }
        }

        LoadErrors = errors;
        Volatile.Write(ref _all, [.. BuiltInProfiles.All, .. custom.Values.OrderBy(p => p.Name, StringComparer.CurrentCulture)]);
    }

    /// <summary>A new custom profile with the values of <paramref name="sourceId"/> (Ctrl D).</summary>
    public SceneProfile Duplicate(string sourceId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var source = Find(sourceId) ?? throw new KeyNotFoundException($"Unknown profile '{sourceId}'.");
        lock (_gate)
        {
            var slug = Slug(name);
            var id = CustomPrefix + slug;
            for (var n = 2; Find(id) is not null || Directory.Exists(_directory) && Directory.EnumerateFiles(_directory, id + ".v*.json").Any(); n++)
            {
                id = CustomPrefix + slug + "-" + n.ToString(CultureInfo.InvariantCulture);
            }

            var copy = source with { Id = id, Name = name.Trim(), Version = 1, IsBuiltIn = false, BasedOn = source.IsBuiltIn ? source.Id : source.BasedOn ?? source.Id };
            Write(copy);
            return copy;
        }
    }

    /// <summary>Saves the changes of a custom profile as its next version; built-in profiles are read-only.</summary>
    public SceneProfile Save(SceneProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            var current = Find(profile.Id);
            if (current is null || current.IsBuiltIn || profile.IsBuiltIn)
            {
                throw new InvalidOperationException($"Profile '{profile.Id}' is not a custom profile.");
            }

            var next = profile with { Version = current.Version + 1 };
            Write(next);
            return next;
        }
    }

    private void Write(SceneProfile profile)
    {
        var json = BuiltInProfiles.Serialize(profile);
        BuiltInProfiles.Parse(json, isBuiltIn: false); // the same checks as when loading
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, FormattableString.Invariant($"{profile.Id}.v{profile.Version}.json"));
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: false);
        Reload();
    }

    private static string Slug(string name)
    {
        var normalized = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var ascii = new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        var slug = NonAlphanumeric().Replace(ascii, "-").Trim('-');
        return slug.Length == 0 ? "profile" : slug[..Math.Min(slug.Length, 40)];
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
