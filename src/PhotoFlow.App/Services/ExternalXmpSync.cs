// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;

namespace PhotoFlow.App.Services;

/// <param name="Read">Files read because their state changed.</param>
/// <param name="Changed">Photos whose catalog data changed with the import.</param>
public sealed record XmpSyncReport(int Read, int Changed, IReadOnlyList<XmpSyncLogEntry> Log);

/// <summary>
/// Synchronization with the XMP changes made by other programs (spec §4, decision F5-5). After each write PhotoFlow
/// records the state of the file that holds the XMP and what it contains; when the state differs, the file is read and
/// what changed is imported as the user's:
/// <list type="bullet">
/// <item>keywords added become manual tags; keywords of automatic tags removed suppress those tags (the external
/// version wins); manual tags of the catalog are never removed (they go back in the file at the next write);</item>
/// <item>rating and label: the most recent change wins (the date of the file against the user's last change in the
/// catalog); the description becomes the user's caption;</item>
/// <item>face regions named by other programs are noted (they can be imported in the people gallery).</item>
/// </list>
/// Every import and conflict is logged (<c>XmpSyncLog</c>). Files are only read (I1, I2).
/// </summary>
public sealed class ExternalXmpSync(ICatalog catalog, IMetadataReader reader)
{
    private static readonly JsonSerializerOptions Json = new();

    /// <summary>Records the state and content of the XMP after PhotoFlow wrote it (base of the next sync).</summary>
    public async Task RecordAsync(long assetId, MetadataTarget target, WritePolicy policy, CancellationToken ct)
    {
        var stamp = await reader.StampAsync(target, policy, ct).ConfigureAwait(false);
        var snapshot = await reader.ReadAsync(target, policy, ct).ConfigureAwait(false);
        await catalog.XmpSync.RecordFileAsync(assetId, stamp is null ? null : JsonSerializer.Serialize(stamp, Json), JsonSerializer.Serialize(snapshot, Json), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Imports the external changes of the given photos (all those with a recorded XMP when null).</summary>
    public async Task<XmpSyncReport> SyncAsync(IReadOnlyList<long>? assetIds, IProgress<int>? progress, CancellationToken ct)
    {
        var ids = assetIds ?? await catalog.XmpSync.ListRecordedAsync(ct).ConfigureAwait(false);
        var libraries = (await catalog.Libraries.ListAsync(ct).ConfigureAwait(false)).ToDictionary(l => l.Id);
        Lookup? lookup = null;
        int read = 0, changed = 0, done = 0;
        var log = new List<XmpSyncLogEntry>();
        foreach (var assetId in ids)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(++done);
            if (await LocateAsync(assetId, libraries, ct).ConfigureAwait(false) is not { } located)
            {
                continue;
            }

            var (target, policy) = located;
            var state = await catalog.XmpSync.GetAsync(assetId, ct).ConfigureAwait(false);
            var previous = state?.FileStampJson is { } stampJson ? JsonSerializer.Deserialize<XmpFileStamp>(stampJson, Json) : null;
            var holder = Metadata.ExifTool.ExifToolMetadataReader.HolderPath(target, policy);
            if (previous is not null && File.Exists(holder) && new FileInfo(holder) is var info
                && previous.SameFileState(previous with { Size = info.Length, Modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) }))
            {
                continue; // nobody touched the file
            }

            var stamp = await reader.StampAsync(target, policy, ct).ConfigureAwait(false);
            if (stamp is null)
            {
                continue;
            }

            var baseline = state?.SnapshotJson is { } snapshotJson ? JsonSerializer.Deserialize<XmpSnapshot>(snapshotJson, Json) ?? XmpSnapshot.Empty : XmpSnapshot.Empty;
            if (previous?.Hash == stamp.Hash)
            {
                await catalog.XmpSync.RecordFileAsync(assetId, JsonSerializer.Serialize(stamp, Json), JsonSerializer.Serialize(baseline, Json), ct).ConfigureAwait(false);
                continue; // same content, only the date changed
            }

            read++;
            var current = await reader.ReadAsync(target, policy, ct).ConfigureAwait(false);
            lookup ??= await Lookup.CreateAsync(catalog, ct).ConfigureAwait(false);
            var entries = await ApplyAsync(assetId, baseline, current, stamp.Modified, lookup, ct).ConfigureAwait(false);
            if (entries.Any(e => e.Winner == XmpSyncWinner.External))
            {
                changed++;
            }

            if (entries.Any(e => e.Winner == XmpSyncWinner.Catalog))
            {
                await catalog.XmpSync.MarkPendingAsync(assetId, null, ct).ConfigureAwait(false);
            }

            log.AddRange(entries);
            await catalog.XmpSync.RecordFileAsync(assetId, JsonSerializer.Serialize(stamp, Json), JsonSerializer.Serialize(current, Json), ct).ConfigureAwait(false);
        }

        if (log.Count > 0)
        {
            await catalog.XmpSync.AddLogAsync(log, ct).ConfigureAwait(false);
        }

        return new XmpSyncReport(read, changed, log);
    }

