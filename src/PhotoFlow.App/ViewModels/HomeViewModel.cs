// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Sources;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Sources.Scanning;

namespace PhotoFlow.App.ViewModels;

public enum HomeState
{
    /// <summary>Board 3b: hardware, models, first library.</summary>
    FirstRun,

    /// <summary>Board 3c: jobs interrupted by an unexpected shutdown can resume.</summary>
    Recovery,

    /// <summary>Board 3a: card banner (when a card is in), active jobs, photos to review, libraries.</summary>
    Normal,
}

/// <summary>A card found by the detector, with its import plan ("1.064 nuove · 248 già importate").</summary>
public sealed class CardBannerViewModel(CardVolume volume, ImportPlan plan, CardSafetyInfo? safety = null)
{
    public CardVolume Volume { get; } = volume;

    public ImportPlan Plan { get; } = plan;

    /// <summary>«Scheda al sicuro» (M6): may the card be formatted?</summary>
    public CardSafetyInfo Safety { get; } = safety ?? CardSafetyInfo.None;

    public bool HasSafety => Safety.State != CardSafetyState.None;

    public bool IsSafe => Safety.State == CardSafetyState.Safe;

    public bool IsUnsafe => Safety.State is CardSafetyState.Failed or CardSafetyState.OneCopy;

    public bool IsImporting => Safety.State == CardSafetyState.Importing;

    public string Title => Strings.Format("Home.Card.Title", Volume.Label, Volume.RootPath.TrimEnd('\\', '/'));

    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                Strings.Format("Home.Card.New", Plan.NewPhotos.ToString("N0", CultureInfo.CurrentCulture)),
                Strings.Format("Home.Card.Already", Plan.AlreadyImportedPhotos.ToString("N0", CultureInfo.CurrentCulture)),
            };
            if (Plan.FirstCapture is { } first && Plan.LastCapture is { } last)
            {
                parts.Add(Formats.CaptureRange(first, last));
            }

            parts.Add(Formats.Bytes(Plan.TotalBytes));
            return string.Join(" · ", parts);
        }
    }

    public string ImportLabel => Strings.Format("Home.Card.Import", Plan.NewPhotos.ToString("N0", CultureInfo.CurrentCulture));

    public bool HasNew => Plan.NewPhotos > 0;
}

/// <summary>A job in the Home lists (active, to review, interrupted).</summary>
public sealed partial class JobRowViewModel(Job job) : ObservableObject
{
    public Job Job { get; } = job;

    public string Name => string.IsNullOrWhiteSpace(Job.EventName) ? Strings.Get("Job.Unnamed") : Job.EventName;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _remaining = string.Empty;

    [ObservableProperty]
    private int _toReview;

    public string ToReviewText => Strings.Format("Home.ToReview.Count", ToReview.ToString("N0", CultureInfo.CurrentCulture));

    partial void OnToReviewChanged(int value) => OnPropertyChanged(nameof(ToReviewText));

    // --- Where the event is in the work (M10) ---

    public int Photos { get; init; }

    public int Chosen { get; init; }

    public int Developed { get; init; }

    public int Exported { get; init; }

    public bool HasToReview => ToReview > 0;

    public string PhotosText => Strings.Format("Home.Photos", Photos.ToString("N0", CultureInfo.CurrentCulture));

    public string ChosenText => Strings.Format("Home.Event.Chosen", Chosen.ToString("N0", CultureInfo.CurrentCulture));

    public string DevelopedText => Strings.Format("Home.Event.Developed", Developed.ToString("N0", CultureInfo.CurrentCulture));

