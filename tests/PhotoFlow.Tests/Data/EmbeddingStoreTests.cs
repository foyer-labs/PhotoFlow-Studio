// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Data;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Data;

/// <summary>Image embeddings in the catalog (phase 2): per asset and model version, exact round trip.</summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class EmbeddingStoreTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-emb");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Embeddings_round_trip_per_model_version_and_are_replaced()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var a = await catalog.Assets.AddAsync(new Asset(0, "qa", null, "JPG", "jpeg", null, null, null, DateSource.Unknown, null, AssetState.Active), Ct);
        var b = await catalog.Assets.AddAsync(new Asset(0, "qb", null, "JPG", "jpeg", null, null, null, DateSource.Unknown, null, AssetState.Active), Ct);
        var vector = Enumerable.Range(0, 512).Select(i => MathF.Sin(i) / 16).ToArray();

        await catalog.Analysis.SaveEmbeddingAsync(a, "clip@1", vector, Ct);
        await catalog.Analysis.SaveEmbeddingAsync(a, "clip@2", vector.Select(v => -v).ToArray(), Ct);
        await catalog.Analysis.SaveEmbeddingAsync(a, "clip@1", vector.Select(v => v * 2).ToArray(), Ct);

        var v1 = await catalog.Analysis.GetEmbeddingsAsync([a, b], "clip@1", Ct);
        Assert.Equal([a], v1.Keys);
        Assert.Equal(vector.Select(v => v * 2), v1[a]);
        Assert.Equal(vector.Select(v => -v), (await catalog.Analysis.GetEmbeddingsAsync([a], "clip@2", Ct))[a]);
    }

    public void Dispose() => _temp.Dispose();
}
