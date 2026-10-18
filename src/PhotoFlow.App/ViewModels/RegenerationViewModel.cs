// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Sources.Jobs;

namespace PhotoFlow.App.ViewModels;

/// <summary>A part of the analysis the regeneration can compute again.</summary>
public sealed partial class RegenerationPartViewModel(ReanalysisParts part, bool available) : ObservableObject
{
    public ReanalysisParts Part { get; } = part;

    public string Label => Strings.Get("Regeneration.Part." + Part);

    public bool IsAvailable { get; } = available;

    [ObservableProperty]
    private bool _isChecked = available;
}

/// <summary>
/// Regeneration (spec §5, D14; board 5g "Prova su campione → anteprima differenze → rianalisi"): perimeter (a library and
/// a folder of it, the photos of a job, a search result), the parts to compute again, "only older models" or "force
/// all", leaving out what was reviewed; the whole catalog too, and, with the faces, "Riparti da zero con le persone"
/// (P22: every person and assignment goes, manual ones included, after a confirmation with the counts). A trial on a sample or the whole perimeter runs as a low-priority job; the new
/// results wait; the preview shows the differences; "Applica" confirms them (the user's decisions stay, I5), "Scarta"
/// keeps the catalog as it was.
/// </summary>
public sealed partial class RegenerationViewModel : PageViewModel, IDisposable
{
    public const int SampleSize = 50;

    private static readonly CommandTable Table = new(
    [
        new("Regeneration.Sample", new KeyGesture(Key.P, KeyModifiers.Control), "Regeneration.Command.Sample", StatusGroupKey: "Regeneration.Command.Sample"),
        new("Regeneration.Run", new KeyGesture(Key.R, KeyModifiers.Control), "Regeneration.Command.Run", StatusGroupKey: "Regeneration.Command.Run"),
        new("Regeneration.Apply", new KeyGesture(Key.Enter, KeyModifiers.Control), "Regeneration.Command.Apply", StatusGroupKey: "Regeneration.Command.Apply"),
    ]);

    private readonly PhotoFlowHost _host;
    private readonly IShellNavigator? _navigator;
    private readonly long? _sourceJobId;
    private readonly IReadOnlyList<long>? _assetIds;
    private readonly bool _allCatalog;

    /// <param name="libraryId">Perimeter: a library (and a folder of it).</param>
    /// <param name="sourceJobId">Perimeter: the photos of a job.</param>
    /// <param name="assetIds">Perimeter: chosen photos (a search result, one photo).</param>
    /// <param name="reanalysisJobId">A regeneration already run: its preview.</param>
    /// <param name="parts">Parts proposed (a new model: the parts it makes).</param>
    /// <param name="allCatalog">Perimeter proposed: the whole catalog (a new face recognizer, P22).</param>
    public RegenerationViewModel(PhotoFlowHost host, IShellNavigator? navigator, long? libraryId = null, long? sourceJobId = null, IReadOnlyList<long>? assetIds = null,
        long? reanalysisJobId = null, ReanalysisParts? parts = null, string? note = null, bool allCatalog = false)
    {
        _allCatalog = allCatalog;
        _host = host;
        _navigator = navigator;
        _sourceJobId = sourceJobId;
        _assetIds = assetIds;
        _libraryId = libraryId;
        JobId = reanalysisJobId;
        Note = note;
        var setup = host.DescribeAnalysis();
        var available = new Dictionary<ReanalysisParts, bool>
        {
            [ReanalysisParts.Culling] = setup.ObjectDetector is not null,
            [ReanalysisParts.Faces] = setup.FaceDetector is not null,
            [ReanalysisParts.Embedding] = setup.Embedder is not null,
            [ReanalysisParts.Tags] = host.Models.IsInstalled(PhotoFlowHost.RamPlusModelId),
            [ReanalysisParts.Captions] = host.Models.IsInstalled(PhotoFlowHost.CaptionModelId),
            [ReanalysisParts.Ocr] = host.Models.IsInstalled(PhotoFlowHost.OcrModelId),
        };
        foreach (var (part, isAvailable) in available)
        {
            var row = new RegenerationPartViewModel(part, isAvailable) { IsChecked = isAvailable && (parts is null || parts.Value.HasFlag(part)) };
            if (part == ReanalysisParts.Faces)
            {
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(RegenerationPartViewModel.IsChecked))
                    {
                        OnPropertyChanged(nameof(CanResetPeople));
                    }
                };
            }