    public string ExportedText => Strings.Format("Home.Event.Exported", Exported.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>The next step of the work (S4): choose, develop, deliver; nothing when everything chosen is delivered.</summary>
    public EventStep NextStep => ToReview > 0 ? EventStep.Review
        : Chosen == 0 ? EventStep.Review
        : Developed < Chosen ? EventStep.Develop
        : Exported < Chosen ? EventStep.Export
        : EventStep.Done;

    public bool HasNextStep => NextStep != EventStep.Done;

    public string NextStepText => Strings.Get("Home.Event.Next." + NextStep);

    /// <summary>0 imported, 1 chosen, 2 developed (in part), 3 exported (in part), 4 done.</summary>
    public int StageValue => NextStep switch
    {
        EventStep.Review => ToReview > 0 ? 0 : 1,
        EventStep.Develop => Developed > 0 ? 2 : 1,
        EventStep.Export => Exported > 0 ? 3 : 2,
        _ => 4,
    };
}

public enum EventStep
{
    Review,
    Develop,
    Export,
    Done,
}

public sealed record LibraryRowViewModel(Library Library, int Photos)
{
    public string PhotosText => Photos.ToString("N0", CultureInfo.CurrentCulture);

    public string Detail => $"{Library.RootPath} · {Strings.Get(Library.IsReadOnly ? "Library.Policy.CatalogOnly" : "Library.Policy." + Library.XmpPolicy)}";
}

/// <summary>Home (boards 3a-3c): what needs attention now, and the way into an import.</summary>
public sealed partial class HomeViewModel : PageViewModel, IDisposable
{
    private readonly PhotoFlowHost _host;
    private readonly IShellNavigator _navigator;
    private readonly Func<CardVolume?, string?, ImportWizardViewModel> _wizardFactory;
    private bool _recoveryDismissed;

    public HomeViewModel(PhotoFlowHost host, IShellNavigator navigator, Func<CardVolume?, string?, ImportWizardViewModel> wizardFactory)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(wizardFactory);
        _host = host;
        _navigator = navigator;
        _wizardFactory = wizardFactory;
        FirstRun = new FirstRunViewModel(host, () => _ = ActivateAsync());
        _host.Cards.CardInserted += OnCardInserted;
        _host.Cards.CardRemoved += OnCardRemoved;
        _host.Jobs.StatusChanged += OnJobStatus;
        foreach (var card in _host.Cards.Current)
        {
            OnCardInserted(this, card);
        }
    }

    public override string Title => Strings.Get("Nav.Home");

    public const string PrimaryCommand = "Home.Primary";
    public const string SecondaryCommand = "Home.Secondary";

    private static readonly CommandTable Table = new(
    [
        new(PrimaryCommand, new KeyGesture(Key.Enter), "Home.Command.Primary", StatusGroupKey: "Home.Command.Primary"),
        new(SecondaryCommand, new KeyGesture(Key.Escape), "Home.Command.Secondary"),
    ]);

    public override CommandTable Commands => Table;

    /// <summary>The view shows the folder picker and calls <see cref="ImportFolder"/>.</summary>
    public event EventHandler? FolderPickRequested;

