// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Search;
using PhotoFlow.Data;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Data;

/// <summary>
/// The facets of Consultazione (spec §12bis): groups of tags (OR inside, AND between), the count of each tag over the
/// whole result (what an AND keeps), the "+N" of an OR (photos added, not already in the result), people counted on
/// the result. Suppressed and blacklisted tags are not counted; the RAW of a pair is not a photo of its own.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class SearchFacetTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-facets");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Groups_and_counts_follow_the_selection()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), _temp.Sub("b")), Ct);
        string[][] photos = [["car", "track"], ["car"], ["moto", "track"], ["moto"], ["boat", "sea"], ["car", "moto"]];
        var ids = new List<long>();
        for (var i = 0; i < photos.Length; i++)
        {
            var id = await catalog.Assets.AddAsync(new Asset(0, $"q{i}", null, "JPG", "jpeg", 10, 10, DateTimeOffset.UnixEpoch.AddDays(i), DateSource.Exif, null, AssetState.Active), Ct);
            await catalog.Tags.ReplaceAutomaticAsync(id, TagSource.RamPlus, "ram@1", photos[i].Select(k => (k, (double?)0.9)).ToList(), Ct);
            ids.Add(id);
        }

        async Task<long> Tag(string key) => (await catalog.Tags.FindAsync(key, Ct))!.Id;
        var (car, moto, track, boat, sea) = (await Tag("car"), await Tag("moto"), await Tag("track"), await Tag("boat"), await Tag("sea"));
        await catalog.Tags.SuppressAsync(ids[0], track, Ct); // removed by the user on photo 0
        var seaTag = (await catalog.Tags.FindAsync("sea", Ct))!;
        await catalog.Tags.UpdateAsync(sea, seaTag.Italian, seaTag.Reviewed, seaTag.Category, blacklisted: true, Ct);

        SearchQuery Groups(params long[][] groups) => SearchQuery.Empty with { TagGroups = groups.Select(g => (IReadOnlyList<long>)g).ToList(), Limit = int.MaxValue };
        async Task<long[]> Result(SearchQuery query) => [.. (await catalog.Search.SearchAsync(query, Ct)).Hits.Select(h => h.AssetId).Order()];

        // (car OR moto) → 0, 1, 2, 3, 5; AND track → only 2 (track was removed from 0).
        Assert.Equal([ids[0], ids[1], ids[2], ids[3], ids[5]], await Result(Groups([car, moto])));
        Assert.Equal([ids[2]], await Result(Groups([car, moto], [track])));

        // AND counts over the result of (car): what each tag would keep.
        var and = await catalog.Search.CountTagsAsync(Groups([car]), null, Ct);
        Assert.Equal(3, and[car]);
        Assert.Equal(1, and[moto]);
        Assert.False(and.ContainsKey(track)); // suppressed on the only car photo that had it
        Assert.False(and.ContainsKey(boat));

        // OR "+N": photos a tag adds to (car) when put in the same group.
        var or = await catalog.Search.CountTagsAsync(SearchQuery.Empty with { Limit = int.MaxValue }, Groups([car]), Ct);
        Assert.Equal(2, or[moto]); // 2 and 3; 5 is already there
        Assert.Equal(1, or[track]);
        Assert.Equal(1, or[boat]);
        Assert.False(or.ContainsKey(car));
        Assert.False(or.ContainsKey(sea)); // blacklisted
    }

    [Fact]
    public async Task People_are_counted_on_the_result()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), _temp.Sub("b")), Ct);
        var keys = new FingerprintCipher(RandomNumberGenerator.GetBytes(32));
        var anna = await catalog.People.CreatePersonAsync("Anna", Ct);
        var bruno = await catalog.People.CreatePersonAsync("Bruno", Ct);
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var id = await catalog.Assets.AddAsync(new Asset(0, $"q{i}", null, "JPG", "jpeg", 10, 10, null, DateSource.Unknown, null, AssetState.Active), Ct);
            await catalog.Tags.ReplaceAutomaticAsync(id, TagSource.RamPlus, "ram@1", [(i < 2 ? "car" : "boat", 0.9)], Ct);
            var faces = Enumerable.Range(0, i + 1).Select(n => new Face(0, id, new BoundingBox(0.1f * n, 0.1f, 0.1f * n + 0.05f, 0.2f), null, 0.9, EyeState.Open, EyeState.Open,
                keys.Protect([1f, n, 0f]), null, null, null, "det@1", [], "arc@1")).ToList();
            await catalog.Analysis.ReplaceAutomaticFacesAsync(id, faces, Ct);
            var saved = await catalog.Analysis.GetFacesAsync(id, Ct);
            await catalog.People.AssignManualAsync([saved[0].Id], anna, Ct);
            if (saved.Count > 1)
            {
                await catalog.People.AssignManualAsync([saved[1].Id], bruno, Ct);
            }

            ids.Add(id);
        }

        var car = (await catalog.Tags.FindAsync("car", Ct))!.Id;
        var people = await catalog.Search.CountPersonsAsync(SearchQuery.Empty with { TagGroups = [[car]] }, Ct);
        Assert.Equal((2, 1), (people[anna], people[bruno])); // photos 0, 1 (Bruno only on 1)
        Assert.Equal((3, 2), await catalog.Search.CountPersonsAsync(SearchQuery.Empty, Ct) is var all ? (all[anna], all[bruno]) : default);
    }

    public void Dispose() => _temp.Dispose();
}
