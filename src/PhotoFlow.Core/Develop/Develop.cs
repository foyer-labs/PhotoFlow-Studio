// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace PhotoFlow.Core.Develop;

/// <summary>A noise-reduction method offered by the engine (classic or neural, the latter downloaded at first use).</summary>
public sealed record DevelopAlgorithm(string Key, string Label, string Summary, string Description, bool Neural, bool Available, int SizeMb, string License);

/// <summary>What the engine offers: methods, presets, GPU, default settings.</summary>
public sealed record DevelopEngineInfo(
    string Version,
    IReadOnlyList<DevelopAlgorithm> Algorithms,
    IReadOnlyList<(string Name, string Description)> Presets,
    bool GpuAvailable,
    string GpuText,
    JsonObject Defaults);

/// <summary>A photo opened by the engine: its facts and the settings to start from.</summary>
public sealed record DevelopPhoto(
    string Path,
    int Width,
    int Height,
    bool IsRaw,
    string? Camera,
    double? Iso,
    string NoiseLevel,
    string NoiseDetail,
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<string, (double Temperature, double Tint)> WhiteBalanceEquivalents,
    JsonObject Settings,
    bool HasSidecarSettings,
    string SuggestedPreset);

/// <summary>A preview: the "before" (development only) and, when asked, the "after" (with the noise reduction).</summary>
public sealed record DevelopRender(
    string BeforeFile,
    string? AfterFile,
    string? DifferenceFile,
    int Width,
    int Height,
    (int X, int Y, int Width, int Height) Region,
    int? Reduction);

/// <summary>
/// A photo to export with its settings (taken when it joins the queue) and extra name placeholders. <paramref name="Auto"/>:
/// never developed, the engine applies the preset suggested by the measured noise (decision S8).
/// </summary>
public sealed record DevelopExportItem(long AssetId, string Path, JsonObject Settings, IReadOnlyDictionary<string, string> NameValues, bool Auto = false);

/// <summary>Output of an export: <c>ok</c>, <c>saltata</c> (skipped) or <c>errore</c>.</summary>
public sealed record DevelopExportResult(string Path, string Outcome, string? Output, IReadOnlyList<string> Notes);

/// <summary>Progress of a long engine operation (0..1 and a message for the user).</summary>
public sealed record DevelopProgress(double Fraction, string Message);

/// <summary>
/// The development and noise-reduction engine (decision S1): Denoiser in a separate process. Every call is
/// asynchronous and cancellable; settings travel as the JSON of Denoiser's <c>Settings</c>.
/// </summary>
public interface IDevelopEngine
{
    Task<DevelopEngineInfo> HelloAsync(CancellationToken ct);

    /// <summary>Opens a photo; <paramref name="baseSettings"/> are used when the photo has no settings of its own.</summary>
    Task<DevelopPhoto> OpenAsync(string path, JsonObject? baseSettings, CancellationToken ct);

    Task CloseAsync(string path, CancellationToken ct);

    /// <summary>Preview of the whole photo (at most <paramref name="maxSide"/> pixels) or of a region at full resolution.</summary>
    Task<DevelopRender> RenderAsync(string path, JsonObject settings, (int X, int Y, int Width, int Height)? region, int maxSide, bool denoise,
        IProgress<DevelopProgress>? progress, CancellationToken ct);

    /// <summary>The whole photo with the noise reduction (long queue of the engine).</summary>
    Task<DevelopRender> RenderFullAsync(string path, JsonObject settings, int maxSide, IProgress<DevelopProgress>? progress, CancellationToken ct);

    /// <summary>Histogram (red, green, blue, luminance × 256) of the developed photo.</summary>
    Task<int[][]> HistogramAsync(string path, JsonObject settings, CancellationToken ct);

    /// <summary>Temperature and tint that make the point neutral (eyedropper).</summary>
    Task<(double Temperature, double Tint)> PickWhiteBalanceAsync(string path, double x, double y, CancellationToken ct);

    Task<JsonObject> ApplyPresetAsync(string name, JsonObject settings, CancellationToken ct);

    Task<IReadOnlyList<DevelopExportResult>> ExportAsync(IReadOnlyList<DevelopExportItem> items, JsonObject output, string policy, string nonRawFormat,
        IProgress<DevelopProgress>? progress, CancellationToken ct);

    Task DownloadModelAsync(string key, IProgress<DevelopProgress>? progress, CancellationToken ct);
}

/// <summary>Raised when the engine reports an error (the message is for the user).</summary>
public sealed class DevelopEngineException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}

/// <summary>An export recorded in the catalog (decision S4: what has been delivered, and where).</summary>
public sealed record ExportRecord(long AssetId, string OutputPath, string Recipe, DateTimeOffset ExportedAt);

/// <summary>Development settings of the photos (decision S9: the catalog is the source of truth).</summary>
public interface IDevelopStore
{
    Task<string?> GetSettingsAsync(long assetId, CancellationToken ct);

    Task SetSettingsAsync(long assetId, string settingsJson, CancellationToken ct);

    /// <summary>The photos among <paramref name="assetIds"/> with settings of their own.</summary>
    Task<IReadOnlySet<long>> ListDevelopedAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct);

    Task AddExportAsync(ExportRecord record, CancellationToken ct);

    /// <summary>The last export of each photo among <paramref name="assetIds"/>.</summary>
    Task<IReadOnlyDictionary<long, ExportRecord>> LastExportsAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct);
}
