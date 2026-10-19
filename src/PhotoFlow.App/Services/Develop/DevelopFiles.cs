// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Develop;
using PhotoFlow.Core.IO;

namespace PhotoFlow.App.Services.Develop;

/// <summary>
/// The development settings of every photo Develop can open (decision S24): the catalog for imported photos, and for
/// files opened from disk without importing them a small JSON file per photo in the data folder, never next to the
/// original. A file from disk has a negative id derived from its full path, the same in every session, so its settings,
/// previews and exports are found again when it is reopened.
/// </summary>
public sealed class DevelopFiles(ICatalog catalog, IFileSystem fileSystem, string directory) : IDevelopStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<long, string> _paths = new();
    private readonly Lock _lock = new();

    /// <summary>The extensions Develop opens from disk (RAW of the main makers, DNG, JPEG, PNG, TIFF).</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        ["nef", "nrw", "cr2", "cr3", "crw", "arw", "srf", "sr2", "dng", "raf", "orf", "rw2", "pef", "srw", "x3f", "3fr", "iiq", "jpg", "jpeg", "png", "tif", "tiff"];

    public static bool IsLoose(long id) => id < 0;

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    /// <summary>The id of a file from disk (negative, stable): registers its path for this session.</summary>
    public long Register(string path)
    {
        var full = Path.GetFullPath(path);
        var id = IdOf(full);
        _paths.TryAdd(id, full); // the same file written with another case keeps its first spelling
        return id;
    }

    public static long IdOf(string fullPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()));
        var value = BitConverter.ToInt64(hash, 0) & 0x3FFF_FFFF_FFFF_FFFF;
        return -(value == 0 ? 1 : value);
    }

    /// <summary>The path of a file from disk, known in this session or from its saved settings.</summary>
    public string? PathOf(long id)
    {
        if (!IsLoose(id))
        {
            return null;
        }

        if (_paths.TryGetValue(id, out var path))
        {
            return path;
        }

        if (Read(id)?["path"]?.GetValue<string>() is { } saved)
        {
            _paths[id] = saved;
            return saved;
        }

        return null;
    }

    /// <summary>Where the file of a photo can be read now: the original from disk, or the catalog location.</summary>
    public async Task<string?> LocateAsync(long id, CancellationToken ct) =>
        IsLoose(id)
            ? PathOf(id) is { } path && fileSystem.FileExists(path) ? path : null
            : await AssetFiles.LocateAsync(catalog, fileSystem, id, ct).ConfigureAwait(false);

    public async Task<string?> GetSettingsAsync(long assetId, CancellationToken ct) =>
        IsLoose(assetId)
            ? Read(assetId)?["settings"] is JsonObject settings ? settings.ToJsonString() : null
            : await catalog.Develop.GetSettingsAsync(assetId, ct).ConfigureAwait(false);

    public Task SetSettingsAsync(long assetId, string settingsJson, CancellationToken ct)
    {
        if (!IsLoose(assetId))
        {
            return catalog.Develop.SetSettingsAsync(assetId, settingsJson, ct);
        }

        Update(assetId, file => file["settings"] = JsonNode.Parse(settingsJson));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlySet<long>> ListDevelopedAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var result = new HashSet<long>(await catalog.Develop.ListDevelopedAsync([.. assetIds.Where(id => !IsLoose(id))], ct).ConfigureAwait(false));
        result.UnionWith(assetIds.Where(id => IsLoose(id) && Read(id)?["settings"] is JsonObject));
        return result;
    }

    public Task AddExportAsync(ExportRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsLoose(record.AssetId))
        {
            return catalog.Develop.AddExportAsync(record, ct);
        }

        Update(record.AssetId, file => file["export"] = new JsonObject
        {
            ["output"] = record.OutputPath,
            ["recipe"] = record.Recipe,
            ["at"] = record.ExportedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        });
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<long, ExportRecord>> LastExportsAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var result = new Dictionary<long, ExportRecord>(await catalog.Develop.LastExportsAsync([.. assetIds.Where(id => !IsLoose(id))], ct).ConfigureAwait(false));
        foreach (var id in assetIds.Where(IsLoose))
        {
            if (Read(id)?["export"] is JsonObject export && export["output"]?.GetValue<string>() is { } output)
            {
                var at = DateTimeOffset.TryParse(export["at"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : DateTimeOffset.MinValue;
                result[id] = new ExportRecord(id, output, export["recipe"]?.GetValue<string>() ?? string.Empty, at);
            }
        }

        return result;
    }

    private string FileOf(long id) => Path.Combine(directory, $"{-id:x16}.json");

    private JsonObject? Read(long id)
    {
        lock (_lock)
        {
            var file = FileOf(id);
            try
            {
                return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private void Update(long id, Action<JsonObject> change)
    {
        var path = PathOf(id) ?? throw new InvalidOperationException($"Unknown file {id}.");
        lock (_lock)
        {
            var file = FileOf(id);
            JsonObject content;
            try
            {
                content = File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject existing ? existing : [];
            }
            catch (JsonException)
            {
                content = [];
            }

            content["path"] = path;
            change(content);
            Directory.CreateDirectory(directory);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, content.ToJsonString(Indented));
            File.Move(temporary, file, overwrite: true);
        }
    }
}
