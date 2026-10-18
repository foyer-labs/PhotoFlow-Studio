// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Faces.Recognition;

/// <param name="Transferred">New faces that took the person of an old face.</param>
/// <param name="KeptManual">Faces decided by the user that the new model does not find: kept as they are.</param>
/// <param name="Unmatched">Automatic old faces the new model does not find: removed.</param>
/// <param name="New">New faces with no old face: recognized and grouped afterwards.</param>
public sealed record FaceTransferResult(int Transferred, int KeptManual, int Unmatched, int New);

/// <summary>
/// A new face model on a photo (spec §5 "Nuovo modello volti"): the new faces take over the old ones they overlap
/// (IoU ≥ 0.5, the best pairs first), with their person and the origin of the assignment (the user's stay the user's,
/// I5). Faces decided by the user that the new model does not find are kept; people are never deleted; covers follow
/// the faces and the prototypes of the people involved are made again (of the new model).
/// </summary>
public static class FaceTransfer
{
    public const float MinOverlap = 0.5f;

    /// <summary>For each new face, the old face it takes over (or null).</summary>
    public static long?[] Match(IReadOnlyList<Face> old, IReadOnlyList<Face> fresh)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(fresh);
        var pairs = from o in Enumerable.Range(0, old.Count)
                    from n in Enumerable.Range(0, fresh.Count)
                    let overlap = old[o].Box.IntersectionOverUnion(fresh[n].Box)
                    where overlap >= MinOverlap
                    orderby overlap descending, o, n
                    select (o, n);
        var result = new long?[fresh.Count];
        var used = new HashSet<int>();
        foreach (var (o, n) in pairs)
        {
            if (result[n] is null && used.Add(o))
            {
                result[n] = old[o].Id;
            }
        }

        return result;
    }

    public static async Task<FaceTransferResult> ApplyAsync(ICatalog catalog, long assetId, IReadOnlyList<Face> fresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fresh);
        var old = await catalog.Analysis.GetFacesAsync(assetId, ct).ConfigureAwait(false);
        var byId = old.ToDictionary(f => f.Id);
        var matches = Match(old, fresh);
        var faces = new List<Face>(fresh.Count);
        var replaces = new Dictionary<int, long>();
        var touched = new HashSet<long>();
        int transferred = 0, added = 0;
        for (var i = 0; i < fresh.Count; i++)
        {
            var face = fresh[i] with { Id = 0, AssetId = assetId, PersonId = null, AssignmentOrigin = null, Similarity = null };
            if (matches[i] is { } oldId)
            {
                var previous = byId[oldId];
                replaces[i] = oldId;
                face = face with { PersonId = previous.PersonId, AssignmentOrigin = previous.AssignmentOrigin, Similarity = previous.Similarity };
                if (previous.PersonId is { } person)
                {
                    touched.Add(person);
                    transferred++;
                }
            }
            else
            {
                added++;
            }

            faces.Add(face);
        }

        var matched = matches.OfType<long>().ToHashSet();
        var keep = old.Where(f => !matched.Contains(f.Id) && f.AssignmentOrigin == AssignmentOrigin.Manual).Select(f => f.Id).ToList();
        var unmatched = old.Count(f => !matched.Contains(f.Id) && f.AssignmentOrigin != AssignmentOrigin.Manual);
        touched.UnionWith(old.Where(f => !matched.Contains(f.Id)).Select(f => f.PersonId).OfType<long>());
        await catalog.Analysis.ReplaceFacesAsync(assetId, faces, keep, replaces, ct).ConfigureAwait(false);
        foreach (var person in touched)
        {
            await PeopleEngine.RefreshPrototypesAsync(catalog, person, ct).ConfigureAwait(false);
        }

        return new FaceTransferResult(transferred, keep.Count, unmatched, added);
    }
}