    private async Task<(MetadataTarget Target, WritePolicy Policy)?> LocateAsync(long assetId, Dictionary<long, Library> libraries, CancellationToken ct)
    {
        var asset = await catalog.Assets.GetAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return null;
        }

        foreach (var location in await catalog.Assets.GetLocationsAsync(assetId, ct).ConfigureAwait(false))
        {
            if (libraries.TryGetValue(location.LibraryId, out var library))
            {
                var path = Path.Combine(library.RootPath, location.RelativePath);
                if (File.Exists(path))
                {
                    return (new MetadataTarget(path, asset.Handler == "raw"), library.XmpPolicy == WritePolicy.CatalogOnly ? WritePolicy.SidecarOnly : library.XmpPolicy);
                }
            }
        }

        return null;
    }

    private async Task<List<XmpSyncLogEntry>> ApplyAsync(long assetId, XmpSnapshot baseline, XmpSnapshot current, DateTimeOffset fileTime, Lookup lookup, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var log = new List<XmpSyncLogEntry>();
        void Log(string field, string? value, XmpSyncWinner winner, string? note = null) => log.Add(new XmpSyncLogEntry(assetId, now, field, value, winner, note));

        // Rating and label: the most recent change wins.
        var ratingChanged = current.Rating != baseline.Rating;
        var labelChanged = !string.Equals(current.Label ?? string.Empty, baseline.Label ?? string.Empty, StringComparison.Ordinal);
        if (ratingChanged || labelChanged)
        {
            var decision = await catalog.Decisions.GetAsync(assetId, ct).ConfigureAwait(false);
            if (decision?.UserUpdatedAt is { } userTime && userTime > fileTime)
            {
                if (ratingChanged)
                {
                    Log("rating", current.Rating?.ToString(System.Globalization.CultureInfo.InvariantCulture), XmpSyncWinner.Catalog, "PhotoFlow change more recent");
                }

                if (labelChanged)
                {
                    Log("label", current.Label, XmpSyncWinner.Catalog, "PhotoFlow change more recent");
                }
            }
            else
            {
                var outcome = decision?.UserOutcome;
                var rating = decision?.Rating;
                var label = decision?.Label;
                if (ratingChanged)
                {
                    (outcome, rating) = current.Rating switch
                    {
                        -1 => (CullingOutcome.Discard, null),
                        > 0 => (decision?.EffectiveOutcome == CullingOutcome.Discard ? CullingOutcome.Kept : outcome, current.Rating),
                        _ => (outcome, (int?)null),
                    };
                    Log("rating", current.Rating?.ToString(System.Globalization.CultureInfo.InvariantCulture), XmpSyncWinner.External);
                }

                if (labelChanged)
                {
                    label = current.Label;
                    Log("label", current.Label, XmpSyncWinner.External);
                }

                await catalog.Decisions.SetUserAsync(assetId, outcome, rating, label, ct).ConfigureAwait(false);
            }
        }

        // Keywords: added → manual tags; removed → automatic tags suppressed, manual tags kept.
        var tags = await catalog.Tags.GetForAssetAsync(assetId, ct).ConfigureAwait(false);
        var before = baseline.FlatKeywords.ToHashSet(StringComparer.Ordinal);
        var after = current.FlatKeywords.ToHashSet(StringComparer.Ordinal);
        foreach (var keyword in current.FlatKeywords.Where(k => !before.Contains(k)).Distinct(StringComparer.Ordinal))
        {
            if (lookup.People.Contains(keyword))
            {
                continue; // people are regions, not tags
            }

            if (lookup.Find(keyword) is { } tag)
            {
                if (tags.FirstOrDefault(t => t.Tag.Id == tag.Id) is not { Assignment.State: AssetTagState.Active })
                {
                    await catalog.Tags.AddManualAsync(assetId, tag.Key, ct).ConfigureAwait(false);
                    Log("keyword", keyword, XmpSyncWinner.External, "added");
                }
            }
            else
            {
                await catalog.Tags.AddManualAsync(assetId, keyword, ct).ConfigureAwait(false);
                if (await catalog.Tags.FindAsync(keyword, ct).ConfigureAwait(false) is { } created)
                {
                    await catalog.Tags.UpdateAsync(created.Id, keyword, reviewed: true, null, blacklisted: false, ct).ConfigureAwait(false);
                    lookup.Add(created with { Italian = keyword });
                }

                Log("keyword", keyword, XmpSyncWinner.External, "added");
            }
        }

        var removedTags = new HashSet<long>();
        foreach (var keyword in baseline.FlatKeywords.Where(k => !after.Contains(k)).Distinct(StringComparer.Ordinal))
        {
            if (lookup.Find(keyword) is not { } tag || tags.FirstOrDefault(t => t.Tag.Id == tag.Id) is not { Assignment.State: AssetTagState.Active } assigned
                || !removedTags.Add(tag.Id))
            {
                continue;
            }

            if (assigned.Assignment.Source == TagSource.Manual)
            {
                Log("keyword", keyword, XmpSyncWinner.Catalog, "manual tag never removed");
            }
            else
            {
                await catalog.Tags.SuppressAsync(assetId, tag.Id, ct).ConfigureAwait(false);
                Log("keyword", keyword, XmpSyncWinner.External, "removed");
            }
        }

        // Description → the user's caption.
        if (!string.Equals(current.Description ?? string.Empty, baseline.Description ?? string.Empty, StringComparison.Ordinal))
        {
            var caption = await catalog.Captions.GetAsync(assetId, ct).ConfigureAwait(false);
            await catalog.Captions.SetManualAsync(assetId, caption?.Language ?? "en", current.Description ?? string.Empty, ct).ConfigureAwait(false);
            Log("description", current.Description, XmpSyncWinner.External);
        }

        // Face regions named by other programs: noted, importable in the people gallery.
        var named = baseline.Regions.Select(r => r.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var region in current.Regions.Where(r => r.Name is { } name && !named.Contains(name) && !lookup.People.Contains(name)))
        {
            Log("region", region.Name, XmpSyncWinner.External, "named by another program");
        }

        return log;
    }

    /// <summary>
    /// Imports in the people gallery the face regions named by other programs (spec §4): the face of the photo that
    /// overlaps a named region (IoU ≥ 0.3) gets that person, created when the name is new. Faces the user assigned are
    /// never changed (I5). Returns the people created and the faces assigned.
    /// </summary>
    public async Task<(int People, int Faces)> ImportNamedRegionsAsync(CancellationToken ct)
    {
        var persons = (await catalog.People.ListPersonsAsync(ct).ConfigureAwait(false)).Where(p => p.Person.Name is not null)
            .GroupBy(p => p.Person.Name!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Person.Id, StringComparer.Ordinal);
        int created = 0, assigned = 0;
        foreach (var assetId in await catalog.XmpSync.ListRecordedAsync(ct).ConfigureAwait(false))
        {
            var state = await catalog.XmpSync.GetAsync(assetId, ct).ConfigureAwait(false);
            if (state?.SnapshotJson is not { } json || JsonSerializer.Deserialize<XmpSnapshot>(json, Json) is not { } snapshot)
            {
                continue;
            }

            var regions = snapshot.Regions.Where(r => r.Name is { Length: > 0 } && (r.Type is null || r.Type == "Face")).ToList();
            if (regions.Count == 0)
            {
                continue;
            }

            var faces = (await catalog.Analysis.GetFacesAsync(assetId, ct).ConfigureAwait(false)).ToList();
            if (faces.Count == 0 && await catalog.Assets.GetTwinAsync(assetId, ct).ConfigureAwait(false) is { } twin)
            {
                faces = [.. await catalog.Analysis.GetFacesAsync(twin, ct).ConfigureAwait(false)];
            }

            foreach (var region in regions)
            {
                var box = new Core.Analysis.BoundingBox((float)(region.CenterX - (region.Width / 2)), (float)(region.CenterY - (region.Height / 2)),
                    (float)(region.CenterX + (region.Width / 2)), (float)(region.CenterY + (region.Height / 2)));
                var face = faces.Where(f => f.AssignmentOrigin != AssignmentOrigin.Manual).Select(f => (Face: f, Overlap: Iou(f.Box, box)))
                    .Where(m => m.Overlap >= 0.3).OrderByDescending(m => m.Overlap).Select(m => m.Face).FirstOrDefault();
                if (face is null)
                {
                    continue;
                }

                if (!persons.TryGetValue(region.Name!, out var personId))
                {
                    personId = await catalog.People.CreatePersonAsync(region.Name, ct).ConfigureAwait(false);
                    persons[region.Name!] = personId;
                    created++;
                }

                if (face.PersonId != personId)
                {
                    await catalog.People.AssignManualAsync([face.Id], personId, ct).ConfigureAwait(false);
                    assigned++;
                }

                faces.Remove(face);
            }
        }

        return (created, assigned);

        static double Iou(Core.Analysis.BoundingBox a, Core.Analysis.BoundingBox b)
        {
            var w = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
            var h = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
            var intersection = w * h;
            var union = ((a.X2 - a.X1) * (a.Y2 - a.Y1)) + ((b.X2 - b.X1) * (b.Y2 - b.Y1)) - intersection;
            return union <= 0 ? 0 : intersection / union;
        }
    }

    /// <summary>Tags by key and labels (English, Italian), and the names of the people, for one sync.</summary>
    private sealed class Lookup
    {
        private readonly Dictionary<string, Tag> _tags = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> People { get; } = new(StringComparer.Ordinal);

        public static async Task<Lookup> CreateAsync(ICatalog catalog, CancellationToken ct)
        {
            var lookup = new Lookup();
            foreach (var tag in await catalog.Tags.ListAsync(ct).ConfigureAwait(false))
            {
                lookup.Add(tag);
            }

            foreach (var person in await catalog.People.ListPersonsAsync(ct).ConfigureAwait(false))
            {
                if (person.Person.Name is { } name)
                {
                    lookup.People.Add(name);
                }
            }

            return lookup;
        }

        public void Add(Tag tag)
        {
            foreach (var label in new[] { tag.Key, tag.English, tag.Italian }.OfType<string>())
            {
                _tags.TryAdd(label, tag);
            }
        }

        public Tag? Find(string keyword) => _tags.GetValueOrDefault(keyword);
    }
}
