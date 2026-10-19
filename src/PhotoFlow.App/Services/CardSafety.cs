// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PhotoFlow.App.Localization;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.Sources;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Jobs;

namespace PhotoFlow.App.Services;

public enum CardSafetyState
{
    /// <summary>Photos still to import, or nothing to say.</summary>
    None,

    /// <summary>The import of the card is still running.</summary>
    Importing,

    /// <summary>Some files were not copied: the card must not be formatted.</summary>
    Failed,

    /// <summary>Everything imported, but in one place only.</summary>
    OneCopy,

    /// <summary>Everything imported and verified in at least two places: the card can be formatted.</summary>
    Safe,
}

public sealed record CardSafetyInfo(CardSafetyState State, string Text)
{
    public static CardSafetyInfo None { get; } = new(CardSafetyState.None, string.Empty);
}

/// <summary>
/// «Scheda al sicuro» (M6): when every photo of the card is in the catalog with its copies verified in at least two places
/// (rule 3-2-1), the card can be formatted in the camera. The card itself is never touched (I1).
/// </summary>
public static class CardSafety
{
    public static async Task<CardSafetyInfo> AssessAsync(ICatalog catalog, CardVolume card, ImportPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.NewPhotos > 0 || plan.AlreadyImportedPhotos == 0)
        {
            return CardSafetyInfo.None;
        }

        var root = Normalize(card.RootPath);
        var job = (await catalog.Jobs.ListRecentAsync(100, ct)).FirstOrDefault(j => j.Type == JobType.Import && j.Source is { } s && Normalize(s) == root);
        if (job is null)
        {
            return CardSafetyInfo.None; // imported some other way: nothing to say about the copies
        }

        if (job.State is JobState.Pending or JobState.Running or JobState.Paused)
        {
            return new(CardSafetyState.Importing, Strings.Get("Home.Card.Safety.Importing"));
        }

        var items = await catalog.Jobs.GetItemsAsync(job.Id, ct);
        var failed = items.Count(i => i.State == JobItemState.Failed);
        if (failed > 0)
        {
            return new(CardSafetyState.Failed, Strings.Format("Home.Card.Safety.Failed", failed.ToString("N0", CultureInfo.CurrentCulture)));
        }

        var settings = ImportJobRunner.DeserializeSettings(job.SettingsSnapshotJson);
        var copies = settings.Destinations.Where(d => d.Kind != DestinationKind.Deferred).Select(d => d.RootPath).ToList();
        var deferred = settings.Destinations.Any(d => d.Kind == DestinationKind.Deferred);
        var pending = deferred && await catalog.DeferredCopies.CountPendingAsync(ct) > 0;
        var photos = plan.AlreadyImportedPhotos.ToString("N0", CultureInfo.CurrentCulture);
        var places = string.Join(", ", copies);
        if (copies.Count + (deferred && !pending ? 1 : 0) >= 2)
        {
            return new(CardSafetyState.Safe, Strings.Format("Home.Card.Safety.Safe", photos, (copies.Count + (deferred && !pending ? 1 : 0)).ToString(CultureInfo.CurrentCulture), places));
        }

        return new(CardSafetyState.OneCopy, Strings.Format(pending ? "Home.Card.Safety.OneCopyNas" : "Home.Card.Safety.OneCopy", photos, places));
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path).ToUpperInvariant();
}

/// <summary>Safe removal of a card (M6), as «Espelli» in Explorer: Windows only.</summary>
public static class CardEjector
{
    public static bool TryEject(string rootPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                return false;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic drives = shell.NameSpace(17); // «Questo PC»
            dynamic drive = drives.ParseName(Path.TrimEndingDirectorySeparator(rootPath));
            if (drive is null)
            {
                return false;
            }

            drive.InvokeVerb("Eject");
            return true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException)
        {
            return false;
        }
    }
}
