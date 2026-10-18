// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text.Json;
using PhotoFlow.Core.Inference;

namespace PhotoFlow.Inference.Models;

public sealed record ModelLicense(string Spdx, string Source, bool CommercialUse, bool RequiresAcceptance, string? Notes);

public sealed record ModelFile(string Path, string Url, string Sha256, long Size, string? Archive, IReadOnlyList<string> Use);

/// <summary>One entry of models/manifest.json (spec §3 "registro modelli", §11).</summary>
public sealed record ModelInfo(
    string Id, ModelFunction Function, string Name, string Version, int Phase, string? DefaultFor, string Adapter, ModelLicense License, IReadOnlyList<ModelFile> Files)
{
    public long DownloadBytes => Files.Sum(f => f.Size);

    /// <summary>
    /// The user downloads the original files into <see cref="ModelRegistry.SourceDirectory"/>; PhotoFlow verifies and
    /// converts them (level D, phase 5b, decision U2). The files of the manifest are the originals.
    /// </summary>
    public bool ManualConvert { get; init; }

    /// <summary>The file a conversion leaves in the model folder (<c>output</c> in the manifest): the GenAI configuration of a VLM, the ONNX of a face model.</summary>
    public string ConvertedFile { get; init; } = ModelRegistry.ConvertedConfig;
}

/// <summary>A file of a manually downloaded model that is missing or not the expected one.</summary>
public sealed record SourceFileProblem(string Path, bool Missing);

/// <summary>
/// Model registry: the manifest embedded in the application, local state, verified download at first use with the
/// license shown first and explicit acceptance when required (buffalo_l, CLAUDE.md §6, P11).
/// </summary>
public sealed class ModelRegistry(HttpClient http, IReadOnlyList<ModelInfo> models, string modelsDirectory)
{
    public IReadOnlyList<ModelInfo> Models => models;

