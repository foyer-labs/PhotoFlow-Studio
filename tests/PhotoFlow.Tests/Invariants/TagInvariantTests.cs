// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Data;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Invariants;

/// <summary>
/// I5 for tags (spec §8 "Provenienza", acceptance of phase 3 "Rianalisi: tag manuali e soppressi preservati"): a manual
/// tag is never removed by an analysis; an automatic tag removed by the user is suppressed and no analysis adds it
/// again, whatever the model or the confidence; each source replaces only its own tags.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class TagInvariantTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-tag-invariants");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reanalysis_keeps_manual_and_suppressed_tags()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var asset = await catalog.Assets.AddAsync(new Asset(0, "q", "f", "JPG", "jpeg", 10, 10, null, DateSource.Unknown, null, AssetState.Active), Ct);
        var tags = catalog.Tags;

        await tags.ReplaceAutomaticAsync(asset, TagSource.RamPlus, "ram@1", [("beach", 0.9), ("sunset", 0.8), ("person", 0.7)], Ct);
        await tags.ReplaceAutomaticAsync(asset, TagSource.ClipZeroShot, "clip@1", [("custom:lake-como", 0.3)], Ct);
        await tags.AddManualAsync(asset, "sunset", Ct);       // the user confirms a detected tag: now manual
        await tags.AddManualAsync(asset, "holiday", Ct);      // and adds one of their own
        var person = (await tags.FindAsync("person", Ct))!.Id;
        await tags.SuppressAsync(asset, person, Ct);          // and removes a wrong one

        // A new analysis with another model and other confidences finds none of the old tags but "person" again.
        await tags.ReplaceAutomaticAsync(asset, TagSource.RamPlus, "ram@2", [("person", 0.99), ("sea", 0.8)], Ct);

        var state = (await tags.GetForAssetAsync(asset, Ct)).ToDictionary(t => t.Tag.Key, t => t.Assignment);
        Assert.Equal(TagSource.Manual, state["sunset"].Source);           // manual: kept
        Assert.Equal(AssetTagState.Active, state["sunset"].State);
        Assert.Equal(TagSource.Manual, state["holiday"].Source);
        Assert.Equal(AssetTagState.Suppressed, state["person"].State);    // suppressed: not added again
        Assert.False(state.ContainsKey("beach"));                         // automatic and no longer found: gone
        Assert.Equal(AssetTagState.Active, state["sea"].State);
        Assert.Equal("ram@2", state["sea"].ModelVersion);
        Assert.Equal(AssetTagState.Active, state["custom:lake-como"].State); // another source: untouched

        // Nothing automatic brings a suppressed tag back; only the user does.
        await tags.ReplaceAutomaticAsync(asset, TagSource.ClipZeroShot, "clip@2", [("person", 0.9)], Ct);
        Assert.Equal(AssetTagState.Suppressed, (await tags.GetForAssetAsync(asset, Ct)).Single(t => t.Tag.Key == "person").Assignment.State);
        await tags.AddManualAsync(asset, "person", Ct);
        var person2 = (await tags.GetForAssetAsync(asset, Ct)).Single(t => t.Tag.Key == "person").Assignment;
        Assert.Equal((TagSource.Manual, AssetTagState.Active), (person2.Source, person2.State));

        // A manual tag removed by the user is suppressed too, so a later detection does not bring it back.
        await tags.SuppressAsync(asset, (await tags.FindAsync("sunset", Ct))!.Id, Ct);
        await tags.ReplaceAutomaticAsync(asset, TagSource.RamPlus, "ram@3", [("sunset", 0.95)], Ct);
        Assert.Equal(AssetTagState.Suppressed, (await tags.GetForAssetAsync(asset, Ct)).Single(t => t.Tag.Key == "sunset").Assignment.State);
    }

    public void Dispose() => _temp.Dispose();
}
