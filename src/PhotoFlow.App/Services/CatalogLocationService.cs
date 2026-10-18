// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using PhotoFlow.Core.Paths;
using PhotoFlow.Data;

namespace PhotoFlow.App.Services;

/// <summary>A change of catalog waiting for the next start: a move (copy to <see cref="Target"/>) or another catalog to open.</summary>
public sealed record PendingCatalogChange(bool Move, string Target);

/// <summary>
/// Where the catalog lives (spec §9): chosen at the first start, before the catalog exists, and changed in the settings.
/// The choice is kept in <c>config/catalog-location.json</c> of the local data folder. A move copies the closed catalog
/// at the next start and leaves the original where it was; opening another catalog only checks it. If the change
/// fails at startup, the catalog of before stays in use and the reason is kept for the settings. Nothing is deleted.
/// </summary>
public sealed class CatalogLocationService(DefaultAppPaths defaults)
{
    public const string FileName = "catalog-location.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private string FilePath => Path.Combine(defaults.ConfigDirectory, FileName);

    /// <summary>The catalog in use (the default one until another is chosen).</summary>
    public string CatalogPath => Read().CatalogPath ?? defaults.CatalogPath;

    /// <summary>The proposal of the first start: the local data folder.</summary>
    public string DefaultFolder => Path.GetDirectoryName(defaults.CatalogPath)!;

    /// <summary>True before the very first catalog: nothing chosen yet and no catalog in the default place.</summary>
    public bool NeedsFirstChoice => !File.Exists(FilePath) && !File.Exists(defaults.CatalogPath);

    public PendingCatalogChange? Pending => Read() switch
    {
        { PendingMove: { } move } => new PendingCatalogChange(true, move),
        { PendingOpen: { } open } => new PendingCatalogChange(false, open),
        _ => null,
    };

    /// <summary>What happened to the last change applied at startup (shown once in the settings).</summary>
    public string? LastResult => Read().LastResult;

    /// <summary>The paths of the application with the catalog in use.</summary>
    public IAppPaths Resolve() => new DefaultAppPaths(defaults.DataDirectory, new AppPathOverrides(CatalogPath: CatalogPath));

    /// <summary>
    /// First start: the folder of the catalog. A valid PhotoFlow catalog already there (another PC, a NAS) is used as it
    /// is; otherwise the catalog is created there at the start. Returns true when an existing catalog was found.
    /// </summary>
    /// <exception cref="InvalidOperationException">The folder cannot be written, or holds a file of that name that is not a catalog.</exception>
    public async Task<bool> ChooseAsync(string folder, CancellationToken ct)
    {
        var target = CatalogIn(folder);
        var existing = File.Exists(target);
        if (existing && !await SqliteCatalog.IsCatalogAsync(target, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"{target} exists and is not a PhotoFlow catalog.");
        }

        EnsureWritable(folder);
        Write(Read() with { CatalogPath = target });
        return existing;
    }

    /// <summary>Settings: moves the catalog into <paramref name="folder"/> at the next start (the original stays).</summary>
    /// <exception cref="InvalidOperationException">Same place, a catalog already there, or a folder that cannot be written.</exception>
    public void RequestMove(string folder)
    {
        var target = CatalogIn(folder);
        if (string.Equals(target, Path.GetFullPath(CatalogPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The catalog is already in this folder.");
        }

        if (File.Exists(target))
        {
            throw new InvalidOperationException($"{target} already exists: open it as an existing catalog instead.");
        }

        EnsureWritable(folder);
        Write(Read() with { PendingMove = target, PendingOpen = null, LastResult = null });
    }

    /// <summary>Settings: uses another existing catalog from the next start (checked now and again then).</summary>
    /// <exception cref="InvalidOperationException">The file is not a PhotoFlow catalog in good health.</exception>
    public async Task RequestOpenAsync(string file, CancellationToken ct)
    {
        var target = Path.GetFullPath(file);
        if (!await SqliteCatalog.IsCatalogAsync(target, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"{target} is not a PhotoFlow catalog in good health.");
        }

        Write(Read() with { PendingOpen = target, PendingMove = null, LastResult = null });
    }

    public void CancelPending() => Write(Read() with { PendingMove = null, PendingOpen = null });

    /// <summary>
    /// At startup, before the catalog opens: applies a pending move or opening. On failure the catalog of before stays
    /// in use; either way the outcome is kept in <see cref="LastResult"/>.
    /// </summary>
    public async Task ApplyPendingAsync(CancellationToken ct)
    {
        var stored = Read();
        if (stored is { PendingMove: null, PendingOpen: null })
        {
            return;
        }

        var current = CatalogPath;
        try
        {
            if (stored.PendingMove is { } move)
            {
                if (File.Exists(current))
                {
                    await SqliteCatalog.CopyAsync(current, move, ct).ConfigureAwait(false);
                }

                Write(stored with { CatalogPath = move, PendingMove = null, LastResult = $"moved|{move}|{current}" });
            }
            else if (stored.PendingOpen is { } open)
            {
                if (!await SqliteCatalog.IsCatalogAsync(open, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException($"{open} is no longer a PhotoFlow catalog in good health.");
                }

                Write(stored with { CatalogPath = open, PendingOpen = null, LastResult = $"opened|{open}|{current}" });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) // locked and damaged catalogs are IOExceptions
        {
            Write(stored with { PendingMove = null, PendingOpen = null, LastResult = $"failed|{e.Message}|{current}" });
        }
    }

    /// <summary>Forgets the outcome once shown.</summary>
    public void ClearLastResult() => Write(Read() with { LastResult = null });

    private static string CatalogIn(string folder) => Path.Combine(Path.GetFullPath(folder), DefaultAppPaths.CatalogFileName);

    private static void EnsureWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            using var probe = new FileStream(Path.Combine(folder, $".photoflow-write-test-{Environment.ProcessId}"), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException($"{folder} cannot be written: {e.Message}", e);
        }
    }

    private Stored Read()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath), Json) ?? new Stored() : new Stored();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Stored();
        }
    }

    private void Write(Stored stored)
    {
        Directory.CreateDirectory(defaults.ConfigDirectory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(stored, Json));
        File.Move(temporary, FilePath, overwrite: true);
    }

    private sealed record Stored(string? CatalogPath = null, string? PendingMove = null, string? PendingOpen = null, string? LastResult = null);
}
