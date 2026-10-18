// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Pipeline;

namespace PhotoFlow.App.ViewModels;

public enum ReviewMode
{
    Grid,
    Compare,
}

/// <summary>
/// Review (boards 2a-2h): grid of a job's photos in shooting order with the bursts and special sequences under their
/// headers, outcome filters, S/M/L sizes, comparison of 2-4 photos with synchronized zoom (2b), P/X/1-5/Ctrl+Z,
/// profile change per group, decisions during the analysis (2e, P2), empty state (2f), confirmation and XMP (2g, 2h,
/// P4), moving the discards on request (P6). Every action has a key (acceptance criterion: usable from the keyboard
/// only). User decisions win over the automatic ones (I5); XMP are written only after the confirmation.
/// </summary>
public sealed partial class ReviewViewModel : PageViewModel, IDisposable
{
    public static readonly int[] CellSizes = [120, 168, 240];
    public const int CellGap = 4;

    /// <summary>Long side of the large photo (P21): enough for a full-screen view on a 2560 px monitor.</summary>
    public const int LargePhotoSide = 2560;

    private static readonly CommandTable Table = new(
    [
        new("Review.Move", new KeyGesture(Key.Left), "Review.Command.Navigate", StatusGroupKey: "Review.Command.Navigate", Parameter: "-1,0"),
        new("Review.Move", new KeyGesture(Key.Right), "Review.Command.Navigate", StatusGroupKey: "Review.Command.Navigate", Parameter: "1,0"),
        new("Review.Move", new KeyGesture(Key.Up), "Review.Command.Navigate", StatusGroupKey: "Review.Command.Navigate", Parameter: "0,-1"),
        new("Review.Move", new KeyGesture(Key.Down), "Review.Command.Navigate", StatusGroupKey: "Review.Command.Navigate", Parameter: "0,1"),
        new("Review.Keep", new KeyGesture(Key.P), "Review.Command.Keep", StatusGroupKey: "Review.Command.Keep"),
        new("Review.Discard", new KeyGesture(Key.X), "Review.Command.Discard", StatusGroupKey: "Review.Command.Discard"),
        new("Review.Rate", new KeyGesture(Key.D0), "Review.Command.Stars", Parameter: "0"),
        new("Review.Rate", new KeyGesture(Key.D1), "Review.Command.Stars", StatusGroupKey: "Review.Command.Stars", Parameter: "1"),
        new("Review.Rate", new KeyGesture(Key.D2), "Review.Command.Stars", StatusGroupKey: "Review.Command.Stars", Parameter: "2"),
        new("Review.Rate", new KeyGesture(Key.D3), "Review.Command.Stars", StatusGroupKey: "Review.Command.Stars", Parameter: "3"),
        new("Review.Rate", new KeyGesture(Key.D4), "Review.Command.Stars", StatusGroupKey: "Review.Command.Stars", Parameter: "4"),
        new("Review.Rate", new KeyGesture(Key.D5), "Review.Command.Stars", StatusGroupKey: "Review.Command.Stars", Parameter: "5"),
        new("Review.Compare", new KeyGesture(Key.C), "Review.Command.Compare", StatusGroupKey: "Review.Command.Compare"),
        new("Review.Grid", new KeyGesture(Key.G), "Review.Command.Grid"),
        new("Review.Numbers", new KeyGesture(Key.N), "Review.Command.Numbers"),
        new("Review.Labeling", new KeyGesture(Key.L), "Review.Command.Labeling"),
        new("Review.Zoom", new KeyGesture(Key.Z), "Review.Command.Zoom", StatusGroupKey: "Review.Command.Zoom"),
        new("Review.Fit", new KeyGesture(Key.F), "Review.Command.Fit"),
        new("Review.Sync", new KeyGesture(Key.S), "Review.Command.Sync"),
        new("Review.Details", new KeyGesture(Key.Tab), "Review.Command.Details"),
        new("Review.NextGroup", new KeyGesture(Key.PageDown), "Review.Command.NextGroup"),
        new("Review.PreviousGroup", new KeyGesture(Key.PageUp), "Review.Command.PreviousGroup"),
        new("Review.Reanalyze", new KeyGesture(Key.R, KeyModifiers.Shift), "Review.Command.Reanalyze"),
        new("Review.Retry", new KeyGesture(Key.R, KeyModifiers.Control), "Review.Command.Retry"),
        new("Review.Bigger", new KeyGesture(Key.OemPlus, KeyModifiers.Control), "Review.Command.Size", Parameter: "1"),
        new("Review.Smaller", new KeyGesture(Key.OemMinus, KeyModifiers.Control), "Review.Command.Size", Parameter: "-1"),
        new("Review.Filter", new KeyGesture(Key.D0, KeyModifiers.Alt), "Review.Command.Filter", Parameter: "0"),
        new("Review.Filter", new KeyGesture(Key.D1, KeyModifiers.Alt), "Review.Command.Filter", StatusGroupKey: "Review.Command.Filter", Parameter: "1"),
        new("Review.Filter", new KeyGesture(Key.D2, KeyModifiers.Alt), "Review.Command.Filter", Parameter: "2"),
        new("Review.Filter", new KeyGesture(Key.D3, KeyModifiers.Alt), "Review.Command.Filter", Parameter: "3"),
        new("Review.Filter", new KeyGesture(Key.D4, KeyModifiers.Alt), "Review.Command.Filter", Parameter: "4"),
        new("Review.Filter", new KeyGesture(Key.D5, KeyModifiers.Alt), "Review.Command.Filter", Parameter: "5"),
        new("Review.MoveDiscards", new KeyGesture(Key.M, KeyModifiers.Control | KeyModifiers.Shift), "Review.Command.MoveDiscards"),
        new("Review.Undo", new KeyGesture(Key.Z, KeyModifiers.Control), "Review.Command.Undo", StatusGroupKey: "Review.Command.Undo"),
        new("Review.Confirm", new KeyGesture(Key.Enter, KeyModifiers.Control), "Review.Confirm", StatusGroupKey: "Review.Confirm"),
        new("Review.LargePhoto", new KeyGesture(Key.Space), "Review.Command.LargePhoto", StatusGroupKey: "Review.Command.LargePhoto"),
        new("Review.Tag", new KeyGesture(Key.T), "Browse.Command.Tag", StatusGroupKey: "Browse.Command.Tag"),
        new("Review.Caption", new KeyGesture(Key.D), "Browse.Command.Caption"),
        new("Review.Faces", new KeyGesture(Key.V), "Browse.Command.Faces"),
        new("Review.AddFace", new KeyGesture(Key.N, KeyModifiers.Shift), "Browse.Command.AddFace"),
        new("Review.Escape", new KeyGesture(Key.Escape), "Common.Cancel"),
    ]);