    public override ICommand ResolveCommand(string id) => id switch
    {
        PrimaryCommand => PrimaryActionCommand,
        SecondaryCommand => SecondaryActionCommand,
        _ => base.ResolveCommand(id),
    };

    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        if (IsRecovery)
        {
            await ResumeInterruptedAsync();
        }
        else if (IsNormal && Card is not null)
        {
            ImportCard();
        }
    }

    [RelayCommand]
    private void SecondaryAction()
    {
        if (IsRecovery)
        {
            ResumeLater();
        }
        else
        {
            IgnoreCard();
        }
    }

    [RelayCommand]
    private void RequestFolder() => FolderPickRequested?.Invoke(this, EventArgs.Empty);

    public FirstRunViewModel FirstRun { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstRun), nameof(IsRecovery), nameof(IsNormal))]
    private HomeState _state = HomeState.Normal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCard))]
    private CardBannerViewModel? _card;

    public bool IsFirstRun => State == HomeState.FirstRun;

    public bool IsRecovery => State == HomeState.Recovery;

    public bool IsNormal => State == HomeState.Normal;

    public bool HasCard => Card is not null;

    public ObservableCollection<JobRowViewModel> ActiveJobs { get; } = [];

    public ObservableCollection<JobRowViewModel> ToReview { get; } = [];

    public ObservableCollection<JobRowViewModel> Interrupted { get; } = [];

    /// <summary>The recent events with where they are in the work and their next step (M10).</summary>
    public ObservableCollection<JobRowViewModel> Events { get; } = [];

    public bool HasEvents => Events.Count > 0;

    public ObservableCollection<LibraryRowViewModel> Libraries { get; } = [];

    public string ActiveJobsHeader => Strings.Format("Home.ActiveJobs", ActiveJobs.Count);

    public bool NoActiveJobs => ActiveJobs.Count == 0;

    public bool HasToReview => ToReview.Count > 0;

    public string LogsDirectory => _host.Paths.LogsDirectory;

    public string ResumeLabel => Strings.Format("Home.Recovery.Resume", Interrupted.Count);

    public override async Task ActivateAsync()
    {
        if (!_host.Settings.SetupCompleted)
        {
            State = HomeState.FirstRun;
            await FirstRun.RefreshAsync();
            return;
        }

        var catalog = _host.Catalog;
        var unfinished = await catalog.Jobs.ListUnfinishedAsync(CancellationToken.None);
        var interrupted = _host.InterruptedJobs.Select(j => j.Id).ToHashSet();

        // Everything is read first and the lists are replaced without awaiting in between: refreshes raised by job
        // events may overlap, and interleaved Clear/Add would duplicate rows.
        var active = new List<JobRowViewModel>();
        var stopped = new List<JobRowViewModel>();
        foreach (var job in unfinished)
        {
            var row = new JobRowViewModel(job) { Detail = await DescribeAsync(job) };
            if (interrupted.Contains(job.Id) && !_host.Jobs.IsRunning(job.Id) && !_recoveryDismissed)
            {
                stopped.Add(row);
            }

            active.Add(row);
        }

        var toReviewRows = new List<JobRowViewModel>();
        var eventRows = new List<JobRowViewModel>();
        foreach (var job in (await catalog.Jobs.ListRecentAsync(20, CancellationToken.None)).Where(j => j.State == JobState.Completed))
        {
            var counts = await catalog.Decisions.CountOutcomesForJobAsync(job.Id, CancellationToken.None);
            var toReview = counts.GetValueOrDefault(CullingOutcome.ToReview);
            var photos = counts.Values.Sum();
            if (toReview > 0)
            {
                toReviewRows.Add(new JobRowViewModel(job) { ToReview = toReview, Detail = Strings.Format("Home.Photos", photos.ToString("N0", CultureInfo.CurrentCulture)) });
            }

            // M10: the last events, with where they are in the work.
            if (eventRows.Count < 5 && photos > 0)
            {
                var chosen = await DevelopSources.BestOfJobAsync(catalog, job.Id, CancellationToken.None);
                var developed = await catalog.Develop.ListDevelopedAsync(chosen.ToList(), CancellationToken.None);
                var exported = await catalog.Develop.LastExportsAsync(chosen.ToList(), CancellationToken.None);
                eventRows.Add(new JobRowViewModel(job)
                {
                    ToReview = toReview, Photos = photos, Chosen = chosen.Count, Developed = developed.Count, Exported = exported.Count,
                    Detail = Strings.Format("Home.Photos", photos.ToString("N0", CultureInfo.CurrentCulture)),
                });
            }
        }

        var libraries = new List<LibraryRowViewModel>();
        foreach (var library in await catalog.Libraries.ListAsync(CancellationToken.None))
        {
            libraries.Add(new LibraryRowViewModel(library, await catalog.Libraries.CountAssetsAsync(library.Id, CancellationToken.None)));
        }

        Replace(Interrupted, stopped);
        Replace(ActiveJobs, active);
        Replace(ToReview, toReviewRows);
        Replace(Events, eventRows);
        Replace(Libraries, libraries);
        OnPropertyChanged(nameof(HasEvents));

        State = Interrupted.Count > 0 ? HomeState.Recovery : HomeState.Normal;
        OnPropertyChanged(nameof(ActiveJobsHeader));
        OnPropertyChanged(nameof(NoActiveJobs));
        OnPropertyChanged(nameof(HasToReview));
        OnPropertyChanged(nameof(ResumeLabel));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    [RelayCommand]
    private void ImportCard()
    {
        if (Card is { } card)
        {
            _navigator.ShowPage(_wizardFactory(card.Volume, null));
        }
    }

    [RelayCommand]
    private void ChooseWhatToImport() => ImportCard();

    [RelayCommand]
    private void IgnoreCard() => Card = null;

    [ObservableProperty]
    private string? _cardMessage;

    /// <summary>«Espelli» (M6): safe removal of the card, as in Explorer.</summary>
    [RelayCommand]
    private void EjectCard()
    {
        if (Card is not { } card)
        {
            return;
        }

        CardMessage = CardEjector.TryEject(card.Volume.RootPath) ? Strings.Get("Home.Card.Ejected") : Strings.Get("Home.Card.EjectFailed");
    }

    /// <summary>Opens the procedure on a folder chosen by the user (the view shows the folder picker).</summary>
    public void ImportFolder(string folder) => _navigator.ShowPage(_wizardFactory(null, folder));

    [RelayCommand]
    private async Task ResumeInterruptedAsync()
    {
        foreach (var row in Interrupted.ToList())
        {
            _ = _host.Jobs.StartAsync(row.Job.Id, row.Job.Priority);
        }

        _recoveryDismissed = true;
        Interrupted.Clear();
        State = HomeState.Normal;
        await ActivateAsync();
    }

    [RelayCommand]
    private void ResumeLater()
    {
        _recoveryDismissed = true;
        Interrupted.Clear();
        State = HomeState.Normal;
    }

    [RelayCommand]
    private void OpenReview(JobRowViewModel? row)
    {
        if (row is not null)
        {
            _navigator.OpenReview(row.Job.Id);
        }
    }

    [RelayCommand]
    private void OpenJobs() => _navigator.ShowSection(SectionCatalog.Jobs);

    /// <summary>The next step of an event (M10): its review, its development, its delivery.</summary>
    [RelayCommand]
    private async Task NextStepAsync(JobRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var name = row.Name;
        switch (row.NextStep)
        {
            case EventStep.Review:
                _navigator.OpenReview(row.Job.Id);
                break;
            case EventStep.Develop:
            case EventStep.Export:
                var chosen = await DevelopSources.BestOfJobAsync(_host.Catalog, row.Job.Id, CancellationToken.None);
                _host.WorkingSet.Set(Strings.Format("Develop.Origin.Review", name, chosen.Count), chosen, null, row.Job.EventName);
                _navigator.ShowSection(row.NextStep == EventStep.Develop ? SectionCatalog.Develop : SectionCatalog.Export);
                break;
            default:
                break;
        }
    }

    /// <summary>Quick actions (board 3a): search, people, duplicates.</summary>
    [RelayCommand]
    private void OpenSection(string? sectionId)
    {
        if (sectionId is not null)
        {
            _navigator.ShowSection(sectionId);
        }
    }

    public void Dispose()
    {
        _host.Cards.CardInserted -= OnCardInserted;
        _host.Cards.CardRemoved -= OnCardRemoved;
        _host.Jobs.StatusChanged -= OnJobStatus;
    }

    private async Task<string> DescribeAsync(Job job)
    {
        var items = await _host.Catalog.Jobs.GetItemsAsync(job.Id, CancellationToken.None);
        var copied = items.Count(i => i.AssetId is not null);
        var done = items.Count(i => i.State == JobItemState.Done);
        var kind = Strings.Get("JobType." + job.Type);
        return copied == items.Count
            ? Strings.Format("Home.Job.AnalysisProgress", kind, done.ToString("N0", CultureInfo.CurrentCulture), items.Count.ToString("N0", CultureInfo.CurrentCulture))
            : Strings.Format("Home.Job.CopyProgress", kind, copied.ToString("N0", CultureInfo.CurrentCulture), items.Count.ToString("N0", CultureInfo.CurrentCulture));
    }

    private void OnJobStatus(object? sender, JobStatus status) => Ui.Post(() =>
    {
        var row = ActiveJobs.FirstOrDefault(r => r.Job.Id == status.JobId);
        if (row is null)
        {
            if (status.State == JobState.Running)
            {
                _ = ActivateAsync();
            }

            return;
        }

        if (status.State != JobState.Running)
        {
            _ = ActivateAsync();
            if (Card is { } card && status.State == JobState.Completed)
            {
                OnCardInserted(this, card.Volume); // the card banner says now whether the card is safe (M6)
            }

            return;
        }

        var p = status.Progress;
        row.Progress = p.Total == 0 ? 0 : (double)(p.Completed + p.Failed) / p.Total;
        row.Remaining = status.Remaining is { } r ? Formats.Duration(r) : string.Empty;
        row.Detail = p.Copied < p.Total
            ? Strings.Format("Home.Job.CopyProgress", Strings.Get("JobType.Import"), p.Copied.ToString("N0", CultureInfo.CurrentCulture), p.Total.ToString("N0", CultureInfo.CurrentCulture))
            : Strings.Format("Home.Job.AnalysisProgress", Strings.Get("JobType.Import"), p.Completed.ToString("N0", CultureInfo.CurrentCulture), p.Total.ToString("N0", CultureInfo.CurrentCulture));
    });

    private void OnCardInserted(object? sender, CardVolume volume) => _ = Task.Run(async () =>
    {
        try
        {
            var scanned = new List<ScannedFile>();
            await foreach (var file in new SourceScanner(_host.FileSystem, volume.RootPath).ScanAsync(CancellationToken.None))
            {
                scanned.Add(file);
            }

            var plan = await _host.Planner.PlanAsync(scanned, string.Empty, null, _host.PathTemplate, CancellationToken.None);
            var safety = await CardSafety.AssessAsync(_host.Catalog, volume, plan, CancellationToken.None);
            Ui.Post(() => Card = new CardBannerViewModel(volume, plan, safety));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Card removed while scanning or unreadable: no banner.
        }
    });

    private void OnCardRemoved(object? sender, CardVolume volume) => Ui.Post(() =>
    {
        if (Card?.Volume.RootPath == volume.RootPath)
        {
            Card = null;
        }
    });
}

/// <summary>Number and time formats of the views (current culture: "1.064", "41,8 GB").</summary>
public static class Formats
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    public static string Duration(TimeSpan duration) =>
        duration.TotalMinutes < 1 ? Strings.Get("Duration.UnderMinute")
        : duration.TotalHours < 1 ? Strings.Format("Duration.Minutes", Math.Ceiling(duration.TotalMinutes).ToString(CultureInfo.CurrentCulture))
        : Strings.Format("Duration.HoursMinutes", ((int)duration.TotalHours).ToString(CultureInfo.CurrentCulture), duration.Minutes.ToString(CultureInfo.CurrentCulture));

    /// <summary>"12 ott 2026, 10:02–16:48" (same day) or "12–13 ott 2026".</summary>
    public static string CaptureRange(DateTimeOffset first, DateTimeOffset last)
    {
        var culture = CultureInfo.CurrentCulture;
        return first.Date == last.Date
            ? $"{first.ToString("d MMM yyyy", culture)}, {first.ToString("HH:mm", culture)}–{last.ToString("HH:mm", culture)}"
            : $"{first.ToString("d MMM", culture)} – {last.ToString("d MMM yyyy", culture)}";
    }
}