    public static IReadOnlyList<ModelInfo> EmbeddedManifest()
    {
        using var stream = typeof(ModelRegistry).Assembly.GetManifestResourceStream("PhotoFlow.Inference.manifest.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("models").EnumerateArray().Select(Parse).ToList();
    }

    public ModelInfo Get(string id) =>
        models.FirstOrDefault(m => m.Id == id) ?? throw new KeyNotFoundException($"Unknown model '{id}'.");

    public bool IsInstalled(string id)
    {
        var model = Get(id);
        return model.ManualConvert
            ? File.Exists(MarkerPath(model)) && File.Exists(Path.Combine(ModelDirectory(model), model.ConvertedFile))
            : File.Exists(MarkerPath(model)) && LocalFiles(model).All(File.Exists);
    }

    /// <summary>The file the conversion of a VLM leaves in its folder (ONNX Runtime GenAI configuration), the default of <see cref="ModelInfo.ConvertedFile"/>.</summary>
    public const string ConvertedConfig = "genai_config.json";

    /// <summary>Folder of an installed model (the converted one for a manual model).</summary>
    public string InstallDirectory(string id) => ModelDirectory(Get(id));

    /// <summary>Folder where the user puts the original files of a manual model.</summary>
    public string SourceDirectory(string id) => Path.Combine(modelsDirectory, Get(id).Id + "-source");

    /// <summary>
    /// The original files of a manual model that are missing or differ (size, then SHA-256), in the order of the
    /// manifest; empty when all are there and intact. <paramref name="progress"/> gets the bytes checked.
    /// </summary>
    public async Task<IReadOnlyList<SourceFileProblem>> VerifySourceAsync(string id, IProgress<long>? progress, CancellationToken ct)
    {
        var model = Get(id);
        var problems = new List<SourceFileProblem>();
        long done = 0;
        foreach (var file in model.Files)
        {
            var path = Path.Combine(SourceDirectory(id), file.Path);
            if (!File.Exists(path))
            {
                problems.Add(new SourceFileProblem(file.Path, Missing: true));
            }
            else if (new FileInfo(path).Length != file.Size
                || !string.Equals(await VerifiedDownload.Sha256Async(path, ct).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new SourceFileProblem(file.Path, Missing: false));
            }

            done += file.Size;
            progress?.Report(done);
        }

        return problems;
    }

    /// <summary>
    /// Downloads the original files of a manual model into <see cref="SourceDirectory"/> (pinned URLs, SHA-256 checked;
    /// the files already there and intact are kept), after the license when it needs acceptance. The conversion follows.
    /// </summary>
    public async Task DownloadSourceAsync(string id, bool licenseAccepted, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var model = Get(id);
        if (!model.ManualConvert)
        {
            throw new InvalidOperationException($"'{model.Name}' is installed with a download, not converted.");
        }

        if (model.License.RequiresAcceptance && !licenseAccepted)
        {
            throw new InvalidOperationException($"The license of '{model.Name}' ({model.License.Spdx}) must be accepted before downloading it.");
        }

        foreach (var file in model.Files)
        {
            await VerifiedDownload.FetchAsync(http, new Uri(file.Url), Path.Combine(SourceDirectory(id), file.Path), file.Sha256, file.Size,
                $"{model.Id}/{file.Path}", progress, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Records a manual model as installed once its conversion left its files in <see cref="InstallDirectory"/>.</summary>
    public Task MarkConvertedAsync(string id, CancellationToken ct)
    {
        var model = Get(id);
        return File.WriteAllTextAsync(MarkerPath(model), model.Version, ct);
    }

    public async Task InstallAsync(string id, bool licenseAccepted, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var model = Get(id);
        if (model.ManualConvert)
        {
            throw new InvalidOperationException($"'{model.Name}' is downloaded by the user and converted on this PC, not downloaded by PhotoFlow.");
        }

        if (model.License.RequiresAcceptance && !licenseAccepted)
        {
            throw new InvalidOperationException($"The license of '{model.Name}' ({model.License.Spdx}) must be accepted before downloading it.");
        }

        var folder = ModelDirectory(model);
        foreach (var file in model.Files)
        {
            var target = Path.Combine(folder, file.Path);
            await VerifiedDownload.FetchAsync(http, new Uri(file.Url), target, file.Sha256, file.Size, $"{model.Id}/{file.Path}", progress, ct).ConfigureAwait(false);
            if (file.Archive == "zip")
            {
                Extract(target, folder, file.Use);
            }
        }

        await File.WriteAllTextAsync(MarkerPath(model), model.Version, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs the models found in a folder copied by the user (offline package, spec §11): each file is looked up as
    /// <c>folder/&lt;model id&gt;/&lt;file&gt;</c> or <c>folder/&lt;file&gt;</c>, and copied with the same SHA-256 check as a download.
    /// A model whose license needs acceptance is skipped unless <paramref name="licenseAccepted"/> says so; a model with
    /// missing files is skipped. Returns the ids installed. The folder is only read.
    /// </summary>
    public async Task<IReadOnlyList<string>> ImportOfflineAsync(string folder, Func<ModelInfo, bool> licenseAccepted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(licenseAccepted);
        var installed = new List<string>();
        foreach (var model in models.Where(m => !m.ManualConvert && !IsInstalled(m.Id)))
        {
            if (model.License.RequiresAcceptance && !licenseAccepted(model))
            {
                continue;
            }

            var sources = model.Files.Select(f => new[] { Path.Combine(folder, model.Id, f.Path), Path.Combine(folder, f.Path) }.FirstOrDefault(File.Exists)).ToList();
            if (sources.Any(s => s is null))
            {
                continue;
            }

            var target = ModelDirectory(model);
            foreach (var (file, source) in model.Files.Zip(sources))
            {
                var local = Path.Combine(target, file.Path);
                await VerifiedDownload.CopyAsync(source!, local, file.Sha256, file.Size, $"{model.Id}/{file.Path}", ct).ConfigureAwait(false);
                if (file.Archive == "zip")
                {
                    Extract(local, target, file.Use);
                }
            }

            await File.WriteAllTextAsync(MarkerPath(model), model.Version, ct).ConfigureAwait(false);
            installed.Add(model.Id);
        }

        return installed;
    }

    /// <summary>Re-hashes the downloaded files (e.g. after copying a model package from another PC).</summary>
    public async Task<bool> VerifyAsync(string id, CancellationToken ct)
    {
        var model = Get(id);
        foreach (var file in model.Files)
        {
            var path = Path.Combine(ModelDirectory(model), file.Path);
            if (!File.Exists(path) || !string.Equals(await VerifiedDownload.Sha256Async(path, ct).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The local file of an installed model (an archive member is addressed by its file name).</summary>
    public ModelDescriptor Resolve(string id, string fileName)
    {
        var model = Get(id);
        if (!IsInstalled(id))
        {
            throw new InvalidOperationException($"Model '{id}' is not installed.");
        }

        var file = model.Files.FirstOrDefault(f => f.Path == fileName);
        var path = Path.Combine(ModelDirectory(model), fileName);
        if (model.ManualConvert && fileName == model.ConvertedFile)
        {
            return new ModelDescriptor(id, model.Function, model.Name, model.Version, path, string.Empty, model.Adapter); // made on this PC
        }

        if (file is null && !model.Files.Any(f => f.Use.Contains(fileName)))
        {
            throw new KeyNotFoundException($"Model '{id}' has no file '{fileName}'.");
        }

        return new ModelDescriptor(id, model.Function, model.Name, model.Version, path, file?.Sha256 ?? string.Empty, model.Adapter);
    }

    private string ModelDirectory(ModelInfo model) => Path.Combine(modelsDirectory, model.Id);

    private string MarkerPath(ModelInfo model) => Path.Combine(ModelDirectory(model), ".installed");

    private IEnumerable<string> LocalFiles(ModelInfo model) =>
        model.Files.SelectMany(f => f.Use.Count > 0 ? f.Use : [f.Path]).Select(p => Path.Combine(ModelDirectory(model), p));

    private static void Extract(string archive, string folder, IReadOnlyList<string> use)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var name in use)
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"{Path.GetFileName(archive)} does not contain {name}.");
            entry.ExtractToFile(Path.Combine(folder, entry.Name), overwrite: true);
        }
    }

    private static ModelInfo Parse(JsonElement m)
    {
        var license = m.GetProperty("license");
        return new ModelInfo(
            m.GetProperty("id").GetString()!,
            Enum.Parse<ModelFunction>(m.GetProperty("function").GetString()!),
            m.GetProperty("name").GetString()!,
            m.GetProperty("version").GetString()!,
            m.GetProperty("phase").GetInt32(),
            m.TryGetProperty("defaultFor", out var d) ? d.GetString() : null,
            m.GetProperty("adapter").GetString()!,
            new ModelLicense(
                license.GetProperty("spdx").GetString()!,
                license.GetProperty("source").GetString()!,
                license.GetProperty("commercialUse").GetBoolean(),
                license.GetProperty("requiresAcceptance").GetBoolean(),
                license.TryGetProperty("notes", out var n) ? n.GetString() : null),
            m.GetProperty("files").EnumerateArray().Select(f => new ModelFile(
                f.GetProperty("path").GetString()!,
                f.GetProperty("url").GetString()!,
                f.GetProperty("sha256").GetString()!,
                f.GetProperty("size").GetInt64(),
                f.TryGetProperty("archive", out var a) ? a.GetString() : null,
                f.TryGetProperty("use", out var u) ? u.EnumerateArray().Select(x => x.GetString()!).ToList() : [])).ToList())
        {
            ManualConvert = m.TryGetProperty("install", out var install) && install.GetString() == "manual-convert",
            ConvertedFile = m.TryGetProperty("output", out var output) ? output.GetString()! : ConvertedConfig,
        };
    }
}
