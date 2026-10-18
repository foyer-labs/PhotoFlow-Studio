// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.Core.Paths;
using PhotoFlow.Data;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>Cleanup rules of PhotoFlow's internal files (decision B1): only inside the internal folders.</summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class MaintenanceTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-maint");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Backups_rotate_cache_and_temp_are_trimmed_user_files_stay()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        var service = new MaintenanceService(paths, new InternalFileCleaner(paths));
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(paths.CatalogPath, paths.BackupsDirectory), Ct);

        // Twelve old backups, the daily one is due.
        Directory.CreateDirectory(paths.BackupsDirectory);
        for (var i = 0; i < 12; i++)
        {
            var old = Path.Combine(paths.BackupsDirectory, $"catalog-old-{i:00}.pfdb");
            File.WriteAllText(old, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30 + i));
        }

        // Thumbnails over the limit, old and new temp files, a user photo next to the profile.
        var thumbs = Directory.CreateDirectory(Path.Combine(paths.ThumbnailsDirectory, "ab")).FullName;
        for (var i = 0; i < 10; i++)
        {
            var thumb = Path.Combine(thumbs, $"{i}.jpg");
            File.WriteAllBytes(thumb, new byte[1000]);
            File.SetLastWriteTimeUtc(thumb, DateTime.UtcNow.AddMinutes(-100 + i));
        }

        Directory.CreateDirectory(paths.TempDirectory);
        var oldTemp = Path.Combine(paths.TempDirectory, "old.tmp");
        File.WriteAllText(oldTemp, "x");
        File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddDays(-3));
        var newTemp = Path.Combine(paths.TempDirectory, "new.tmp");
        File.WriteAllText(newTemp, "x");
        var photo = Path.Combine(_temp.Sub("library"), "DSC_0001.NEF");
        File.WriteAllText(photo, "raw");

        var report = await service.RunAtStartupAsync(catalog, null, false, Ct);
        Assert.NotNull(report.Backup);
        Assert.True(File.Exists(report.Backup));
        Assert.Equal(MaintenanceService.KeepBackups, Directory.GetFiles(paths.BackupsDirectory).Length);
        Assert.Contains(report.Backup, Directory.GetFiles(paths.BackupsDirectory));

        Assert.Equal(0, report.ThumbnailsRemoved); // under the 4 GB limit
        Assert.Equal(6, service.TrimThumbnails(5000)); // down to 4000 bytes: the six oldest go
        Assert.True(File.Exists(Path.Combine(thumbs, "9.jpg")));
        Assert.False(File.Exists(Path.Combine(thumbs, "0.jpg")));

        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(newTemp));
        Assert.True(File.Exists(photo));

        var close = await service.BackupAtCloseAsync(catalog, Ct);
        Assert.True(File.Exists(close));
        Assert.Equal(MaintenanceService.KeepBackups, Directory.GetFiles(paths.BackupsDirectory).Length);
    }

    public void Dispose() => _temp.Dispose();
}
