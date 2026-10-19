// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoFlow.Core.Develop;
using PhotoFlow.Core.Paths;

namespace PhotoFlow.App.Services.Develop;

/// <summary>
/// Previews of Develop with the noise reduction already computed (M8): a photo prepared in the background opens at once.
/// Kept in the cache folder of the app under the photo and the exact settings (a change of any setting is a new entry);
/// the oldest entries go when there are more than <see cref="MaxEntries"/>.
/// </summary>
public sealed class PreviewCache
{
    public const int MaxEntries = 400;

    private readonly string _folder;
    private readonly InternalFileCleaner _cleaner;
    private readonly object _gate = new();

    public PreviewCache(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _folder = Path.Combine(paths.CacheDirectory, "develop");
        _cleaner = new InternalFileCleaner(paths);
    }

    /// <summary>Key of the preview of a photo with the settings the engine receives (keys sorted, numbers normalized).</summary>
    public static string Key(long assetId, JsonObject engineSettings, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(engineSettings);
        var text = Canonical(engineSettings) + "|" + maxSide.ToString(CultureInfo.InvariantCulture);
        return assetId.ToString(CultureInfo.InvariantCulture) + "-" + Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public bool Contains(string key) => File.Exists(Path.Combine(_folder, key + ".json"));

    /// <summary>The preview, or null; its files belong to the cache (do not delete them after showing).</summary>
    public DevelopRender? TryGet(string key)
    {
        var meta = Path.Combine(_folder, key + ".json");
        var before = Path.Combine(_folder, key + "-prima.png");
        var after = Path.Combine(_folder, key + "-dopo.png");
        if (!File.Exists(meta) || !File.Exists(before) || !File.Exists(after))
        {
            return null;
        }

        try
        {
            var m = JsonNode.Parse(File.ReadAllText(meta)) as JsonObject ?? [];
            var difference = Path.Combine(_folder, key + "-differenza.png");
            File.SetLastWriteTimeUtc(meta, DateTime.UtcNow); // recently used
            return new DevelopRender(before, after, File.Exists(difference) ? difference : null, Json.Int(m["width"]), Json.Int(m["height"]),
                (0, 0, Json.Int(m["frameWidth"]), Json.Int(m["frameHeight"])), m["reduction"] is null ? null : Json.Int(m["reduction"]),
                (Json.Int(m["frameWidth"]), Json.Int(m["frameHeight"])));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps a full render of the engine under <paramref name="key"/> (the engine's files are moved here).</summary>
    public DevelopRender? Store(string key, DevelopRender render)
    {
        ArgumentNullException.ThrowIfNull(render);
        if (render.AfterFile is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(_folder);
            Move(render.BeforeFile, key + "-prima.png");
            Move(render.AfterFile, key + "-dopo.png");
            if (render.DifferenceFile is { } difference)
            {
                Move(difference, key + "-differenza.png");
            }

            var frame = render.Frame.Width > 0 ? render.Frame : (render.Region.Width, render.Region.Height);
            var meta = new JsonObject
            {
                ["width"] = render.Width, ["height"] = render.Height, ["frameWidth"] = frame.Width, ["frameHeight"] = frame.Height,
                ["reduction"] = render.Reduction,
            };
            File.WriteAllText(Path.Combine(_folder, key + ".json"), meta.ToJsonString());
            Trim();
            return TryGet(key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Move(string source, string name)
    {
        var target = Path.Combine(_folder, name);
        File.Copy(source, target, overwrite: true);
        if (_cleaner.IsInternal(source))
        {
            _cleaner.DeleteInternalFile(source);
        }
    }

    private void Trim()
    {
        lock (_gate)
        {
            var entries = new DirectoryInfo(_folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxEntries).ToList();
            foreach (var entry in entries)
            {
                var key = Path.GetFileNameWithoutExtension(entry.Name);
                foreach (var file in new[] { entry.FullName, Path.Combine(_folder, key + "-prima.png"), Path.Combine(_folder, key + "-dopo.png"), Path.Combine(_folder, key + "-differenza.png") })
                {
                    if (File.Exists(file))
                    {
                        _cleaner.DeleteInternalFile(file);
                    }
                }
            }
        }
    }

    /// <summary>JSON with the keys sorted and the numbers written the same way, whatever produced it.</summary>
    public static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => Json.Double(v).ToString("R", CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };
}
