// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Invariants;

/// <summary>
/// I4 after decision B1: the only deletions allowed are PhotoFlow's own files inside its internal folders of
/// <see cref="IAppPaths"/>; anything else (user files, look-alike paths, paths escaping through "..") is refused.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class InternalCleanupTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-cleanup");

    [Fact]
    public void Files_inside_the_internal_folders_can_be_deleted()
    {
        var paths = new DefaultAppPaths(_temp.Path);
        var cleaner = new InternalFileCleaner(paths);
        foreach (var folder in new[] { paths.BackupsDirectory, paths.CacheDirectory, paths.ThumbnailsDirectory, paths.TempDirectory, paths.LogsDirectory, paths.ModelsDirectory, paths.GpuRuntimeDirectory })
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "old.bin");
            File.WriteAllText(file, "x");
            Assert.True(cleaner.IsInternal(file), file);
            cleaner.DeleteInternalFile(file);
            Assert.False(File.Exists(file));
        }
    }

    [Fact]
    public void Anything_outside_the_internal_folders_is_refused()
    {
        var paths = new DefaultAppPaths(_temp.Path);
        var cleaner = new InternalFileCleaner(paths);
        Directory.CreateDirectory(paths.CacheDirectory);
        var photo = Path.Combine(_temp.Sub("library"), "DSC_0001.NEF");
        File.WriteAllText(photo, "raw");
        var lookAlike = Path.Combine(paths.CacheDirectory + "2", "x.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(lookAlike)!);
        File.WriteAllText(lookAlike, "x");
        var escaping = Path.Combine(paths.CacheDirectory, "..", "..", "library", "DSC_0001.NEF");

        foreach (var path in new[] { photo, lookAlike, escaping, paths.CatalogPath, paths.CacheDirectory, Path.Combine(paths.ConfigDirectory, "settings.json") })
        {
            Assert.False(cleaner.IsInternal(path), path);
            Assert.Throws<InvalidOperationException>(() => cleaner.DeleteInternalFile(path));
        }

        Assert.True(File.Exists(photo));
        Assert.True(File.Exists(lookAlike));
    }

    [Fact]
    public void A_link_inside_an_internal_folder_that_points_elsewhere_is_refused()
    {
        var paths = new DefaultAppPaths(_temp.Path);
        var cleaner = new InternalFileCleaner(paths);
        Directory.CreateDirectory(paths.CacheDirectory);
        var outside = _temp.Sub("photos");
        File.WriteAllText(Path.Combine(outside, "a.jpg"), "jpeg");
        var link = Path.Combine(paths.CacheDirectory, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links cannot be created here (Windows without developer mode).");
        }

        var target = Path.Combine(link, "a.jpg");
        Assert.False(cleaner.IsInternal(target));
        Assert.Throws<InvalidOperationException>(() => cleaner.DeleteInternalFile(target));
        Assert.True(File.Exists(Path.Combine(outside, "a.jpg")));
    }

    [Fact]
    public void An_internal_folder_is_deleted_whole_and_nothing_else()
    {
        var paths = new DefaultAppPaths(_temp.Path);
        var cleaner = new InternalFileCleaner(paths);
        var work = Path.Combine(paths.TempDirectory, "vlm-work");
        Directory.CreateDirectory(Path.Combine(work, "recipe", "cache"));
        File.WriteAllText(Path.Combine(work, "recipe", "cache", "model.onnx"), "x");
        var readOnly = Path.Combine(work, "pack.idx");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        cleaner.DeleteInternalDirectory(work);
        Assert.False(Directory.Exists(work));
        Assert.True(Directory.Exists(paths.TempDirectory));
        cleaner.DeleteInternalDirectory(work); // already gone: fine

        // The internal roots themselves, the profile and user folders are refused.
        var library = _temp.Sub("library");
        File.WriteAllText(Path.Combine(library, "DSC_0001.NEF"), "raw");
        foreach (var path in new[] { paths.TempDirectory, paths.CacheDirectory, library, Path.Combine(paths.CacheDirectory, "..", "..", "library") })
        {
            Assert.Throws<InvalidOperationException>(() => cleaner.DeleteInternalDirectory(path));
        }

        Assert.True(File.Exists(Path.Combine(library, "DSC_0001.NEF")));
    }

    [Fact]
    public void A_folder_holding_a_link_is_not_deleted_at_all()
    {
        var paths = new DefaultAppPaths(_temp.Path);
        var cleaner = new InternalFileCleaner(paths);
        var env = Path.Combine(paths.CacheDirectory, "vlm-env");
        Directory.CreateDirectory(env);
        File.WriteAllText(Path.Combine(env, "pyvenv.cfg"), "x");
        var outside = _temp.Sub("photos");
        File.WriteAllText(Path.Combine(outside, "a.jpg"), "jpeg");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(env, "linked"), outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links cannot be created here (Windows without developer mode).");
        }

        Assert.Throws<InvalidOperationException>(() => cleaner.DeleteInternalDirectory(env));
        Assert.True(File.Exists(Path.Combine(env, "pyvenv.cfg")));
        Assert.True(File.Exists(Path.Combine(outside, "a.jpg")));
    }

    public void Dispose() => _temp.Dispose();
}
