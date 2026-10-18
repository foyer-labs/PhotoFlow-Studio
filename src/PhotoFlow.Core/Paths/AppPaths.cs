// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Core.Paths;

/// <summary>Single point of path resolution: no path is written anywhere else (enables portable mode, D19).</summary>
public interface IAppPaths
{
    /// <summary>Settings, profiles and vocabulary (JSON).</summary>
    string ConfigDirectory { get; }

    string ProfilesDirectory { get; }

    /// <summary>Root of the user data folder.</summary>
    string DataDirectory { get; }

    /// <summary>Catalog file (<c>.pfdb</c>, P19); local by default, configurable (D5).</summary>
    string CatalogPath { get; }

    string BackupsDirectory { get; }

    /// <summary>Downloaded models; configurable (spec §11).</summary>
    string ModelsDirectory { get; }

    /// <summary>GPU runtime libraries (CUDA/cuDNN) and their downloaded archives (decision F1-2).</summary>
    string GpuRuntimeDirectory { get; }

    /// <summary>Always local, rebuildable.</summary>
    string CacheDirectory { get; }

    string ThumbnailsDirectory { get; }

    string LogsDirectory { get; }

    string TempDirectory { get; }
}

/// <summary>Overrides for the configurable locations; null means default.</summary>
public sealed record AppPathOverrides(string? CatalogPath = null, string? ModelsDirectory = null);

/// <summary>Default layout under the per-user local application data folder.</summary>
public sealed class DefaultAppPaths : IAppPaths
{
    public const string ApplicationFolderName = "PhotoFlow";
    public const string CatalogFileName = "catalog.pfdb";

    public DefaultAppPaths(AppPathOverrides? overrides = null)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationFolderName), overrides)
    {
    }

    /// <summary>Layout rooted at <paramref name="dataRoot"/> (tests, future portable mode).</summary>
    public DefaultAppPaths(string dataRoot, AppPathOverrides? overrides = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException("The data root must be an absolute path.", nameof(dataRoot));
        }

        DataDirectory = dataRoot;
        ConfigDirectory = Path.Combine(dataRoot, "config");
        ProfilesDirectory = Path.Combine(ConfigDirectory, "profiles");
        CatalogPath = overrides?.CatalogPath ?? Path.Combine(dataRoot, "catalog", CatalogFileName);
        BackupsDirectory = Path.Combine(dataRoot, "backups");
        ModelsDirectory = overrides?.ModelsDirectory ?? Path.Combine(dataRoot, "models");
        GpuRuntimeDirectory = Path.Combine(dataRoot, "gpu-runtime");
        CacheDirectory = Path.Combine(dataRoot, "cache");
        ThumbnailsDirectory = Path.Combine(CacheDirectory, "thumbnails");
        LogsDirectory = Path.Combine(dataRoot, "logs");
        TempDirectory = Path.Combine(dataRoot, "temp");
    }

    public string ConfigDirectory { get; }

    public string ProfilesDirectory { get; }

    public string DataDirectory { get; }

    public string CatalogPath { get; }

    public string BackupsDirectory { get; }

    public string ModelsDirectory { get; }

    public string GpuRuntimeDirectory { get; }

    public string CacheDirectory { get; }

    public string ThumbnailsDirectory { get; }

    public string LogsDirectory { get; }

    public string TempDirectory { get; }
}
