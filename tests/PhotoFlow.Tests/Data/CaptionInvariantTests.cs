// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Data;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Data;

/// <summary>
/// I5 for captions (written before the code): an automatic caption is replaced by a newer model's, never a caption
/// written by the user; a caption the user cleared stays cleared; the user's caption keeps no model version.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class CaptionInvariantTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-captions");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_user_caption_is_never_replaced_by_an_analysis()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var a = await catalog.Assets.AddAsync(new Asset(0, "qa", null, "JPG", "jpeg", 10, 10, null, DateSource.Unknown, null, AssetState.Active), Ct);
        var b = await catalog.Assets.AddAsync(new Asset(0, "qb", null, "JPG", "jpeg", 10, 10, null, DateSource.Unknown, null, AssetState.Active), Ct);

        // Automatic captions: a newer model replaces the older one.
        Assert.True(await catalog.Captions.SetAutomaticAsync(a, "en", "A car on a track.", "florence@1", Ct));
        Assert.True(await catalog.Captions.SetAutomaticAsync(a, "en", "A red car on a race track.", "florence@2", Ct));
        var automatic = await catalog.Captions.GetAsync(a, Ct);
        Assert.Equal(("A red car on a race track.", "florence@2", CaptionOrigin.Automatic), (automatic!.Text, automatic.ModelVersion, automatic.Origin));

        // The user writes a caption: a new analysis does not touch it.
        await catalog.Captions.SetManualAsync(a, "en", "Monza 2026, start of the race.", Ct);
        Assert.False(await catalog.Captions.SetAutomaticAsync(a, "en", "A car.", "florence@3", Ct));
        var manual = await catalog.Captions.GetAsync(a, Ct);
        Assert.Equal(("Monza 2026, start of the race.", CaptionOrigin.Manual, null), (manual!.Text, manual.Origin, manual.ModelVersion));

        // The user clears the caption: it stays empty.
        await catalog.Captions.SetAutomaticAsync(b, "en", "A dog.", "florence@1", Ct);
        await catalog.Captions.SetManualAsync(b, "en", string.Empty, Ct);
        Assert.False(await catalog.Captions.SetAutomaticAsync(b, "en", "A dog on grass.", "florence@2", Ct));
        Assert.Equal((string.Empty, CaptionOrigin.Manual), (await catalog.Captions.GetAsync(b, Ct)) is { } c ? (c.Text, c.Origin) : default);

        // Two languages (phase 5b): the Italian caption of level D comes first, the English one stays; the user's
        // Italian caption is protected while the English one still follows the model.
        Assert.True(await catalog.Captions.SetAutomaticAsync(b, "it", "Un cane sul prato.", "qwen2.5-vl-7b@1", Ct));
        Assert.Equal("Un cane sul prato.", (await catalog.Captions.GetAsync(b, Ct))!.Text);
        var c2 = await catalog.Assets.AddAsync(new Asset(0, "qc", null, "JPG", "jpeg", 10, 10, null, DateSource.Unknown, null, AssetState.Active), Ct);
        await catalog.Captions.SetAutomaticAsync(c2, "en", "A boat on a lake.", "florence@1", Ct);
        await catalog.Captions.SetManualAsync(c2, "it", "La barca di Sara.", Ct);
        Assert.False(await catalog.Captions.SetAutomaticAsync(c2, "it", "Una barca sul lago.", "qwen2.5-vl-7b@2", Ct));
        Assert.True(await catalog.Captions.SetAutomaticAsync(c2, "en", "A small boat on a lake.", "florence@2", Ct));
        Assert.Equal([("it", "La barca di Sara.", CaptionOrigin.Manual), ("en", "A small boat on a lake.", CaptionOrigin.Automatic)],
            (await catalog.Captions.GetAllAsync(c2, Ct)).Select(x => (x.Language, x.Text, x.Origin)));

        // Listing for the search and the export; the words of the captions are searchable.
        Assert.Equal(2, (await catalog.Captions.GetForAssetsAsync([a, b], Ct)).Count);
        Assert.Equal("it", (await catalog.Captions.GetForAssetsAsync([c2], Ct))[c2].Language);
        var hits = await catalog.Search.SearchAsync(PhotoFlow.Core.Search.SearchQuery.Empty with { Text = "Monza" }, Ct);
        Assert.Equal([a], hits.Hits.Select(h => h.AssetId));
        Assert.Empty((await catalog.Search.SearchAsync(PhotoFlow.Core.Search.SearchQuery.Empty with { Text = "dog" }, Ct)).Hits);
    }

    public void Dispose() => _temp.Dispose();
}
