// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Data;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Invariants;

/// <summary>I5: manual decisions are never overwritten by automatic processes (also P2: decisions taken during the analysis win).</summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class ManualDecisionsWinTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-i5");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_automatic_proposal_after_the_user_decision_leaves_it_intact()
    {
        await using var catalog = await OpenAsync();
        var asset = await AddAssetAsync(catalog);

        await catalog.Decisions.SetUserAsync(asset, CullingOutcome.Kept, 4, null, Ct);
        await catalog.Decisions.SetAutomaticAsync(asset, CullingOutcome.Discard, ["blur"], "sharpness@1", Ct);

        var decision = await catalog.Decisions.GetAsync(asset, Ct);
        Assert.Equal(CullingOutcome.Kept, decision!.UserOutcome);
        Assert.Equal(4, decision.Rating);
        Assert.Equal(CullingOutcome.Discard, decision.AutomaticOutcome);
        Assert.Equal(CullingOutcome.Kept, decision.EffectiveOutcome);
    }

    [Fact]
    public async Task A_reanalysis_updates_only_the_automatic_proposal()
    {
        await using var catalog = await OpenAsync();
        var asset = await AddAssetAsync(catalog);
        await catalog.Decisions.SetAutomaticAsync(asset, CullingOutcome.ToReview, ["near threshold"], "v1", Ct);
        await catalog.Decisions.SetUserAsync(asset, CullingOutcome.Discard, null, "Red", Ct);

        await catalog.Decisions.SetAutomaticAsync(asset, CullingOutcome.Kept, [], "v2", Ct);

        var decision = await catalog.Decisions.GetAsync(asset, Ct);
        Assert.Equal(CullingOutcome.Discard, decision!.UserOutcome);
        Assert.Equal("Red", decision.Label);
        Assert.Equal("v2", decision.ModelVersion);
        Assert.Equal(CullingOutcome.Discard, decision.EffectiveOutcome);
    }

    private async Task<SqliteCatalog> OpenAsync() =>
        await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);

    private static Task<long> AddAssetAsync(ICatalog catalog) =>
        catalog.Assets.AddAsync(new Asset(0, "q", null, "NEF", "raw", null, null, null, DateSource.Unknown, null, AssetState.Active), Ct);

    public void Dispose() => _temp.Dispose();
}
