// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Paths;
using PhotoFlow.Data;
using PhotoFlow.Faces.Protection;

namespace PhotoFlow.App.Services;

/// <param name="IncludeThumbnails">The thumbnails travel too (otherwise they are made again from the photos).</param>
/// <param name="FingerprintPassword">With a password the fingerprints travel, encrypted with a key it protects; without, they stay behind.</param>
public sealed record CatalogExportOptions(bool IncludeThumbnails, string? FingerprintPassword);

public sealed record CatalogExportSummary(string Path, int Libraries, int Assets, int Fingerprints, int Thumbnails, long Bytes);

public sealed record PackageLibrary(long Id, string Name, string RootPath, int Assets);

/// <summary>The description of a .pfcatalog (manifest.json).</summary>
public sealed record CatalogPackageManifest(
    int Format,
    string App,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    IReadOnlyList<PackageLibrary> Libraries,
    FingerprintKeyStore.PasswordWrap? FingerprintKey,
    int Thumbnails,
    IReadOnlyList<string> Profiles)
{
    public bool HasFingerprints => FingerprintKey is not null;
}

public sealed class CatalogPackageException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Export and import of the whole catalog as one file (spec §9, decision F5-4): a zip with <c>manifest.json</c>, a
/// consistent copy of the catalog (SQLite backup API), the custom profiles and, optionally, the thumbnails. The
/// fingerprints are encrypted again with a random key protected by the export password (PBKDF2-SHA256 and AES-GCM as
/// the local key), or left out. The import prepares the catalog with the library roots remapped and the fingerprints
/// encrypted with the local key; it replaces the current catalog at the next start, as a restore (the replaced one is
/// kept). Profiles and thumbnails are added without replacing files that are already there.
/// </summary>
public static class CatalogPackage
{
    public const int Format = 1;
    public const string Extension = ".pfcatalog";
    private const string ManifestEntry = "manifest.json";
    private const string CatalogEntry = "catalog.pfdb";
    private const string ProfilesFolder = "profiles/";
    private const string ThumbnailsFolder = "thumbnails/";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<CatalogExportSummary> ExportAsync(ICatalog catalog, IAppPaths paths, IFingerprintProtector localKey, string outputPath,
        CatalogExportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(localKey);
        ArgumentNullException.ThrowIfNull(options);
        var work = Path.Combine(paths.TempDirectory, "export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // A consistent copy, changed (fingerprints) without touching the catalog in use.
            var copyPath = await catalog.BackupAsync(work, "export", ct).ConfigureAwait(false);
            FingerprintKeyStore.PasswordWrap? wrap = null;
            int fingerprints;
            var libraries = new List<PackageLibrary>();
            int assets = 0, schema;
            await using (var copy = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(copyPath, Path.Combine(work, "backups")), ct).ConfigureAwait(false))
            {
                schema = await copy.GetSchemaVersionAsync(ct).ConfigureAwait(false);
                foreach (var library in await copy.Libraries.ListAsync(ct).ConfigureAwait(false))
                {
                    var count = await copy.Libraries.CountAssetsAsync(library.Id, ct).ConfigureAwait(false);
                    assets += count;
                    libraries.Add(new PackageLibrary(library.Id, library.Name, library.RootPath, count));
                }

                if (options.FingerprintPassword is { Length: > 0 } password && localKey.IsAvailable)
                {
                    var exportKey = RandomNumberGenerator.GetBytes(32);
                    fingerprints = await ReencryptAsync(copy, localKey, new FingerprintCipher(exportKey), ct).ConfigureAwait(false);
                    wrap = FingerprintKeyStore.Wrap(exportKey, password);
                }
                else
                {
                    await copy.People.ClearFingerprintsAsync(ct).ConfigureAwait(false);
                    fingerprints = 0;
                }
            }

            var profiles = Directory.Exists(paths.ProfilesDirectory)
                ? Directory.EnumerateFiles(paths.ProfilesDirectory, "custom-*.json").Order(StringComparer.Ordinal).ToList()
                : [];
            var thumbnails = options.IncludeThumbnails && Directory.Exists(paths.ThumbnailsDirectory)
                ? Directory.EnumerateFiles(paths.ThumbnailsDirectory, "*.jpg", SearchOption.AllDirectories).ToList()
                : [];
            var manifest = new CatalogPackageManifest(Format, AppVersion(), DateTimeOffset.UtcNow, schema, libraries, wrap, thumbnails.Count,
                [.. profiles.Select(Path.GetFileName).OfType<string>()]);

            // Written next to the target and moved at the end: a cancelled export leaves no half file.
            var partial = outputPath + ".partial";
            await using (var stream = File.Create(partial))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await using (var entry = zip.CreateEntry(ManifestEntry).Open())
                {
                    await JsonSerializer.SerializeAsync(entry, manifest, Json, ct).ConfigureAwait(false);
                }

                zip.CreateEntryFromFile(copyPath, CatalogEntry, CompressionLevel.Optimal);
                foreach (var profile in profiles)
                {
                    zip.CreateEntryFromFile(profile, ProfilesFolder + Path.GetFileName(profile), CompressionLevel.Optimal);
                }

                foreach (var thumbnail in thumbnails)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(paths.ThumbnailsDirectory, thumbnail).Replace('\\', '/');
                    zip.CreateEntryFromFile(thumbnail, ThumbnailsFolder + relative, CompressionLevel.NoCompression);
                }
            }

            File.Move(partial, outputPath, overwrite: true);
            return new CatalogExportSummary(outputPath, libraries.Count, assets, fingerprints, thumbnails.Count, new FileInfo(outputPath).Length);
        }
        finally
        {
            TryDelete(paths, work);
        }
    }

    public static CatalogPackageManifest ReadManifest(string packagePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(packagePath);
            using var stream = (zip.GetEntry(ManifestEntry) ?? throw new CatalogPackageException($"{Path.GetFileName(packagePath)} is not a PhotoFlow catalog.")).Open();
            var manifest = JsonSerializer.Deserialize<CatalogPackageManifest>(stream, Json) ?? throw new CatalogPackageException("The catalog description is empty.");
            return manifest.Format > Format
                ? throw new CatalogPackageException($"The catalog was exported by a newer PhotoFlow (format {manifest.Format}).")
                : manifest;
        }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        {
            throw new CatalogPackageException($"{Path.GetFileName(packagePath)} is damaged or is not a PhotoFlow catalog.", e);
        }
    }

    /// <summary>
    /// Prepares the imported catalog in the backups folder: library roots remapped (<paramref name="roots"/>, by
    /// library id; the others keep their path), fingerprints encrypted with <paramref name="localKey"/> when the
    /// export password is right, removed otherwise. Profiles and thumbnails are added now. Returns the prepared catalog,
    /// to restore at the next start.
    /// </summary>
    /// <exception cref="CatalogPackageException">Damaged file, newer format, or wrong password.</exception>
    public static async Task<string> PrepareImportAsync(string packagePath, IAppPaths paths, IReadOnlyDictionary<long, string> roots, string? password,
        IFingerprintProtector? localKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(roots);
        var manifest = ReadManifest(packagePath);
        byte[]? exportKey = null;
        if (manifest.FingerprintKey is { } wrap && password is { Length: > 0 })
        {
            try
            {
                exportKey = FingerprintKeyStore.Unwrap(wrap, password);
            }
            catch (CryptographicException e)
            {
                throw new CatalogPackageException("The password of the export is not right.", e);
            }
        }

        Directory.CreateDirectory(paths.BackupsDirectory);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(paths.BackupsDirectory, $"{Path.GetFileNameWithoutExtension(paths.CatalogPath)}-import-{stamp}{Path.GetExtension(paths.CatalogPath)}");
        using (var zip = ZipFile.OpenRead(packagePath))
        {
            var entry = zip.GetEntry(CatalogEntry) ?? throw new CatalogPackageException("The file holds no catalog.");
            entry.ExtractToFile(target, overwrite: false);
            foreach (var other in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var folder = other.FullName.StartsWith(ProfilesFolder, StringComparison.Ordinal) ? paths.ProfilesDirectory
                    : other.FullName.StartsWith(ThumbnailsFolder, StringComparison.Ordinal) ? paths.ThumbnailsDirectory : null;
                if (folder is null || other.Name.Length == 0)
                {
                    continue;
                }

                var relative = other.FullName[(other.FullName.IndexOf('/', StringComparison.Ordinal) + 1)..];
                var destination = Path.GetFullPath(Path.Combine(folder, relative));
                if (!destination.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase) || File.Exists(destination))
                {
                    continue; // nothing outside the folder; nothing replaced
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                other.ExtractToFile(destination);
            }
        }

        try
        {
            await using var imported = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(target, paths.BackupsDirectory), ct).ConfigureAwait(false);
            foreach (var (libraryId, root) in roots)
            {
                if (await imported.Libraries.GetAsync(libraryId, ct).ConfigureAwait(false) is not null)
                {
                    await imported.Libraries.UpdateRootAsync(libraryId, Path.GetFullPath(root), ct).ConfigureAwait(false);
                }
            }

            if (exportKey is not null && localKey is { IsAvailable: true })
            {
                await ReencryptAsync(imported, new FingerprintCipher(exportKey), localKey, ct).ConfigureAwait(false);
            }
            else
            {
                await imported.People.ClearFingerprintsAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            TryDelete(paths, target);
            throw;
        }

        return target;
    }

    /// <summary>Every fingerprint (faces and prototypes) decrypted with one key and encrypted with the other.</summary>
    private static async Task<int> ReencryptAsync(ICatalog catalog, IFingerprintProtector from, IFingerprintProtector to, CancellationToken ct)
    {
        var count = 0;
        foreach (var face in await catalog.People.ListAllFingerprintsAsync(ct).ConfigureAwait(false))
        {
            await catalog.People.SetFingerprintAsync(face.FaceId, to.Protect(from.Unprotect(face.EncryptedEmbedding)), face.EmbeddingModel, ct).ConfigureAwait(false);
            count++;
        }

        foreach (var person in (await catalog.People.ListPrototypesAsync(ct).ConfigureAwait(false)).GroupBy(p => p.PersonId))
        {
            await catalog.People.ReplacePrototypesAsync(person.Key,
                [.. person.Select(p => p with { EncryptedEmbedding = to.Protect(from.Unprotect(p.EncryptedEmbedding)) })], ct).ConfigureAwait(false);
        }

        return count;
    }

    private static string AppVersion() =>
        typeof(CatalogPackage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

    /// <summary>Only PhotoFlow's own working files, through the cleaner of the internal folders (spec §10, B1).</summary>
    private static void TryDelete(IAppPaths paths, string path)
    {
        var cleaner = new InternalFileCleaner(paths);
        try
        {
            var files = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList() : File.Exists(path) ? [path] : [];
            foreach (var file in files)
            {
                cleaner.DeleteInternalFile(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // left for the cleaning of the temporary folder
        }
    }
}
