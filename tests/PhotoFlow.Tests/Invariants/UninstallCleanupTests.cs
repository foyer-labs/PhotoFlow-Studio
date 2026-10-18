// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.App.Services;
using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Invariants;

/// <summary>
/// The uninstall (spec §11, "Dati sul disco", B1): thumbnails, caches and temporary files go; configuration, catalog,
/// models, backups and logs stay; nothing outside PhotoFlow's folders is touched.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class UninstallCleanupTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-uninstall");

    [Fact]
    public void Only_caches_and_temporary_files_go()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        string Write(string folder, string name)
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, name);
            File.WriteAllText(file, "x");
            return file;
        }

        var thumbnail = Write(Path.Combine(paths.ThumbnailsDirectory, "ab"), "abc.jpg");
        var venv = Write(Path.Combine(paths.CacheDirectory, "vlm-env", "Scripts"), "python.exe");
        var temp = Write(paths.TempDirectory, "work.tmp");
        var kept = new[]
        {
            Write(paths.ConfigDirectory, "settings.json"),
            Write(Path.GetDirectoryName(paths.CatalogPath)!, "catalog.pfdb"),
            Write(Path.Combine(paths.ModelsDirectory, "clip"), "model.onnx"),
            Write(paths.BackupsDirectory, "catalog-close.pfdb"),
            Write(paths.LogsDirectory, "photoflow.log"),
            Write(_temp.Sub("library"), "DSC_0001.NEF"),
        };

        UninstallCleanup.Run(paths);

        Assert.False(File.Exists(thumbnail));
        Assert.False(File.Exists(venv));
        Assert.False(File.Exists(temp));
        Assert.All(kept, f => Assert.True(File.Exists(f), f));
        UninstallCleanup.Run(paths); // again: nothing left to do, no error
    }

    public void Dispose() => _temp.Dispose();
}