            Parts.Add(row);
        }

        _host.Jobs.StatusChanged += OnJobStatus;
    }

    public override string Title => Strings.Get("Regeneration.Title");

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Regeneration.Sample" => SampleCommand,
        "Regeneration.Run" => RunCommand,
        "Regeneration.Apply" => ApplyCommand,
        _ => base.ResolveCommand(id),
    };

    public ObservableCollection<OptionItem<long?>> Libraries { get; } = [];

    public ObservableCollection<RegenerationPartViewModel> Parts { get; } = [];

    public ObservableCollection<string> PreviewLines { get; } = [];

    public ObservableCollection<string> Examples { get; } = [];

    public string? Note { get; }

    public bool HasNote => Note is not null;

    /// <summary>The perimeter is a job or chosen photos (no library to choose).</summary>
    public bool HasFixedScope => _sourceJobId is not null || _assetIds is not null;

    public string ScopeText => _assetIds is { } ids ? Strings.Format("Regeneration.Scope.Photos", ids.Count.ToString("N0", CultureInfo.CurrentCulture))
        : _sourceJobId is { } job ? Strings.Format("Regeneration.Scope.Job", job.ToString(CultureInfo.InvariantCulture)) : string.Empty;

    [ObservableProperty]
    private long? _libraryId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllCatalog))]
    private OptionItem<long?>? _library;

    /// <summary>The perimeter is the whole catalog (the last choice of the list, P22).</summary>
    public bool IsAllCatalog => !HasFixedScope && Library is { Value: null };

    /// <summary>The faces are computed again: the people can start from zero (P22).</summary>
    public bool CanResetPeople => Parts.Any(p => p.Part == ReanalysisParts.Faces && p.IsChecked && p.IsAvailable);

    /// <summary>"Riparti da zero con le persone": asked, then confirmed after the counts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AsksResetConfirmation))]
    private bool _resetPeople;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AsksResetConfirmation))]
    private bool _resetConfirmed;

    [ObservableProperty]
    private string? _resetText;

    public bool AsksResetConfirmation => ResetPeople && !ResetConfirmed;

    [ObservableProperty]
    private string? _folder;

    [ObservableProperty]
    private bool _forceAll;

    [ObservableProperty]
    private bool _excludeReviewed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(HasPreview))]
    private long? _jobId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private bool _running;

    [ObservableProperty]
    private string? _progressText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private ReanalysisPreview? _preview;

    [ObservableProperty]
    private bool _isSample;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsRunning => Running;

    public bool HasPreview => Preview is not null && !Running;

    public override async Task ActivateAsync()
    {
        Libraries.Clear();
        foreach (var library in await _host.Catalog.Libraries.ListAsync(CancellationToken.None))
        {
            Libraries.Add(new OptionItem<long?>(library.Id, library.Name));
        }

        Libraries.Add(new OptionItem<long?>(null, Strings.Get("Regeneration.Scope.AllCatalog")));
        Library = _allCatalog ? Libraries[^1] : Libraries.FirstOrDefault(l => l.Value is not null && l.Value == LibraryId) ?? Libraries[0];
        if (JobId is { } jobId)
        {
            Running = _host.Jobs.IsRunning(jobId);
            IsSample = (await _host.Catalog.Jobs.GetAsync(jobId, CancellationToken.None))?.EventName == "sample";
            if (!Running)
            {
                await LoadPreviewAsync(jobId);
            }
        }
    }

    partial void OnLibraryChanged(OptionItem<long?>? value) => LibraryId = value?.Value;

    partial void OnResetPeopleChanged(bool value)
    {
        ResetConfirmed = false;
        ResetText = null;
        if (value)
        {
            _ = ShowResetCountsAsync();
        }
    }

    private async Task ShowResetCountsAsync()
    {
        var counts = await Task.Run(() => _host.Catalog.People.CountResetAsync(CancellationToken.None));
        ResetText = ResetCountsText(counts);
    }

    private static string ResetCountsText(PeopleReset counts)
    {
        string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
        return Strings.Format("Regeneration.Reset.Counts", N(counts.Persons), N(counts.NamedPersons), N(counts.ManualAssignments), N(counts.DrawnFaces));
    }

    /// <summary>The second, explicit step of the reset: the counts were shown.</summary>
    [RelayCommand]
    private void ConfirmReset() => ResetConfirmed = ResetPeople;

    [RelayCommand]
    private void CancelReset() => ResetPeople = false;

    private ReanalysisSettings Settings(int? sample) => new(
        Parts.Where(p => p.IsChecked && p.IsAvailable).Aggregate(ReanalysisParts.None, (all, p) => all | p.Part),
        ForceAll ? ReanalysisMode.ForceAll : ReanalysisMode.OlderModelOnly,
        _assetIds is null && _sourceJobId is null ? LibraryId : null,
        _assetIds is null && _sourceJobId is null && !string.IsNullOrWhiteSpace(Folder) ? Folder.Trim() : null,
        _sourceJobId,
        _assetIds,
        ExcludeReviewed,
        sample,
        _host.Settings.Import.ProfileId,
        AllCatalog: IsAllCatalog,
        ResetPeople: ResetPeople && ResetConfirmed && CanResetPeople && sample is null);

    /// <summary>Ctrl P: a trial on a sample of the perimeter.</summary>
    [RelayCommand]
    private Task SampleAsync() => StartAsync(SampleSize);

    /// <summary>Ctrl R: the whole perimeter.</summary>
    [RelayCommand]
    private Task RunAsync() => StartAsync(null);

    private async Task StartAsync(int? sample)
    {
        if (Running)
        {
            return;
        }

        var settings = Settings(sample);
        if (settings.Parts == ReanalysisParts.None || (settings.LibraryId is null && settings.SourceJobId is null && settings.AssetIds is null && !settings.AllCatalog))
        {
            ResultText = Strings.Get("Regeneration.NothingChosen");
            return;
        }

        if (sample is null && AsksResetConfirmation && CanResetPeople)
        {
            ResultText = Strings.Get("Regeneration.Reset.ConfirmFirst");
            return;
        }

        if (JobId is { } previous && Preview is not null)
        {
            await _host.DiscardReanalysisAsync(previous, CancellationToken.None);
        }

        Preview = null;
        PreviewLines.Clear();
        Examples.Clear();
        ResultText = null;
        IsSample = sample is not null;
        Running = true;
        ProgressText = Strings.Get("Regeneration.Starting");
        JobId = await _host.StartReanalysisAsync(settings, CancellationToken.None);
    }

    private void OnJobStatus(object? sender, JobStatus status)
    {
        if (status.JobId != JobId)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            if (status.Progress is { } p)
            {
                ProgressText = Strings.Format("Regeneration.Progress", p.Completed.ToString("N0", CultureInfo.CurrentCulture), p.Total.ToString("N0", CultureInfo.CurrentCulture));
            }

            if (status.State is JobState.Completed or JobState.Failed or JobState.Paused or JobState.Cancelled)
            {
                Running = false;
                if (status.State == JobState.Completed)
                {
                    await LoadPreviewAsync(status.JobId);
                }
                else
                {
                    ResultText = Strings.Get("Regeneration.Stopped");
                }
            }
        });
    }

    public async Task LoadPreviewAsync(long jobId)
    {
        var preview = await Task.Run(() => _host.PreviewReanalysisAsync(jobId, CancellationToken.None));
        PreviewLines.Clear();
        Examples.Clear();
        string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
        PreviewLines.Add(Strings.Format(IsSample ? "Regeneration.Preview.PhotosSample" : "Regeneration.Preview.Photos", N(preview.Photos)));
        if (preview.OutcomesChanged > 0)
        {
            PreviewLines.Add(Strings.Format("Regeneration.Preview.Outcomes", N(preview.OutcomesChanged), N(preview.UserDecisionsKept)));
        }

        if (preview.TagsAdded + preview.TagsRemoved > 0)
        {
            PreviewLines.Add(Strings.Format("Regeneration.Preview.Tags", N(preview.TagsAdded), N(preview.TagsRemoved), N(preview.TagsProtected)));
        }

        if (preview.FacesTransferred + preview.FacesNew + preview.FacesLost + preview.FacesKeptManual > 0)
        {
            PreviewLines.Add(Strings.Format("Regeneration.Preview.Faces", N(preview.FacesTransferred), N(preview.FacesNew), N(preview.FacesLost), N(preview.FacesKeptManual)));
        }

        if (preview.CaptionsChanged + preview.CaptionsProtected > 0)
        {
            PreviewLines.Add(Strings.Format("Regeneration.Preview.Captions", N(preview.CaptionsChanged), N(preview.CaptionsProtected)));
        }

        if (preview.Embeddings > 0)
        {
            PreviewLines.Add(Strings.Format("Regeneration.Preview.Embeddings", N(preview.Embeddings)));
        }

        if (preview.OcrChanged > 0)
        {
            PreviewLines.Add(Strings.Format("Regeneration.Preview.Ocr", N(preview.OcrChanged)));
        }

        // P22: what the reset will remove, counted again right before "Applica".
        if (await _host.Catalog.Jobs.GetAsync(jobId, CancellationToken.None) is { } job
            && ReanalysisJobRunner.DeserializeSettings(job.SettingsSnapshotJson) is { ResetPeople: true, Sample: null })
        {
            var counts = await Task.Run(() => _host.Catalog.People.CountResetAsync(CancellationToken.None));
            PreviewLines.Add(Strings.Get("Regeneration.Reset.Preview") + " " + ResetCountsText(counts));
        }

        foreach (var example in preview.Examples)
        {
            Examples.Add(example);
        }

        Preview = preview;
        ResultText = preview.Photos == 0 ? Strings.Get("Regeneration.NothingToDo") : null;
    }

    /// <summary>Ctrl Enter: the new results replace the old ones (the user's decisions stay).</summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (JobId is not { } jobId || Preview is not { Photos: > 0 } || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var applied = await Task.Run(() => _host.ApplyReanalysisAsync(jobId, CancellationToken.None));
            ResultText = Strings.Format("Regeneration.Applied", applied.Photos.ToString("N0", CultureInfo.CurrentCulture),
                applied.Transferred.ToString("N0", CultureInfo.CurrentCulture), applied.XmpWritten.ToString("N0", CultureInfo.CurrentCulture));
            if (applied.Reset is { } reset)
            {
                ResultText += " " + Strings.Format("Regeneration.Reset.Done", reset.Persons.ToString("N0", CultureInfo.CurrentCulture),
                    reset.ManualAssignments.ToString("N0", CultureInfo.CurrentCulture), reset.DrawnFaces.ToString("N0", CultureInfo.CurrentCulture));
                ResetPeople = false;
            }

            Preview = null;
            PreviewLines.Clear();
            Examples.Clear();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (JobId is { } jobId)
        {
            await _host.DiscardReanalysisAsync(jobId, CancellationToken.None);
            Preview = null;
            PreviewLines.Clear();
            Examples.Clear();
            ResultText = Strings.Get("Regeneration.Discarded");
        }
    }

    public void Dispose() => _host.Jobs.StatusChanged -= OnJobStatus;
}
