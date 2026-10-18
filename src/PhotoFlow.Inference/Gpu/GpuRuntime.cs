// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhotoFlow.Inference.Models;

namespace PhotoFlow.Inference.Gpu;

public sealed record GpuRuntimeComponent(string Name, string Version, string License, string Url, string Sha256, long Size, IReadOnlyList<string> Extract);

public sealed record GpuRuntimeManifest(string Platform, IReadOnlyList<GpuRuntimeComponent> Components)
{
    public long TotalBytes => Components.Sum(c => c.Size);

    /// <summary>The manifest embedded in this assembly (models/gpu-runtime.json).</summary>
    public static GpuRuntimeManifest Embedded()
    {
        using var stream = typeof(GpuRuntimeManifest).Assembly.GetManifestResourceStream("PhotoFlow.Inference.gpu-runtime.json")!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var components = root.GetProperty("components").EnumerateArray().Select(c => new GpuRuntimeComponent(
            c.GetProperty("name").GetString()!,
            c.GetProperty("version").GetString()!,
            c.GetProperty("license").GetString()!,
            c.GetProperty("url").GetString()!,
            c.GetProperty("sha256").GetString()!,
            c.GetProperty("size").GetInt64(),
            c.GetProperty("extract").EnumerateArray().Select(e => e.GetString()!).ToList())).ToList();
        return new GpuRuntimeManifest(root.GetProperty("platform").GetString()!, components);
    }
}

/// <summary>
/// Installs the CUDA/cuDNN runtime DLLs needed by the ONNX Runtime CUDA provider (decision F1-2): each archive is
/// downloaded from NVIDIA's redistributable server, verified with SHA-256 while streaming, and only the listed DLLs are
/// extracted. Archives stay in the download folder: they double as an offline package and nothing is ever deleted (I4).
/// </summary>
public sealed partial class GpuRuntimeInstaller(HttpClient http, GpuRuntimeManifest manifest, string runtimeDirectory, string downloadDirectory)
{
    public string RuntimeDirectory => runtimeDirectory;

    public bool IsInstalled => manifest.Components.All(c => File.Exists(MarkerPath(c)));

    /// <summary>
    /// Downloads (or reuses) and installs every missing component. The user must have accepted the NVIDIA licenses
    /// (CUDA Toolkit EULA, cuDNN SLA) shown before the download; the libraries are for NVIDIA GPUs only.
    /// </summary>
    public async Task InstallAsync(bool licensesAccepted, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!licensesAccepted)
        {
            throw new InvalidOperationException("The NVIDIA licenses must be accepted before downloading the GPU runtime.");
        }

        Directory.CreateDirectory(runtimeDirectory);
        Directory.CreateDirectory(downloadDirectory);
        foreach (var component in manifest.Components.Where(c => !File.Exists(MarkerPath(c))))
        {
            var archive = Path.Combine(downloadDirectory, Path.GetFileName(new Uri(component.Url).LocalPath));
            await VerifiedDownload.FetchAsync(http, new Uri(component.Url), archive, component.Sha256, component.Size, component.Name, progress, ct).ConfigureAwait(false);

            Extract(component, archive);
            await File.WriteAllTextAsync(MarkerPath(component), component.Sha256, ct).ConfigureAwait(false);
        }
    }

    /// <summary>True when <paramref name="folder"/> holds every archive still missing, so an offline import needs no network.</summary>
    public bool HasOfflinePackage(string folder) =>
        !IsInstalled && manifest.Components.Where(c => !File.Exists(MarkerPath(c)))
            .All(c => File.Exists(Path.Combine(folder, Path.GetFileName(new Uri(c.Url).LocalPath))));

    /// <summary>Installs from archives copied by the user (offline package, spec §11): same verification.</summary>
    public Task ImportOfflineAsync(string folder, bool licensesAccepted, CancellationToken ct)
    {
        foreach (var component in manifest.Components)
        {
            var name = Path.GetFileName(new Uri(component.Url).LocalPath);
            var source = Path.Combine(folder, name);
            var target = Path.Combine(downloadDirectory, name);
            if (File.Exists(source) && !File.Exists(target))
            {
                Directory.CreateDirectory(downloadDirectory);
                File.Copy(source, target);
            }
        }

        return InstallAsync(licensesAccepted, null, ct);
    }

    private void Extract(GpuRuntimeComponent component, string archive)
    {
        var patterns = component.Extract.Select(Glob).ToList();
        using var zip = ZipFile.OpenRead(archive);
        var extracted = 0;
        foreach (var entry in zip.Entries.Where(e => e.Length > 0))
        {
            // Matched by file name: the folder layout differs between releases (bin/ vs bin/x64/).
            if (patterns.Any(p => p.IsMatch(entry.Name)))
            {
                entry.ExtractToFile(Path.Combine(runtimeDirectory, entry.Name), overwrite: true);
                extracted++;
            }
            else if (entry.Name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase) && entry.FullName.Count(c => c == '/') == 1)
            {
                // NVIDIA's license travels with the libraries it covers.
                entry.ExtractToFile(Path.Combine(runtimeDirectory, $"{component.Name}-LICENSE.txt"), overwrite: true);
            }
        }

        if (extracted == 0)
        {
            throw new InvalidDataException($"{component.Name}: no runtime library found in {Path.GetFileName(archive)}.");
        }
    }

    private string MarkerPath(GpuRuntimeComponent component) => Path.Combine(runtimeDirectory, $"{component.Name}-{component.Version}.installed");

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", "[^/]*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
