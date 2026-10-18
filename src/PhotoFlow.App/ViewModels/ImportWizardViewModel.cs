// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Sources;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Sources.Scanning;
using PhotoFlow.Tagging.Ocr;

namespace PhotoFlow.App.ViewModels;

/// <summary>A scene profile tile of step 2 ("Eventi · AF → volti").</summary>
/// <summary>An entry list offered by the import (null: no list).</summary>
public sealed record EntryListChoice(long? Id, string Name);

public sealed record ProfileChoice(SceneProfile Profile)
{
    public string Name => NameOf(Profile);

    public string Zone => Profile.IsBuiltIn ? Strings.Get("Profile." + Profile.Id + ".Zone")
        : Strings.Format("Profiles.From", Profile.BasedOn is { } b ? Strings.Get("Profile." + b) : "—");

    /// <summary>Built-in profiles take their name from the resources, custom ones have their own.</summary>
    public static string NameOf(SceneProfile? profile) => profile is null ? string.Empty
        : profile.IsBuiltIn ? Strings.Get("Profile." + profile.Id) : profile.Name;
}

/// <summary>A destination of step 3 with its space check (board 3f).</summary>
public sealed partial class DestinationViewModel(IngestDestination destination, int index) : ObservableObject
{
    public IngestDestination Destination { get; } = destination;

    public int Index { get; } = index;

    public string Name => Strings.Get(Destination.Kind switch
    {
        DestinationKind.Primary => "Import.Destination.Primary",
        DestinationKind.Secondary => "Import.Destination.Secondary",
        _ => "Import.Destination.Deferred",
    });

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _state = string.Empty;

    public bool CanRemove => Destination.Kind != DestinationKind.Primary;
}

/// <summary>
/// The import procedure (boards 3d-3f): source and event, analysis, destinations and XMP. Every choice starts from
/// what the last import used; "Start now" is available from step 1. The card is only read (I1).
/// </summary>
public sealed partial class ImportWizardViewModel : PageViewModel
{
    private readonly PhotoFlowHost _host;
    private readonly IShellNavigator _navigator;
    private readonly CardVolume? _card;
    private readonly string _sourceRoot;
    private List<ScannedFile> _scanned = [];
    private ImportPlan? _plan;