    private readonly PhotoFlowHost _host;
    private readonly IShellNavigator? _navigator;
    private readonly Stack<List<(ReviewItemViewModel Item, List<Decision?> Before)>> _undo = new();
    private readonly Dictionary<long, string> _personNames = [];
    private List<ReviewItemViewModel> _all = [];
    private List<ReviewGroupViewModel> _groups = [];
    private List<long> _failedAssets = [];
    private int _columns = 4;
    private string _profileId = "events";

    public ReviewViewModel(PhotoFlowHost host, IShellNavigator? navigator = null)
    {
        _host = host;
        _navigator = navigator;
        Filters = [new(null) { IsActive = true }, .. new[] { CullingOutcome.Discard, CullingOutcome.ToReview, CullingOutcome.Best, CullingOutcome.Similar, CullingOutcome.Kept }.Select(o => new OutcomeFilterViewModel(o))];
        Editor = new PhotoEditorViewModel(host);
        Editor.Changed += async (_, _) => await ShowPeopleAsync(Selected);
        Editor.LargePhotoRequested += (_, _) => OpenLargePhoto();
        _host.Jobs.StatusChanged += OnJobStatus;
    }

    /// <summary>Tags, caption and faces of the selected photo, fixed during the review (P21).</summary>
    public PhotoEditorViewModel Editor { get; }

    /// <summary>The selected photo shown large (Space or double click), with its faces.</summary>
    [ObservableProperty]
    private bool _isLargePhoto;

    [ObservableProperty]
    private Bitmap? _largeImage;

    private int _largeVersion;

