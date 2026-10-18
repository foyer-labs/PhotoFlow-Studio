// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Paths;
using PhotoFlow.Data;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// Where the catalog lives (spec §9): asked at the first start before the catalog exists (a PhotoFlow catalog already
/// in the chosen folder is used as it is); moved from the settings at the next start, copying the closed catalog and
/// leaving the original where it was; another existing catalog opened after a check. A change that cannot be applied
/// at startup (catalog busy, target already there, not a catalog) leaves the catalog of before in use, with the reason.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class CatalogLocationTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-catalog-location");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_start_asks_the_folder_before_the_catalog_exists()
    {
        var defaults = new DefaultAppPaths(_temp.Sub("profile"));
        var location = new CatalogLocationService(defaults);
        Assert.True(location.NeedsFirstChoice);
        Assert.Equal(Path.GetDirectoryName(defaults.CatalogPath), location.DefaultFolder);

        // The page: another folder, then continue; the application starts with the catalog there.
        var started = 0;
        var page = new CatalogLocationViewModel(location, () =>
        {
            started++;
            return Task.CompletedTask;
        });
        var chosen = _temp.Sub("disk2");
        page.UseFolder(chosen);
        Assert.False(page.IsDefault);
        await page.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, started);
        Assert.False(location.NeedsFirstChoice);
        Assert.Equal(Path.Combine(chosen, DefaultAppPaths.CatalogFileName), location.Resolve().CatalogPath);
        Assert.Equal(defaults.BackupsDirectory, location.Resolve().BackupsDirectory); // backups stay local
        Assert.Empty(Directory.GetFiles(chosen)); // the write probe leaves nothing behind

        // A folder with a PhotoFlow catalog in it: that catalog is used.
        var shared = _temp.Sub("nas");
        await CreateCatalogAsync(Path.Combine(shared, DefaultAppPaths.CatalogFileName), 2);
        var other = new CatalogLocationService(new DefaultAppPaths(_temp.Sub("profile2")));
        Assert.True(await other.ChooseAsync(shared, Ct));

        // A file of that name that is not a catalog: refused, nothing chosen.
        var wrong = _temp.Sub("wrong");
        await File.WriteAllTextAsync(Path.Combine(wrong, DefaultAppPaths.CatalogFileName), "not a catalog", Ct);
        var third = new CatalogLocationService(new DefaultAppPaths(_temp.Sub("profile3")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => third.ChooseAsync(wrong, Ct));
        Assert.True(third.NeedsFirstChoice);

        // An existing installation (catalog in the default place) is not asked again.
        var existing = new DefaultAppPaths(_temp.Sub("profile4"));
        await CreateCatalogAsync(existing.CatalogPath, 1);
        Assert.False(new CatalogLocationService(existing).NeedsFirstChoice);
    }

    [Fact]
    public async Task A_move_copies_the_closed_catalog_and_keeps_the_original()
    {
        var defaults = new DefaultAppPaths(_temp.Sub("profile"));
        var location = new CatalogLocationService(defaults);
        await location.ChooseAsync(location.DefaultFolder, Ct);
        await CreateCatalogAsync(location.CatalogPath, 3);
        var original = location.CatalogPath;

        // Refused: the same folder, or a folder that already holds a catalog.
        Assert.Throws<InvalidOperationException>(() => location.RequestMove(location.DefaultFolder));
        var occupied = _temp.Sub("occupied");
        await CreateCatalogAsync(Path.Combine(occupied, DefaultAppPaths.CatalogFileName), 1);
        Assert.Throws<InvalidOperationException>(() => location.RequestMove(occupied));
        Assert.Null(location.Pending);

        // Requested, then withdrawn, then requested again and applied at the "next start".
        var target = _temp.Sub("nas");
        location.RequestMove(target);
        Assert.Equal(new PendingCatalogChange(true, Path.Combine(target, DefaultAppPaths.CatalogFileName)), location.Pending);
        location.CancelPending();
        Assert.Null(location.Pending);
        location.RequestMove(target);
        await location.ApplyPendingAsync(Ct);

        Assert.Null(location.Pending);
        Assert.Equal(Path.Combine(target, DefaultAppPaths.CatalogFileName), location.CatalogPath);
        Assert.StartsWith("moved|", location.LastResult, StringComparison.Ordinal);
        Assert.True(File.Exists(original)); // never deleted
        Assert.Equal(3, await CountAssetsAsync(location.CatalogPath));
        location.ClearLastResult();
        Assert.Null(location.LastResult);
    }

    [Fact]
    public async Task A_change_that_cannot_be_applied_keeps_the_catalog_of_before()
    {
        var location = new CatalogLocationService(new DefaultAppPaths(_temp.Sub("profile")));
        await location.ChooseAsync(location.DefaultFolder, Ct);
        await CreateCatalogAsync(location.CatalogPath, 2);
        var before = location.CatalogPath;

        // Busy: the catalog is open elsewhere when the move should happen.
        location.RequestMove(_temp.Sub("target"));
        await using (await SqliteCatalog.OpenAsync(new CatalogOpenOptions(before, _temp.Sub("b")), Ct))
        {
            await location.ApplyPendingAsync(Ct);
        }

        Assert.Equal(before, location.CatalogPath);
        Assert.StartsWith("failed|", location.LastResult, StringComparison.Ordinal);
        Assert.Null(location.Pending);

        // Another catalog: refused when it is not one, used after the check otherwise.
        var notCatalog = Path.Combine(_temp.Sub("x"), "notes.pfdb");
        await File.WriteAllTextAsync(notCatalog, "text", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => location.RequestOpenAsync(notCatalog, Ct));
        var other = Path.Combine(_temp.Sub("other"), DefaultAppPaths.CatalogFileName);
        await CreateCatalogAsync(other, 5);
        await location.RequestOpenAsync(other, Ct);
        Assert.Equal(new PendingCatalogChange(false, other), location.Pending);
        await location.ApplyPendingAsync(Ct);
        Assert.Equal(other, location.CatalogPath);
        Assert.StartsWith("opened|", location.LastResult, StringComparison.Ordinal);
        Assert.Equal(2, await CountAssetsAsync(before)); // the previous one untouched
    }

    public void Dispose() => _temp.Dispose();

    private async Task CreateCatalogAsync(string path, int assets)
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(path, _temp.Sub("backups-" + Guid.NewGuid().ToString("N"))), Ct);
        for (var i = 0; i < assets; i++)
        {
            await catalog.Assets.AddAsync(new Asset(0, $"q{i}", null, "JPG", "jpeg", 10, 10, null, DateSource.Unknown, null, AssetState.Active), Ct);
        }
    }

    private async Task<int> CountAssetsAsync(string path)
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(path, _temp.Sub("backups-" + Guid.NewGuid().ToString("N"))), Ct);
        var count = 0;
        for (long id = 1; id <= 10; id++)
        {
            if (await catalog.Assets.GetAsync(id, Ct) is not null)
            {
                count++;
            }
        }

        return count;
    }
}