    public ImportWizardViewModel(PhotoFlowHost host, IShellNavigator navigator, CardVolume? card, string? folder)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(navigator);
        _host = host;
        _navigator = navigator;
        _card = card;
        _sourceRoot = card?.RootPath ?? folder ?? throw new ArgumentException("A card or a folder is required.");
        var defaults = host.Settings.Import;
        Profiles = host.Profiles.All.Select(p => new ProfileChoice(p)).ToList();
        _selectedProfile = Profiles.FirstOrDefault(p => p.Profile.Id == defaults.ProfileId) ?? Profiles[0];
        var setup = host.DescribeAnalysis();
        CanRecognize = setup.FaceRecognizer is not null;
        CanAutomaticProfile = setup.Embedder is not null;
        _faces = defaults.Faces == FaceMode.Off ? FaceMode.Off : defaults.Faces == FaceMode.Recognize && CanRecognize ? FaceMode.Recognize : FaceMode.DetectOnly;
        _automaticProfile = defaults.AutomaticProfile && CanAutomaticProfile;
        CanTagA = setup.Embedder is not null;
        CanTagB = host.Models.IsInstalled(PhotoFlowHost.RamPlusModelId);
        var levels = defaults.Tags ?? (setup.Backend == Core.Inference.ExecutionBackend.Cuda ? Core.Tagging.TagLevels.Default : Core.Tagging.TagLevels.ZeroShot);
        _tagsA = CanTagA && levels.HasFlag(Core.Tagging.TagLevels.ZeroShot);
        _tagsB = CanTagB && levels.HasFlag(Core.Tagging.TagLevels.Tagger);
        CanTagC = host.Models.IsInstalled(PhotoFlowHost.CaptionModelId);
        _tagsC = CanTagC && levels.HasFlag(Core.Tagging.TagLevels.Caption);
        CanTagD = host.VlmAvailable;
        _tagsD = CanTagD && levels.HasFlag(Core.Tagging.TagLevels.VlmCaption);
        _xmpPolicy = defaults.XmpPolicy;
        RecentEvents = defaults.RecentEvents;
        _eventName = string.Empty;
    }

    public override string Title => Strings.Get("Import.Title");

    public const string NextCommandId = "Import.Next";
    public const string StartCommandId = "Import.Start";
    public const string BackCommandId = "Import.Back";
    public const string CancelCommandId = "Import.Cancel";

    private static readonly CommandTable Table = new(
    [
        new(NextCommandId, new KeyGesture(Key.Enter), "Import.Command.Next", StatusGroupKey: "Import.Command.Next"),
        new(StartCommandId, new KeyGesture(Key.Enter, KeyModifiers.Control), "Import.Command.StartNow", StatusGroupKey: "Import.Command.StartNow"),
        new(BackCommandId, new KeyGesture(Key.Left, KeyModifiers.Alt), "Import.Command.Back", StatusGroupKey: "Import.Command.Back"),
        new(CancelCommandId, new KeyGesture(Key.Escape), "Import.Command.Cancel", StatusGroupKey: "Import.Command.Cancel"),
    ]);

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id switch
    {
        NextCommandId => NextOrStartCommand,
        StartCommandId => StartCommand,
        BackCommandId => BackCommand,
        CancelCommandId => CancelCommand,
        _ => base.ResolveCommand(id),
    };

    /// <summary>Enter: next step, or start on the last one.</summary>
    [RelayCommand]
    private async Task NextOrStartAsync()
    {
        if (IsLastStep)
        {
            await StartAsync();
        }
        else
        {
            Next();
        }
    }

    public IReadOnlyList<ProfileChoice> Profiles { get; }

    public IReadOnlyList<string> RecentEvents { get; }

    public ObservableCollection<DestinationViewModel> Destinations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(StepText), nameof(CanGoBack), nameof(IsLastStep))]
    private int _step = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaded))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathPreview), nameof(CanStart), nameof(SummaryEvent))]
    private string _eventName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathPreview), nameof(SummaryEvent))]
    private DateTime? _eventDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCount), nameof(SummarySource), nameof(StartLabel), nameof(CanStart))]
    private ImportSelection _selection = ImportSelection.OnlyNew;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryProfile), nameof(ReadsNumbers))]
    private ProfileChoice _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryFaces), nameof(FacesOff), nameof(FacesDetect), nameof(FacesRecognize))]
    private FaceMode _faces;

    public bool CanTagA { get; }

    public bool CanTagB { get; }

    /// <summary>Level A: personal CLIP tags (needs CLIP).</summary>
    [ObservableProperty]
    private bool _tagsA;

    /// <summary>Level B: RAM++ (on CPU only it is off by default: about a second per photo, spec §5).</summary>
    [ObservableProperty]
    private bool _tagsB;

    /// <summary>Level C: Florence-2 captions (optional, off by default; about a second per photo on the GPU).</summary>
    [ObservableProperty]
    private bool _tagsC;

    public bool CanTagC { get; }

    /// <summary>Level D: Italian captions of the VLM (phase 5b; off by default, a few seconds per photo).</summary>
    [ObservableProperty]
    private bool _tagsD;

    /// <summary>The VLM is converted and the GPU has the 12 GB it needs (P16).</summary>
    public bool CanTagD { get; }

    public Core.Tagging.TagLevels TagLevels =>
        (TagsA ? Core.Tagging.TagLevels.ZeroShot : 0) | (TagsB ? Core.Tagging.TagLevels.Tagger : 0) | (TagsC ? Core.Tagging.TagLevels.Caption : 0)
        | (TagsD ? Core.Tagging.TagLevels.VlmCaption : 0);

    /// <summary>A face recognition model is installed (SFace, or buffalo_l with its license accepted).</summary>
    public bool CanRecognize { get; }

    /// <summary>The CLIP model is installed: the profile can be chosen per group (D8).</summary>
    public bool CanAutomaticProfile { get; }

    /// <summary>Automatic profile per group; the selected profile is the fallback below the confidence threshold.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryProfile), nameof(ReadsNumbers))]
    private bool _automaticProfile;

    /// <summary>The race numbers are read (spec §8): the profile asks for the OCR, or a group may get Motorsport.</summary>
    public bool ReadsNumbers => SelectedProfile.Profile.Ocr || AutomaticProfile;

    /// <summary>Entry lists of the catalog; the first item (null) is "no list".</summary>
    public ObservableCollection<EntryListChoice> EntryLists { get; } = [];

    /// <summary>The entry list of the job: numbers outside it are ignored; also chosen later in the Numbers view.</summary>
    [ObservableProperty]
    private EntryListChoice? _selectedEntryList;

    /// <summary>Raised when the view should open the file picker of an entry list.</summary>
    public event EventHandler? ImportEntryListRequested;

    [RelayCommand]
    private void ImportEntryList() => ImportEntryListRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>An entry list from a CSV or Excel file, saved in the catalog and chosen for this import.</summary>
    public async Task ImportEntryListAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var entries = await EntryListImporter.ReadAsync(path, CancellationToken.None);
            if (entries.Count == 0)
            {
                EntryListMessage = Strings.Format("Numbers.List.ImportEmpty", Path.GetFileName(path));
                return;
            }

            var id = await _host.Catalog.Motorsport.CreateListAsync(Path.GetFileNameWithoutExtension(path), path, CancellationToken.None);
            await _host.Catalog.Motorsport.ReplaceEntriesAsync(id, entries, CancellationToken.None);
            var choice = new EntryListChoice(id, Strings.Format("Import.EntryList.Item", Path.GetFileNameWithoutExtension(path), entries.Count.ToString(CultureInfo.CurrentCulture)));
            EntryLists.Insert(1, choice);
            SelectedEntryList = choice;
            EntryListMessage = null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            EntryListMessage = Strings.Format("Numbers.List.ImportFailed", Path.GetFileName(path), e.Message);
        }
    }

    [ObservableProperty]
    private string? _entryListMessage;

    private async Task LoadEntryListsAsync()
    {
        EntryLists.Clear();
        EntryLists.Add(new EntryListChoice(null, Strings.Get("Import.EntryList.None")));
        foreach (var list in (await _host.Catalog.Motorsport.ListListsAsync(CancellationToken.None)).OrderByDescending(l => l.CreatedAt))
        {
            var count = (await _host.Catalog.Motorsport.GetEntriesAsync(list.Id, CancellationToken.None)).Count;
            EntryLists.Add(new EntryListChoice(list.Id, Strings.Format("Import.EntryList.Item", list.Name, count.ToString(CultureInfo.CurrentCulture))));
        }

        SelectedEntryList ??= EntryLists[0];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyEmbedded), nameof(PolicySidecar), nameof(PolicyCatalog))]
    private WritePolicy _xmpPolicy;

    [ObservableProperty]
    private string _sourceTitle = string.Empty;

    [ObservableProperty]
    private string _sourceDetail = string.Empty;

    [ObservableProperty]
    private string _captureRange = string.Empty;

    [ObservableProperty]
    private string? _error;

    public bool IsLoaded => !IsLoading;

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

    public bool IsStep3 => Step == 3;

    public bool IsLastStep => Step == 3;

    public bool CanGoBack => Step > 1;

    public string StepText => Strings.Format("Import.StepOf", Step, 3);

    public int NewCount => _plan?.Selected(ImportSelection.OnlyNew).Count(f => f.File.IsAnalyzable || f.File.Kind == MediaKind.Video) ?? 0;

    public int AllCount => _plan?.Files.Count ?? 0;

    public string NewCountText => (_plan?.NewPhotos ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    public string AllCountText => ((_plan?.NewPhotos ?? 0) + (_plan?.AlreadyImportedPhotos ?? 0)).ToString("N0", CultureInfo.CurrentCulture);

    public string AllLabel => Strings.Format("Import.Selection.All", (_plan?.AlreadyImportedPhotos ?? 0).ToString("N0", CultureInfo.CurrentCulture));

    public string PairsNote => Strings.Format("Import.Note.Pairs", (_plan?.RawJpegPairs ?? 0).ToString("N0", CultureInfo.CurrentCulture));

    public string VideosNote => Strings.Format("Import.Note.Videos", (_plan?.Videos ?? 0).ToString("N0", CultureInfo.CurrentCulture));

    public bool SelectOnlyNew
    {
        get => Selection == ImportSelection.OnlyNew;
        set => Selection = value ? ImportSelection.OnlyNew : ImportSelection.All;
    }

    public bool SelectAll
    {
        get => Selection == ImportSelection.All;
        set => Selection = value ? ImportSelection.All : ImportSelection.OnlyNew;
    }

    public int SelectedCount => _plan?.Selected(Selection).Count ?? 0;

    public bool FacesOff
    {
        get => Faces == FaceMode.Off;
        set
        {
            if (value)
            {
                Faces = FaceMode.Off;
            }
        }
    }

    public bool FacesDetect
    {
        get => Faces == FaceMode.DetectOnly;
        set
        {
            if (value)
            {
                Faces = FaceMode.DetectOnly;
            }
        }
    }

    public bool FacesRecognize
    {
        get => Faces == FaceMode.Recognize;
        set
        {
            if (value && CanRecognize)
            {
                Faces = FaceMode.Recognize;
            }
        }
    }

    public bool PolicyEmbedded
    {
        get => XmpPolicy == WritePolicy.EmbeddedForNonRaw;
        set => SetPolicy(value, WritePolicy.EmbeddedForNonRaw);
    }

    public bool PolicySidecar
    {
        get => XmpPolicy == WritePolicy.SidecarOnly;
        set => SetPolicy(value, WritePolicy.SidecarOnly);
    }

    public bool PolicyCatalog
    {
        get => XmpPolicy == WritePolicy.CatalogOnly;
        set => SetPolicy(value, WritePolicy.CatalogOnly);
    }

    public string PathPreview
    {
        get
        {
            var root = _host.Settings.Import.PrimaryRoot ?? string.Empty;
            var first = _plan?.Selected(Selection).FirstOrDefault();
            if (first is null)
            {
                return root;
            }

            var date = EventDate is { } d ? DateOnly.FromDateTime(d.Date) : _plan!.ProposedEventDate;
            var relative = _host.PathTemplate.Render(new PathTemplateContext(
                EventNameOrDefault, date, 1, first.Exif?.Model, Path.GetFileNameWithoutExtension(first.File.Item.RelativePath), Path.GetExtension(first.File.Item.RelativePath).TrimStart('.')));
            return Path.Combine(root, relative);
        }
    }

    public string StartLabel => Strings.Format("Import.Start", SelectedCount.ToString("N0", CultureInfo.CurrentCulture));

    public bool CanStart => _plan is not null && SelectedCount > 0 && Destinations.All(d => d.Error is null) && _host.Settings.Import.PrimaryRoot is not null;

    public string SummarySource => Strings.Format("Import.Summary.Source", SourceTitle, SelectedCount.ToString("N0", CultureInfo.CurrentCulture));

    public string SummaryEvent => $"{EventNameOrDefault} · {(EventDate ?? DateTime.Today).ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";

    public string SummaryProfile => AutomaticProfile ? Strings.Format("Import.Summary.AutomaticProfile", SelectedProfile.Name) : SelectedProfile.Name;

    public string SummaryFaces => Strings.Get("Import.Faces." + Faces);

    public string SummaryCopies
    {
        get
        {
            var local = Destinations.Count(d => d.Destination.Kind != DestinationKind.Deferred);
            var nas = Destinations.Count(d => d.Destination.Kind == DestinationKind.Deferred);
            return nas == 0 ? local.ToString(CultureInfo.CurrentCulture) : Strings.Format("Import.Summary.Copies", local, nas);
        }
    }

    private string EventNameOrDefault => string.IsNullOrWhiteSpace(EventName) ? Strings.Get("Import.Event.Default") : EventName.Trim();

    public override async Task ActivateAsync()
    {
        if (_plan is not null)
        {
            return;
        }

        await LoadEntryListsAsync();

        IsLoading = true;
        try
        {
            _scanned = await Task.Run(async () =>
            {
                var files = new List<ScannedFile>();
                await foreach (var file in new SourceScanner(_host.FileSystem, _sourceRoot).ScanAsync(CancellationToken.None))
                {
                    files.Add(file);
                }

                return files;
            });
            _plan = await _host.Planner.PlanAsync(_scanned, string.Empty, null, _host.PathTemplate, CancellationToken.None);
            EventDate = _plan.ProposedEventDate.ToDateTime(TimeOnly.MinValue);
            SourceTitle = _card is { } card ? Strings.Format("Import.Source.Card", card.Label, card.RootPath.TrimEnd('\\', '/')) : _sourceRoot;
            SourceDetail = Strings.Format("Import.Source.Detail", _plan.Files.Count.ToString("N0", CultureInfo.CurrentCulture), Formats.Bytes(_plan.TotalBytes));
            CaptureRange = _plan.FirstCapture is { } f && _plan.LastCapture is { } l ? Strings.Format("Import.Event.FromShots", Formats.CaptureRange(f, l)) : string.Empty;
            BuildDestinations();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = Strings.Format("Import.Error.Source", e.Message);
        }
        finally
        {
            IsLoading = false;
            RaiseAll();
        }
    }

    [RelayCommand]
    private void Next()
    {
        if (Step < 3)
        {
            Step++;
            if (Step == 3)
            {
                CheckSpace();
            }
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 1)
        {
            Step--;
        }
    }

    [RelayCommand]
    private void Cancel() => _navigator.ShowSection(SectionCatalog.Home);

    [RelayCommand]
    private void UseRecentEvent(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            EventName = name;
        }
    }

    /// <summary>Adds a destination chosen by the user: network paths become deferred copies (NAS), others secondary.</summary>
    public void AddDestination(string root)
    {
        var kind = root.StartsWith(@"\\", StringComparison.Ordinal) ? DestinationKind.Deferred : DestinationKind.Secondary;
        Destinations.Add(new DestinationViewModel(new IngestDestination($"{kind}-{Destinations.Count}", root, kind), Destinations.Count + 1));
        CheckSpace();
        RaiseAll();
    }

    [RelayCommand]
    private void RemoveDestination(DestinationViewModel? destination)
    {
        if (destination is { CanRemove: true })
        {
            Destinations.Remove(destination);
            RaiseAll();
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        CheckSpace();
        if (!CanStart || _plan is null)
        {
            Step = 3;
            return;
        }

        var name = EventNameOrDefault;
        var date = EventDate is { } d ? DateOnly.FromDateTime(d.Date) : _plan.ProposedEventDate;
        var plan = await _host.Planner.PlanAsync(_scanned, name, date, _host.PathTemplate, CancellationToken.None);
        var destinations = Destinations.Select(v => v.Destination).ToList();
        var jobId = await ImportJobs.CreateAsync(_host.Catalog, plan, Selection, _sourceRoot, destinations, name, date, SelectedProfile.Profile.Id, Faces, CancellationToken.None, TagLevels);
        if (AutomaticProfile)
        {
            await _host.Catalog.Jobs.SetAutomaticProfileAsync(jobId, true, SelectedProfile.Profile.Id, CancellationToken.None);
        }

        if (ReadsNumbers && SelectedEntryList?.Id is { } listId)
        {
            await _host.Catalog.Motorsport.SetJobListAsync(jobId, listId, CancellationToken.None);
        }

        await _host.UpdateSettingsAsync(s => s with
        {
            Import = s.Import.WithRecentEvent(name) with
            {
                ProfileId = SelectedProfile.Profile.Id,
                AutomaticProfile = AutomaticProfile,
                Tags = TagLevels,
                Faces = Faces,
                XmpPolicy = XmpPolicy,
                SecondaryRoots = destinations.Where(x => x.Kind == DestinationKind.Secondary).Select(x => x.RootPath).ToList(),
                DeferredRoots = destinations.Where(x => x.Kind == DestinationKind.Deferred).Select(x => x.RootPath).ToList(),
            },
        }, CancellationToken.None);
        _ = _host.Jobs.StartAsync(jobId);
        _navigator.ShowSection(SectionCatalog.Jobs);
    }

    private void BuildDestinations()
    {
        Destinations.Clear();
        var defaults = _host.Settings.Import;
        if (defaults.PrimaryRoot is { } primary)
        {
            Destinations.Add(new DestinationViewModel(new IngestDestination("primary", primary, DestinationKind.Primary), 1));
        }

        foreach (var root in defaults.SecondaryRoots)
        {
            Destinations.Add(new DestinationViewModel(new IngestDestination($"secondary-{Destinations.Count}", root, DestinationKind.Secondary), Destinations.Count + 1));
        }

        foreach (var root in defaults.DeferredRoots)
        {
            Destinations.Add(new DestinationViewModel(new IngestDestination($"deferred-{Destinations.Count}", root, DestinationKind.Deferred), Destinations.Count + 1));
        }

        CheckSpace();
    }

    /// <summary>Free space and reachability of each destination (board 3f): an error blocks the start.</summary>
    private void CheckSpace()
    {
        if (_plan is null)
        {
            return;
        }

        var spaces = _host.Planner.CheckSpace(_plan, Selection, Destinations.Select(d => d.Destination).ToList());
        foreach (var destination in Destinations)
        {
            if (destination.Destination.Kind == DestinationKind.Deferred)
            {
                var reachable = SafeExists(destination.Destination.RootPath);
                destination.Detail = destination.Destination.RootPath;
                destination.Error = null;
                destination.State = Strings.Get(reachable ? "Import.Destination.Queued" : "Import.Destination.Unreachable");
                continue;
            }

            var space = spaces.FirstOrDefault(s => s.Destination.Id == destination.Destination.Id);
            destination.Detail = space is null ? destination.Destination.RootPath
                : Strings.Format("Import.Destination.Free", destination.Destination.RootPath, Formats.Bytes(space.AvailableBytes));
            destination.Error = space is { IsSufficient: false }
                ? Strings.Format("Import.Destination.NoSpace", Formats.Bytes(space.RequiredBytes))
                : null;
            destination.State = Strings.Get(destination.Error is null ? "Import.Destination.Ready" : "Import.Destination.Error");
        }

        RaiseAll();
    }

    private bool SafeExists(string path)
    {
        try
        {
            return _host.FileSystem.DirectoryExists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SetPolicy(bool value, WritePolicy policy)
    {
        if (value)
        {
            XmpPolicy = policy;
        }
    }

    partial void OnSelectionChanged(ImportSelection value)
    {
        OnPropertyChanged(nameof(SelectOnlyNew));
        OnPropertyChanged(nameof(SelectAll));
        CheckSpace();
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(NewCount), nameof(AllCount), nameof(NewCountText), nameof(AllCountText), nameof(AllLabel), nameof(PairsNote), nameof(VideosNote),
            nameof(SelectedCount), nameof(PathPreview), nameof(StartLabel), nameof(CanStart), nameof(SummarySource), nameof(SummaryCopies),
        })
        {
            OnPropertyChanged(name);
        }
    }
}