    public override string Title => Strings.Get("Nav.Review");

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Review.Move" => MoveCommand,
        "Review.Keep" => KeepCommand,
        "Review.Discard" => DiscardCommand,
        "Review.Rate" => RateCommand,
        "Review.Compare" => CompareCommand,
        "Review.Grid" => ShowGridCommand,
        "Review.Numbers" => NumbersCommand,
        "Review.Labeling" => LabelingCommand,
        "Review.Zoom" => ZoomCommand,
        "Review.Fit" => FitCommand,
        "Review.Sync" => ToggleSyncCommand,
        "Review.Details" => ToggleDetailsCommand,
        "Review.NextGroup" => NextGroupCommand,
        "Review.PreviousGroup" => PreviousGroupCommand,
        "Review.Reanalyze" => ReanalyzeGroupCommand,
        "Review.Retry" => RetryXmpCommand,
        "Review.Bigger" or "Review.Smaller" => ResizeCommand,
        "Review.Filter" => FilterByIndexCommand,
        "Review.MoveDiscards" => RequestMoveDiscardsCommand,
        "Review.Undo" => UndoCommand,
        "Review.Confirm" => ConfirmOrWriteCommand,
        "Review.LargePhoto" => ToggleLargePhotoCommand,
        "Review.Tag" => Editor.StartTagCommand,
        "Review.Caption" => Editor.StartCaptionCommand,
        "Review.Faces" => Editor.ToggleFacesCommand,
        "Review.AddFace" => Editor.StartAddFaceCommand,
        "Review.Escape" => EscapeCommand,
        _ => base.ResolveCommand(id),
    };

    /// <summary>Raised when the selection moves, so that the view scrolls the row into view.</summary>
    public event EventHandler<ReviewRow>? RowFocusRequested;

    public IReadOnlyList<OutcomeFilterViewModel> Filters { get; }

    public ObservableCollection<ReviewRow> Rows { get; } = [];

    public ObservableCollection<SignalViewModel> SelectedSignals { get; } = [];

    public ObservableCollection<PersonChipViewModel> SelectedPeople { get; } = [];

    public ObservableCollection<CompareItemViewModel> CompareItems { get; } = [];

    [ObservableProperty]
    private long? _jobId;

    [ObservableProperty]
    private string _jobTitle = string.Empty;

    [ObservableProperty]
    private string _jobMeta = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedExif), nameof(SelectedGroupText))]
    private ReviewItemViewModel? _selected;

    [ObservableProperty]
    private Bitmap? _selectedPreview;

    [ObservableProperty]
    private int _manualCount;

    [ObservableProperty]
    private int _pendingXmp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGrid), nameof(IsCompare))]
    private ReviewMode _mode = ReviewMode.Grid;

    public bool IsGrid => Mode == ReviewMode.Grid;

    public bool IsCompare => Mode == ReviewMode.Compare;

    [ObservableProperty]
    private bool _isDetailsOpen = true;

    /// <summary>Index into <see cref="CellSizes"/> (S, M, L).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CellSize), nameof(SizeLabel))]
    private int _sizeIndex = 1;

    public int CellSize => CellSizes[SizeIndex];

    public string SizeLabel => $"{"SML"[SizeIndex]} · Ctrl +/−";

    /// <summary>Comparison zoom: null = fit, otherwise scale of the preview (1 = 100 %).</summary>
    [ObservableProperty]
    private double? _zoom;

    [ObservableProperty]
    private bool _isZoomSynced = true;

    /// <summary>Comparison pan, shared when synchronized (offset of the image centre, in preview pixels).</summary>
    [ObservableProperty]
    private Vector _pan;

    [ObservableProperty]
    private bool _isConfirming;

    [ObservableProperty]
    private bool _isWriting;

    [ObservableProperty]
    private string? _resultMessage;

    [ObservableProperty]
    private string? _resultError;

    [ObservableProperty]
    private bool _prudentMode = true;

    [ObservableProperty]
    private bool _openPeopleAfter = true;

    [ObservableProperty]
    private int _newPeopleGroups;

    /// <summary>The job is still being analyzed (2e): outcomes and groups come when the whole set is done.</summary>
    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string _analysisProgress = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Pending confirmation of "move the discards" (P6), with the number of photos.</summary>
    [ObservableProperty]
    private string? _moveDiscardsQuestion;

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => _all.Count == 0;

    /// <summary>2f: no job to review at all.</summary>
    public bool NoJob => JobId is null;

    public string EmptyText => Strings.Get(JobId is null ? "Review.Empty.NoJob" : "Review.Empty.Filter");

    public bool HasRows => Rows.Count > 0;

    public string SelectedGroupText => Selected?.Group is { } g ? $"{g.Title} · {Selected.RankText}" : Strings.Get("Review.Single");

    public string SelectedExif => Selected?.Exif is { } e
        ? string.Join(" · ", new[]
        {
            e.ExposureTimeSeconds is { } t ? (t >= 1 ? $"{t:0.#} s" : $"1/{Math.Round(1 / t):0} s") : null,
            e.Aperture is { } f ? string.Create(CultureInfo.CurrentCulture, $"f/{f:0.#}") : null,
            e.Iso is { } iso ? $"ISO {iso}" : null,
            e.FocalLengthMm is { } mm ? string.Create(CultureInfo.CurrentCulture, $"{mm:0} mm") : null,
        }.Where(x => x is not null))
        : string.Empty;

    public string ConfirmQuestion => Strings.Format("Review.Confirm.Question", _all.Count.ToString("N0", CultureInfo.CurrentCulture));

    public string ConfirmToReview => Strings.Format("Review.Confirm.ToReview", Count(CullingOutcome.ToReview).ToString("N0", CultureInfo.CurrentCulture));

    public string ConfirmCounts => string.Join(" · ",
        new[] { CullingOutcome.Best, CullingOutcome.ToReview, CullingOutcome.Similar, CullingOutcome.Kept, CullingOutcome.Discard }
            .Select(o => $"{Count(o).ToString("N0", CultureInfo.CurrentCulture)} {Strings.Get("Outcome." + o)}"));

    public string OpenPeopleText => Strings.Format("Review.Confirm.OpenPeople", NewPeopleGroups);

    public bool HasNewPeople => NewPeopleGroups > 0;

    public bool HasFailures => _failedAssets.Count > 0;

    public void SetColumns(int columns)
    {
        columns = Math.Max(1, columns);
        if (columns != _columns)
        {
            _columns = columns;
            Rebuild();
        }
    }

    /// <summary>Opens the review of a job (from Home, Jobs or the end of an import).</summary>
    public async Task OpenJobAsync(long jobId)
    {
        JobId = jobId;
        Selected = null;
        await ActivateAsync();
    }

    partial void OnJobIdChanged(long? value)
    {
        Selected = null;
        Mode = ReviewMode.Grid;
        OnPropertyChanged(nameof(NoJob));
    }

    partial void OnNewPeopleGroupsChanged(int value)
    {
        OnPropertyChanged(nameof(OpenPeopleText));
        OnPropertyChanged(nameof(HasNewPeople));
    }

    public override async Task ActivateAsync()
    {
        var catalog = _host.Catalog;
        PrudentMode = _host.Settings.PrudentMode;
        await Editor.LoadTagSuggestionsAsync();
        JobId ??= (await catalog.Jobs.ListRecentAsync(10, CancellationToken.None)).FirstOrDefault(j => j.State == JobState.Completed || j.Type == JobType.Import)?.Id;
        OnPropertyChanged(nameof(NoJob));
        if (JobId is not { } jobId || await catalog.Jobs.GetAsync(jobId, CancellationToken.None) is not { } job)
        {
            _all = [];
            _groups = [];
            Rebuild();
            return;
        }

        _profileId = job.ProfileId ?? "events";
        IsAnalyzing = _host.Jobs.IsRunning(jobId) || job.State is JobState.Running or JobState.Pending;
        var items = (await catalog.Jobs.GetItemsAsync(jobId, CancellationToken.None)).Where(i => i.AssetId is not null).ToList();
        var photos = new List<ReviewItemViewModel>();
        foreach (var group in items.GroupBy(i => Path.Combine(Path.GetDirectoryName(i.SourcePath) ?? string.Empty, Path.GetFileNameWithoutExtension(i.SourcePath)), StringComparer.OrdinalIgnoreCase))
        {
            var assetIds = group.Select(i => i.AssetId!.Value).Distinct().ToList();
            var results = await catalog.Analysis.GetResultsAsync(assetIds, CullingAnalyzer.AnalyzerId, CancellationToken.None);
            var analyzed = assetIds.FirstOrDefault(results.ContainsKey);
            if (analyzed == 0)
            {
                analyzed = assetIds[0];
            }

            var asset = await catalog.Assets.GetAsync(analyzed, CancellationToken.None);
            if (asset is null || asset.Handler == "video")
            {
                continue;
            }

            var name = string.Join(" + ", group.Select(i => Path.GetExtension(i.SourcePath).TrimStart('.').ToUpperInvariant()).Distinct());
            var item = new ReviewItemViewModel(
                assetIds, analyzed, $"{Path.GetFileNameWithoutExtension(group.First().SourcePath)} · {name}",
                ThumbnailOf(asset), asset.CapturedAt,
                results.TryGetValue(analyzed, out var result) ? SafeSignals(result.Scores) : null, asset.Exif)
            {
                Decision = await catalog.Decisions.GetAsync(analyzed, CancellationToken.None),
                XmpPending = (await catalog.XmpSync.GetAsync(analyzed, CancellationToken.None))?.WritePending == true,
            };
            photos.Add(item);
        }

        _all = [.. photos.OrderBy(p => p.CapturedAt ?? DateTimeOffset.MaxValue).ThenBy(p => p.Name, StringComparer.Ordinal)];
        await LoadGroupsAsync(jobId, job);
        JobTitle = string.IsNullOrWhiteSpace(job.EventName) ? Strings.Get("Job.Unnamed") : job.EventName;
        JobMeta = Strings.Format("Review.Meta", job.EventDate?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? string.Empty,
            _all.Count.ToString("N0", CultureInfo.CurrentCulture), _host.ProfileName(_profileId),
            Strings.Get(PrudentMode ? "Review.Mode.Prudent" : "Review.Mode.Normal"));
        NewPeopleGroups = await CountNewPeopleAsync(items.Select(i => i.AssetId!.Value).ToHashSet());
        Rebuild();
        Selected = Selected is { } current && _all.Contains(current) ? current : Visible().FirstOrDefault();
    }

    private async Task LoadGroupsAsync(long jobId, Job job)
    {
        var byAsset = _all.ToDictionary(p => p.AnalyzedAssetId);
        var profiles = await _host.Catalog.Groups.GetPhotoProfilesAsync(jobId, CancellationToken.None);
        _groups = [];
        var number = 0;
        foreach (var (group, members) in (await _host.Catalog.Groups.ListAsync(jobId, CancellationToken.None))
                     .OrderBy(g => g.Members.Select(m => byAsset.GetValueOrDefault(m.AssetId)?.CapturedAt ?? DateTimeOffset.MaxValue).Min()))
        {
            var photos = members.Select(m => byAsset.GetValueOrDefault(m.AssetId)).OfType<ReviewItemViewModel>().ToList();
            if (photos.Count == 0)
            {
                continue;
            }

            var first = photos[0].AnalyzedAssetId;
            var profile = profiles.GetValueOrDefault(first)?.ProfileId ?? group.ProfileId ?? job.ProfileId ?? "events";
            var view = new ReviewGroupViewModel(group, ++number, photos, profile) { NameOfProfile = _host.ProfileName };
            if (profiles.GetValueOrDefault(first) is { SuggestedProfileId: { } suggested, SuggestedConfidence: { } confidence })
            {
                view.Suggestion = (suggested, confidence);
            }

            var scores = members.ToDictionary(m => m.AssetId, m => m.Score);
            var ranked = group.IsRanked ? photos.OrderByDescending(p => scores.GetValueOrDefault(p.AnalyzedAssetId) ?? double.MinValue).ToList() : photos;
            foreach (var photo in photos)
            {
                photo.Group = view;
                photo.Score = scores.GetValueOrDefault(photo.AnalyzedAssetId);
                photo.Rank = ranked.IndexOf(photo) + 1;
            }

            _groups.Add(view);
        }
    }

    /// <summary>The thumbnail is stored under the hash known at analysis time: the full one (import) or the quick one (archive).</summary>
    private string? ThumbnailOf(Asset asset) =>
        new[] { asset.FullHash, asset.QuickHash }.OfType<string>().Select(_host.Thumbnails.PathFor).FirstOrDefault(File.Exists);

    private async Task<int> CountNewPeopleAsync(HashSet<long> assets)
    {
        var count = 0;
        foreach (var person in await _host.Catalog.People.ListPersonsAsync(CancellationToken.None))
        {
            if (person.Person.Name is null && !person.Person.Ignored
                && (await _host.Catalog.People.ListFacesOfPersonAsync(person.Person.Id, CancellationToken.None)).Any(f => assets.Contains(f.AssetId)))
            {
                count++;
            }
        }

        return count;
    }

    partial void OnSelectedChanged(ReviewItemViewModel? oldValue, ReviewItemViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
            if (Rows.OfType<ReviewRowViewModel>().FirstOrDefault(r => r.Items.Contains(newValue)) is { } row)
            {
                RowFocusRequested?.Invoke(this, row);
            }
        }

        foreach (var item in CompareItems)
        {
            item.IsFocused = item.Photo == newValue;
        }

        _ = ShowDetailsAsync(newValue);
        _ = Editor.LoadAsync(newValue?.AnalyzedAssetId);
        if (IsLargePhoto)
        {
            _ = LoadLargeImageAsync(newValue);
        }
    }

    private async Task ShowDetailsAsync(ReviewItemViewModel? value)
    {
        SelectedSignals.Clear();
        SelectedPreview = null;
        if (value is null)
        {
            return;
        }

        var profileId = value.Group?.ProfileId ?? _profileId;
        var profile = _host.Profiles.Get(profileId);
        if (value.Signals is { } s)
        {
            var t = profile.Thresholds;
            SelectedSignals.Add(new(Strings.Get("Signal.Sharpness." + s.Zone), s.ZoneSharpness, t.BlurReview, s.ZoneSharpness.ToString("0.00", CultureInfo.CurrentCulture)));
            SelectedSignals.Add(new(Strings.Get("Signal.SharpnessFrame"), s.FrameSharpness, t.BlurDiscard, s.FrameSharpness.ToString("0.00", CultureInfo.CurrentCulture)));
            // Exposure: the zone the decision uses (named, as for sharpness), and the whole photo next to it — a small
            // AF area can be well exposed in a photo full of blown highlights (Z5 II, 15 Oct 2026).
            SelectedSignals.Add(new(Strings.Get("Signal.Highlights." + s.Zone), s.ZoneHighlights, t.HighlightsReview, s.ZoneHighlights.ToString("P0", CultureInfo.CurrentCulture)));
            if (s.Zone != SharpnessZoneKind.Tiles)
            {
                SelectedSignals.Add(new(Strings.Get("Signal.Highlights.Frame"), s.FrameHighlights, null, s.FrameHighlights.ToString("P0", CultureInfo.CurrentCulture)));
            }

            SelectedSignals.Add(new(Strings.Get("Signal.Shadows." + s.Zone), s.ZoneShadows, t.ShadowsReview, s.ZoneShadows.ToString("P0", CultureInfo.CurrentCulture)));
            if (s.Zone != SharpnessZoneKind.Tiles)
            {
                SelectedSignals.Add(new(Strings.Get("Signal.Shadows.Frame"), s.FrameShadows, null, s.FrameShadows.ToString("P0", CultureInfo.CurrentCulture)));
            }

            SelectedSignals.Add(new(Strings.Get("Signal.Brightness"), s.FrameBrightness, null, s.FrameBrightness.ToString("P0", CultureInfo.CurrentCulture)));
            if (s.MainFaceLeftEyeRatio is { } left && s.MainFaceRightEyeRatio is { } right)
            {
                var eyes = Math.Min(left, right);
                SelectedSignals.Add(new(Strings.Get("Signal.Eyes"), Math.Min(1, eyes / 0.4), t.EyesClosed / 0.4,
                    $"{Strings.Get("Eye." + s.MainFaceLeftEye)} / {Strings.Get("Eye." + s.MainFaceRightEye)}"));
            }
        }

        await ShowPeopleAsync(value);
        if (Selected != value)
        {
            return;
        }

        var path = value.ThumbnailPath;
        if (path is not null && File.Exists(path))
        {
            var bitmap = await Task.Run(() => LoadBitmap(path));
            if (Selected == value)
            {
                SelectedPreview = bitmap;
            }
        }
    }

    /// <summary>The people of the panel: those assigned, and those only suggested with their similarity.</summary>
    private async Task ShowPeopleAsync(ReviewItemViewModel? value)
    {
        SelectedPeople.Clear();
        if (value is null)
        {
            return;
        }

        var faces = await _host.Catalog.Analysis.GetFacesAsync(value.AnalyzedAssetId, CancellationToken.None);
        var chips = new List<PersonChipViewModel>();
        foreach (var face in faces)
        {
            if (face.PersonId is { } person)
            {
                chips.Add(new(await PersonNameAsync(person), false, string.Empty));
            }
            else if (face.SuggestedPersonId is { } suggested)
            {
                chips.Add(new(await PersonNameAsync(suggested), true, (face.Similarity ?? 0).ToString("0.00", CultureInfo.CurrentCulture)));
            }
        }

        if (Selected == value)
        {
            SelectedPeople.Clear();
            foreach (var chip in chips)
            {
                SelectedPeople.Add(chip);
            }
        }
    }

    // --- Large photo (P21) ---

    /// <summary>Space or double click: the selected photo large, with its faces; again (or Esc) back to the grid.</summary>
    [RelayCommand]
    private void ToggleLargePhoto()
    {
        if (IsLargePhoto)
        {
            CloseLargePhoto();
        }
        else
        {
            OpenLargePhoto();
        }
    }

    private void OpenLargePhoto()
    {
        if (Selected is null || IsLargePhoto)
        {
            return;
        }

        IsLargePhoto = true;
        _ = LoadLargeImageAsync(Selected);
    }

    private void CloseLargePhoto()
    {
        Editor.CancelAddFaceCommand.Execute(null);
        IsLargePhoto = false;
        LargeImage = null;
        if (Selected is { } selected && Rows.OfType<ReviewRowViewModel>().FirstOrDefault(r => r.Items.Contains(selected)) is { } row)
        {
            RowFocusRequested?.Invoke(this, row);
        }
    }

    /// <summary>The photo as large as the analysis sees it (the embedded preview of a RAW), the thumbnail meanwhile.</summary>
    private async Task LoadLargeImageAsync(ReviewItemViewModel? item)
    {
        var version = ++_largeVersion;
        if (item is null)
        {
            LargeImage = null;
            return;
        }

        if (item.ThumbnailPath is { } thumbnail && await Task.Run(() => LoadBitmap(thumbnail)) is { } quick && version == _largeVersion)
        {
            Show(quick);
        }

        var path = await AssetFiles.LocateAsync(_host.Catalog, _host.FileSystem, item.AnalyzedAssetId, CancellationToken.None);
        if (path is null)
        {
            return;
        }

        try
        {
            var preview = await _host.Previews.GetPreviewAsync(path, LargePhotoSide, CancellationToken.None);
            var bitmap = await Task.Run(() => UiBitmaps.FromImage(preview.Image));
            if (version == _largeVersion)
            {
                Show(bitmap);
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // The thumbnail stays.
        }

        void Show(Bitmap bitmap)
        {
            LargeImage = bitmap;
            Editor.ImageSize = new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        }
    }

    private async Task<string> PersonNameAsync(long personId)
    {
        if (!_personNames.TryGetValue(personId, out var name))
        {
            var person = await _host.Catalog.People.GetPersonAsync(personId, CancellationToken.None);
            name = person?.Name ?? Strings.Format("People.Unnamed", personId);
            _personNames[personId] = name;
        }

        return name;
    }

    private static Bitmap? LoadBitmap(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // --- Navigation ---

    [RelayCommand]
    private void MoveBy(string? delta)
    {
        var parts = (delta ?? "0,0").Split(',');
        Move(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    public IRelayCommand<string?> MoveCommand => MoveByCommand;

    /// <summary>← → follow the shooting order (in the comparison: its photos); ↑ ↓ go to the same column of the photo row above or below, as laid out.</summary>
    public void Move(int dx, int dy)
    {
        if (IsCompare)
        {
            if (CompareItems.Count > 0 && dx != 0)
            {
                var current = CompareItems.FirstOrDefault(c => c.Photo == Selected)?.Index ?? 0;
                Selected = CompareItems[Math.Clamp(current + dx, 0, CompareItems.Count - 1)].Photo;
            }
            else if (dy != 0)
            {
                Pan += new Vector(0, dy * 64);
            }

            return;
        }

        var visible = Visible();
        if (visible.Count == 0)
        {
            return;
        }

        if (dy == 0)
        {
            var index = Selected is null ? -1 : visible.IndexOf(Selected);
            Selected = visible[Math.Clamp(index < 0 ? 0 : index + dx, 0, visible.Count - 1)];
            return;
        }

        var rows = Rows.OfType<ReviewRowViewModel>().ToList();
        var rowIndex = Selected is null ? -1 : rows.FindIndex(r => r.Items.Contains(Selected));
        if (rowIndex < 0)
        {
            Selected = visible[0];
            return;
        }

        var column = rows[rowIndex].Items.ToList().IndexOf(Selected!);
        var target = Math.Clamp(rowIndex + dy, 0, rows.Count - 1);
        Selected = rows[target].Items[Math.Min(column, rows[target].Items.Count - 1)];
    }

    [RelayCommand]
    private void NextGroup() => JumpGroup(1);

    [RelayCommand]
    private void PreviousGroup() => JumpGroup(-1);

    private void JumpGroup(int direction)
    {
        var groups = _groups.Where(g => g.Members.Any(Visible().Contains)).ToList();
        if (groups.Count == 0)
        {
            return;
        }

        var current = Selected?.Group is { } g ? groups.IndexOf(g) : -1;
        var next = groups[Math.Clamp(current < 0 ? (direction > 0 ? 0 : groups.Count - 1) : current + direction, 0, groups.Count - 1)];
        Selected = next.Members.First(Visible().Contains);
        if (IsCompare)
        {
            OpenCompare(next.Members);
        }
    }

    [RelayCommand]
    private void SetFilter(OutcomeFilterViewModel? filter)
    {
        foreach (var f in Filters)
        {
            f.IsActive = f == filter || (filter is null && f.Outcome is null);
        }

        Rebuild();
        if (Selected is not null && !Visible().Contains(Selected))
        {
            Selected = Visible().FirstOrDefault();
        }
    }

    [RelayCommand]
    private void FilterByIndex(string? index)
    {
        if (int.TryParse(index, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < Filters.Count)
        {
            SetFilter(Filters[i]);
        }
    }

    [RelayCommand]
    private void Select(ReviewItemViewModel? item) => Selected = item;

    [RelayCommand]
    private void Resize(string? delta)
    {
        if (int.TryParse(delta, CultureInfo.InvariantCulture, out var d))
        {
            SizeIndex = Math.Clamp(SizeIndex + d, 0, CellSizes.Length - 1);
        }
    }

    [RelayCommand]
    private void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    /// <summary>N: the Numbers view of the job (board 2c).</summary>
    [RelayCommand]
    private void Numbers()
    {
        if (_navigator is not null)
        {
            _navigator.ShowPage(new NumbersViewModel(_host, _navigator, JobId));
        }
    }

    [RelayCommand]
    private void Labeling()
    {
        if (_navigator is not null)
        {
            _navigator.ShowPage(new LabelingViewModel(_host, _navigator, JobId));
        }
    }

    [RelayCommand]
    private void Escape()
    {
        if (MoveDiscardsQuestion is not null)
        {
            MoveDiscardsQuestion = null;
        }
        else if (IsConfirming)
        {
            IsConfirming = false;
        }
        else if (Editor.Escape())
        {
            // a field or the face being added closed
        }
        else if (IsLargePhoto)
        {
            CloseLargePhoto();
        }
        else if (IsCompare)
        {
            ShowGrid();
        }
    }

    // --- Decisions ---

    /// <summary>P (P1): in the grid it confirms a "best" and makes any other photo "kept"; in the comparison it makes
    /// the focused photo the best and the other valid photos of the comparison "similar".</summary>
    [RelayCommand]
    private async Task KeepAsync()
    {
        if (Selected is not { } item)
        {
            return;
        }

        if (IsCompare && CompareItems.Count > 1)
        {
            var others = CompareItems.Select(c => c.Photo).Where(p => p != item && p.Outcome != CullingOutcome.Discard).ToList();
            await ApplyAsync([(item, d => (CullingOutcome.Best, d?.Rating, d?.Label)), .. others.Select(o => (o, (Func<Decision?, (CullingOutcome?, int?, string?)>)(d => (CullingOutcome.Similar, d?.Rating, d?.Label))))]);
            return;
        }

        var outcome = item.Outcome == CullingOutcome.Best ? CullingOutcome.Best : CullingOutcome.Kept;
        await ApplyAsync([(item, d => (outcome, d?.Rating, d?.Label))]);
    }

    [RelayCommand]
    private Task DiscardAsync() =>
        Selected is { } item ? ApplyAsync([(item, d => (CullingOutcome.Discard, null, d?.Label))]) : Task.CompletedTask;

    [RelayCommand]
    private async Task RateAsync(string? stars)
    {
        if (Selected is not { } item || !int.TryParse(stars, CultureInfo.InvariantCulture, out var value))
        {
            return;
        }

        await ApplyAsync([(item, d => (d?.UserOutcome, value is >= 1 and <= 5 ? value : null, d?.Label))]);
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (!_undo.TryPop(out var entry))
        {
            return;
        }

        foreach (var (item, before) in entry)
        {
            for (var i = 0; i < item.AssetIds.Count; i++)
            {
                await _host.Catalog.Decisions.SetUserAsync(item.AssetIds[i], before[i]?.UserOutcome, before[i]?.Rating, before[i]?.Label, CancellationToken.None);
            }

            item.Decision = await _host.Catalog.Decisions.GetAsync(item.AnalyzedAssetId, CancellationToken.None);
        }

        Selected = entry[0].Item;
        AfterDecision(entry.Select(e => e.Item));
    }

    private async Task ApplyAsync(IReadOnlyList<(ReviewItemViewModel Item, Func<Decision?, (CullingOutcome? Outcome, int? Rating, string? Label)> Change)> changes)
    {
        var entry = new List<(ReviewItemViewModel, List<Decision?>)>();
        foreach (var (item, change) in changes)
        {
            var before = new List<Decision?>();
            foreach (var id in item.AssetIds)
            {
                before.Add(await _host.Catalog.Decisions.GetAsync(id, CancellationToken.None));
            }

            entry.Add((item, before));
            var (outcome, rating, label) = change(item.Decision);
            foreach (var id in item.AssetIds)
            {
                await _host.Catalog.Decisions.SetUserAsync(id, outcome, rating, label, CancellationToken.None);
            }

            // Correction with the signals of the time (spec §6, learning from the corrections).
            if (item.Decision?.AutomaticOutcome is { } automatic && outcome is { } user && user != automatic && item.Signals is { } signals)
            {
                await _host.Catalog.Decisions.AddCorrectionAsync(new Correction(0, item.AnalyzedAssetId, automatic, user, item.Group?.ProfileId ?? _profileId,
                    CullingScores.ToScores(signals), DateTimeOffset.UtcNow), CancellationToken.None);
            }

            item.Decision = await _host.Catalog.Decisions.GetAsync(item.AnalyzedAssetId, CancellationToken.None);
        }

        _undo.Push(entry);
        AfterDecision(changes.Select(c => c.Item));
    }

    private void AfterDecision(IEnumerable<ReviewItemViewModel> items)
    {
        foreach (var group in items.Select(i => i.Group).OfType<ReviewGroupViewModel>().Distinct())
        {
            group.Refresh();
        }

        RefreshCounts();
    }

    // --- Profile per group (D8) ---

    /// <summary>Shift+R: analyze the selected group again with the suggested profile (or the next one), then decide.</summary>
    [RelayCommand]
    private async Task ReanalyzeGroupAsync()
    {
        if (JobId is not { } jobId || Selected?.Group is not { } group)
        {
            return;
        }

        var profile = group.Suggestion?.ProfileId ?? NextProfile(group.ProfileId);
        StatusMessage = Strings.Format("Review.Reanalyzing", group.Title, _host.ProfileName(profile));
        await _host.ReanalyzeAsync(jobId, group.Members.SelectMany(m => m.AssetIds).ToList(), profile, CancellationToken.None);
        StatusMessage = Strings.Format("Review.Reanalyzed", group.Title, _host.ProfileName(profile));
        await ActivateAsync();
    }

    private string NextProfile(string current)
    {
        var scenes = _host.Profiles.All.Where(p => p.Id != "archive").Select(p => p.Id).ToList();
        return scenes[(scenes.IndexOf(current) + 1) % scenes.Count];
    }

    // --- Confirmation and XMP (2g, 2h) ---

    /// <summary>Ctrl+Enter: opens the confirmation, or writes when it is already open.</summary>
    [RelayCommand]
    private async Task ConfirmOrWriteAsync()
    {
        if (IsConfirming)
        {
            await WriteXmpAsync();
        }
        else
        {
            Confirm();
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (_all.Count > 0)
        {
            ResultMessage = null;
            ResultError = null;
            IsConfirming = true;
            OnPropertyChanged(nameof(ConfirmQuestion));
            OnPropertyChanged(nameof(ConfirmToReview));
            OnPropertyChanged(nameof(ConfirmCounts));
        }
    }

    [RelayCommand]
    private void CancelConfirm() => IsConfirming = false;

    [RelayCommand]
    private void ShowToReview()
    {
        IsConfirming = false;
        SetFilter(Filters.First(f => f.Outcome == CullingOutcome.ToReview));
    }

    [RelayCommand]
    private async Task WriteXmpAsync()
    {
        IsConfirming = false;
        await WriteAsync(_all.SelectMany(i => i.AssetIds).Distinct().ToList());
        if (OpenPeopleAfter && NewPeopleGroups > 0 && ResultError is null && _navigator is not null)
        {
            _navigator.ShowSection(SectionCatalog.People);
        }
    }

    /// <summary>Ctrl+R: writes again the XMP still pending (2h).</summary>
    [RelayCommand]
    private Task RetryXmpAsync() => _failedAssets.Count == 0 ? Task.CompletedTask : WriteAsync(_failedAssets.ToList());

    /// <summary>"Catalog only for these" (P4): the failed photos keep their decisions in the catalog and stop retrying.</summary>
    [RelayCommand]
    private async Task CatalogOnlyAsync()
    {
        foreach (var assetId in _failedAssets)
        {
            await _host.Catalog.XmpSync.MarkWrittenAsync(assetId, "catalog-only", CancellationToken.None);
        }

        _failedAssets = [];
        ResultError = null;
        OnPropertyChanged(nameof(HasFailures));
        await RefreshPendingAsync();
    }

    [RelayCommand]
    private void OpenPeople() => _navigator?.ShowSection(SectionCatalog.People);

    private async Task WriteAsync(IReadOnlyList<long> assets)
    {
        IsWriting = true;
        try
        {
            if (PrudentMode != _host.Settings.PrudentMode)
            {
                await _host.UpdateSettingsAsync(s => s with { PrudentMode = PrudentMode }, CancellationToken.None);
            }

            var summary = await Task.Run(() => _host.CreatePublisher().WriteAsync(assets, PrudentMode, null, CancellationToken.None));
            _failedAssets = [.. summary.FailedAssets];
            ResultMessage = Strings.Format("Review.Result.Written", DateTime.Now.ToString("d MMM yyyy · HH:mm", CultureInfo.CurrentCulture), summary.Written.ToString("N0", CultureInfo.CurrentCulture));
            ResultError = summary.Failed.Count == 0 ? null
                : Strings.Format("Review.Result.Failed", summary.Failed.Count, Path.GetDirectoryName(summary.Failed[0].Path) ?? string.Empty, summary.Failed[0].Error);
            OnPropertyChanged(nameof(HasFailures));
            await RefreshPendingAsync();
        }
        finally
        {
            IsWriting = false;
        }
    }

    private async Task RefreshPendingAsync()
    {
        foreach (var item in _all)
        {
            item.XmpPending = (await _host.Catalog.XmpSync.GetAsync(item.AnalyzedAssetId, CancellationToken.None))?.WritePending == true;
        }

        PendingXmp = _all.Count(i => i.XmpPending);
    }

    // --- Moving the discards (P6) ---

    [RelayCommand]
    private void RequestMoveDiscards()
    {
        var count = Count(CullingOutcome.Discard);
        MoveDiscardsQuestion = count == 0 ? null : Strings.Format("Review.MoveDiscards.Question", count.ToString("N0", CultureInfo.CurrentCulture));
        if (count == 0)
        {
            StatusMessage = Strings.Get("Review.MoveDiscards.None");
        }
    }

    [RelayCommand]
    private async Task MoveDiscardsAsync()
    {
        MoveDiscardsQuestion = null;
        var discards = _all.Where(i => i.Outcome == CullingOutcome.Discard).SelectMany(i => i.AssetIds).Distinct().ToList();
        var result = await Task.Run(() => new DiscardMover(_host.Catalog, _host.FileSystem).MoveAsync(discards, CancellationToken.None));
        StatusMessage = Strings.Format("Review.MoveDiscards.Done", result.Moved, result.Skipped);
    }

    // --- Comparison (2b) ---

    /// <summary>C: compares the burst of the selected photo (its 4 best), or the photos marked with Shift+click.</summary>
    [RelayCommand]
    private void Compare()
    {
        var marked = _all.Where(i => i.IsCompared).ToList();
        if (marked.Count >= 2)
        {
            OpenCompare(marked);
        }
        else if (Selected?.Group is { } group)
        {
            OpenCompare(group.Members.OrderBy(m => m.Rank ?? int.MaxValue).ToList());
        }
        else if (Selected is { } item)
        {
            var index = _all.IndexOf(item);
            OpenCompare(_all.Skip(Math.Max(0, index - 1)).Take(2).ToList());
        }
    }

    /// <summary>Shift+click in the grid: adds or removes a photo from the comparison (2-4).</summary>
    public void ToggleCompared(ReviewItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsCompared && _all.Count(i => i.IsCompared) >= 4)
        {
            return;
        }

        item.IsCompared = !item.IsCompared;
    }

    private void OpenCompare(IReadOnlyList<ReviewItemViewModel> photos)
    {
        if (IsLargePhoto)
        {
            CloseLargePhoto();
        }

        CompareItems.Clear();
        foreach (var (photo, index) in photos.Take(4).Select((p, i) => (p, i)))
        {
            var item = new CompareItemViewModel(photo, index) { IsFocused = photo == Selected };
            CompareItems.Add(item);
            _ = LoadPreviewAsync(item);
        }

        if (!CompareItems.Any(c => c.IsFocused) && CompareItems.Count > 0)
        {
            Selected = CompareItems[0].Photo;
        }

        Zoom = null;
        Pan = default;
        Mode = ReviewMode.Compare;
    }

    private async Task LoadPreviewAsync(CompareItemViewModel item)
    {
        var path = await AssetFiles.LocateAsync(_host.Catalog, _host.FileSystem, item.Photo.AnalyzedAssetId, CancellationToken.None);
        Bitmap? bitmap = null;
        if (path is not null)
        {
            try
            {
                var preview = await _host.Previews.GetPreviewAsync(path, CullingSignals.ReferenceLongSide, CancellationToken.None);
                bitmap = await Task.Run(() => UiBitmaps.FromImage(preview.Image));
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                bitmap = null;
            }
        }

        item.Preview = bitmap ?? (item.Photo.ThumbnailPath is { } thumb ? await Task.Run(() => LoadBitmap(thumb)) : null);
    }

    [RelayCommand]
    private void ShowGrid()
    {
        if (IsLargePhoto)
        {
            CloseLargePhoto();
        }

        Mode = ReviewMode.Grid;
        CompareItems.Clear();
        foreach (var item in _all.Where(i => i.IsCompared))
        {
            item.IsCompared = false;
        }

        if (Selected is { } selected && Rows.OfType<ReviewRowViewModel>().FirstOrDefault(r => r.Items.Contains(selected)) is { } row)
        {
            RowFocusRequested?.Invoke(this, row);
        }
    }

    /// <summary>Z: 100 % (one preview pixel per screen pixel) or back to fit.</summary>
    [RelayCommand]
    private void ZoomToggle()
    {
        if (IsCompare)
        {
            Zoom = Zoom is null ? 1.0 : null;
            Pan = default;
        }
    }

    public IRelayCommand ZoomCommand => ZoomToggleCommand;

    [RelayCommand]
    private void Fit()
    {
        Zoom = null;
        Pan = default;
    }

    [RelayCommand]
    private void ToggleSync() => IsZoomSynced = !IsZoomSynced;

    // --- Grid ---

    private List<ReviewItemViewModel> Visible()
    {
        var filter = Filters.FirstOrDefault(f => f.IsActive)?.Outcome;
        return filter is null || IsAnalyzing ? _all : _all.Where(i => i.Outcome == filter).ToList();
    }

    /// <summary>Rows in shooting order: each group under its header, the single shots in plain rows.</summary>
    private void Rebuild()
    {
        Rows.Clear();
        var visible = Visible();
        var pending = new List<ReviewItemViewModel>();
        var emitted = new HashSet<ReviewGroupViewModel>();
        void Flush()
        {
            foreach (var chunk in pending.Chunk(_columns))
            {
                Rows.Add(new ReviewRowViewModel(chunk));
            }

            pending.Clear();
        }

        foreach (var photo in visible)
        {
            if (photo.Group is { } group && !IsAnalyzing)
            {
                if (!emitted.Add(group))
                {
                    continue;
                }

                Flush();
                Rows.Add(new ReviewHeaderRow(group));
                foreach (var chunk in group.Members.Where(visible.Contains).Chunk(_columns))
                {
                    Rows.Add(new ReviewRowViewModel(chunk));
                }
            }
            else
            {
                pending.Add(photo);
            }
        }

        Flush();
        RefreshCounts();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void RefreshCounts()
    {
        foreach (var filter in Filters)
        {
            filter.Count = filter.Outcome is null ? _all.Count : Count(filter.Outcome.Value);
        }

        ManualCount = _all.Count(i => i.IsManual);
        PendingXmp = _all.Count(i => i.XmpPending);
    }

    private int Count(CullingOutcome outcome) => _all.Count(i => i.Outcome == outcome);

    private void OnJobStatus(object? sender, JobStatus status) => Ui.Post(() =>
    {
        if (status.JobId != JobId)
        {
            return;
        }

        var p = status.Progress;
        AnalysisProgress = Strings.Format("Review.Analyzing", p.Completed.ToString("N0", CultureInfo.CurrentCulture), p.Total.ToString("N0", CultureInfo.CurrentCulture),
            status.Remaining is { } r ? Formats.Duration(r) : "…");
        if (status.State != JobState.Running && IsAnalyzing)
        {
            _ = ActivateAsync();
        }
    });

    public void Dispose() => _host.Jobs.StatusChanged -= OnJobStatus;

    private static CullingSignals? SafeSignals(IReadOnlyDictionary<string, double> scores)
    {
        try
        {
            return CullingScores.FromScores(scores);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }
}
