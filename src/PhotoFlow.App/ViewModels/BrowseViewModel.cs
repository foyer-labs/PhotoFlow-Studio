// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Search;
using PhotoFlow.Faces.Recognition;

namespace PhotoFlow.App.ViewModels;

/// <summary>Bitmaps for the UI: from a decoded preview or from a thumbnail file.</summary>
public static class UiBitmaps
{
    public static Bitmap FromImage(RgbImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var pixels = image.Pixels.Span;
        var bgra = new byte[image.Width * image.Height * 4];
        for (int s = 0, d = 0; s < pixels.Length; s += 3, d += 4)
        {
            bgra[d] = pixels[s + 2];
            bgra[d + 1] = pixels[s + 1];
            bgra[d + 2] = pixels[s];
            bgra[d + 3] = 255;
        }

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new PixelSize(image.Width, image.Height), new Vector(96, 96), image.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    public static Bitmap? FromFile(string path, int width)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public enum SuggestionKind
{
    Person,
    Tag,
    Event,
}

/// <summary>A suggestion of the search field: becomes an exact filter (a token).</summary>
public sealed record SearchSuggestion(SuggestionKind Kind, long Id, string Label, string Detail)
{
    public string KindLabel => Strings.Get("Browse.Kind." + Kind);

    public override string ToString() => Label;
}

/// <summary>A photo of the results grid; the thumbnail loads when the row becomes visible.</summary>
public sealed partial class BrowseItemViewModel(long assetId, string? thumbnailPath, string stars) : ObservableObject
{
    private Bitmap? _thumbnail;
    private bool _loading;

    public long AssetId { get; } = assetId;

    public string Stars { get; } = stars;

    [ObservableProperty]
    private bool _isSelected;

    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_loading && thumbnailPath is { } path)
            {
                _loading = true;
                _ = Task.Run(() =>
                {
                    var bitmap = UiBitmaps.FromFile(path, 320);
                    Ui.Post(() =>
                    {
                        _thumbnail = bitmap;
                        OnPropertyChanged(nameof(Thumbnail));
                    });
                });
            }

            return _thumbnail;
        }
    }
}

public sealed record BrowseRow(IReadOnlyList<BrowseItemViewModel> Items);

/// <summary>A face box on the detail preview (normalized coordinates scaled to the preview).</summary>
/// <summary>
/// A face on a shown photo, in the pixels of the image (spec §7): its name, or "?" when not recognized. The lines and
/// the label follow the size of the image (the view scales it down); the current face of a person page stands out.
/// </summary>
public sealed record FaceBoxViewModel(double X, double Y, double Width, double Height, string? Name, bool IsCurrent = false, double ImageSide = 600, bool Dim = false,
    long FaceId = 0, long? PersonId = null)
{
    public string Label => Name ?? "?";

    private double Unit => Math.Max(1, ImageSide / 400);

    public Thickness Stroke => new((IsCurrent ? 3 : 2) * Unit);

    public double LabelSize => Math.Max(11, ImageSide / 45);

    public Thickness LabelPadding => new(LabelSize / 3, LabelSize / 10);

    public double BoxOpacity => Dim ? 0.55 : 1;

    /// <summary>The faces of a photo shown at <paramref name="width"/> × <paramref name="height"/>; ignored people are left out.</summary>
    public static List<FaceBoxViewModel> From(IReadOnlyList<Face> faces, IReadOnlyDictionary<long, Person> persons, double width, double height, long? current = null)
    {
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(persons);
        var side = Math.Max(width, height);
        return [.. faces
            .Where(f => f.PersonId is not { } id || !persons.TryGetValue(id, out var p) || !p.Ignored)
            .Select(f => new FaceBoxViewModel(f.Box.X1 * width, f.Box.Y1 * height, (f.Box.X2 - f.Box.X1) * width, (f.Box.Y2 - f.Box.Y1) * height,
                f.PersonId is { } id && persons.TryGetValue(id, out var person) ? person.Name : null,
                f.Id == current, side, current is not null && f.Id != current, f.Id, f.PersonId))];
    }

    /// <summary>"Luca, Ripa · 1 non riconosciuto": who is on the photo.</summary>
    public static string People(IReadOnlyList<Face> faces, IReadOnlyDictionary<long, Person> persons)
    {
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(persons);
        var named = faces.Select(f => f.PersonId is { } id && persons.TryGetValue(id, out var p) && !p.Ignored ? p.Name : null).OfType<string>().Distinct().ToList();
        var unknown = faces.Count(f => f.PersonId is not { } id || !persons.TryGetValue(id, out var p) || (p.Name is null && !p.Ignored));
        var parts = new List<string>();
        if (named.Count > 0)
        {
            parts.Add(string.Join(", ", named));
        }

        if (unknown > 0)
        {
            parts.Add(Strings.Format(unknown == 1 ? "Faces.UnknownOne" : "Faces.UnknownMany", unknown));
        }

        return parts.Count == 0 ? Strings.Get("Faces.None") : string.Join(" · ", parts);
    }
}

/// <summary>A tag of the selected photo (✕ removes it: suppressed, I5).</summary>
public sealed record DetailTagViewModel(long TagId, string Label, bool IsManual);

/// <summary>A way out of "no results" (4e): drop one filter, with the count it would give.</summary>
public sealed record NoResultOption(string Label, Func<Task> Apply);

/// <summary>
/// Consultazione (boards 4d, 4e, 4e2; spec §12 view 6, P14): search field with tokens (people, tags, events = exact
/// filters) and free text (words on names and labels, ordered by meaning with CLIP); filters for dates, camera,
/// rating and outcome; virtualized grid; detail with editable tags (T), stars (1-5), faces on the preview, "Apri in
/// Photoshop" (Ctrl E), default app (Ctrl Enter), Explorer (Ctrl Shift E), similar photos (S).
/// </summary>
public sealed partial class BrowseViewModel : PageViewModel
{
    public const int PageSize = 1000;
    public const double CellSize = 168;
    public const double CellGap = 6;

    private static readonly CommandTable Table = new(
    [
        new("Browse.Move", new KeyGesture(Key.Left), "Browse.Command.Navigate", StatusGroupKey: "Browse.Command.Navigate", Parameter: "-1"),
        new("Browse.Move", new KeyGesture(Key.Right), "Browse.Command.Navigate", StatusGroupKey: "Browse.Command.Navigate", Parameter: "1"),
        new("Browse.Move", new KeyGesture(Key.Up), "Browse.Command.Navigate", StatusGroupKey: "Browse.Command.Navigate", Parameter: "-row"),
        new("Browse.Move", new KeyGesture(Key.Down), "Browse.Command.Navigate", StatusGroupKey: "Browse.Command.Navigate", Parameter: "row"),
        new("Browse.Photoshop", new KeyGesture(Key.E, KeyModifiers.Control), "Browse.Command.Photoshop", StatusGroupKey: "Browse.Command.Photoshop"),
        new("Browse.Default", new KeyGesture(Key.Enter, KeyModifiers.Control), "Browse.Command.Default"),
        new("Browse.Explorer", new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift), "Browse.Command.Explorer"),
        new("Browse.Tag", new KeyGesture(Key.T), "Browse.Command.Tag", StatusGroupKey: "Browse.Command.Tag"),
        new("Browse.Caption", new KeyGesture(Key.D), "Browse.Command.Caption", StatusGroupKey: "Browse.Command.Caption"),
        new("Browse.Similar", new KeyGesture(Key.S), "Browse.Command.Similar", StatusGroupKey: "Browse.Command.Similar"),
        new("Browse.Stars", new KeyGesture(Key.D1), "Browse.Command.Stars", StatusGroupKey: "Browse.Command.Stars", Parameter: "1"),
        new("Browse.Stars", new KeyGesture(Key.D2), "Browse.Command.Stars", Parameter: "2"),
        new("Browse.Stars", new KeyGesture(Key.D3), "Browse.Command.Stars", Parameter: "3"),
        new("Browse.Stars", new KeyGesture(Key.D4), "Browse.Command.Stars", Parameter: "4"),
        new("Browse.Stars", new KeyGesture(Key.D5), "Browse.Command.Stars", Parameter: "5"),
        new("Browse.Stars", new KeyGesture(Key.D0), "Browse.Command.Stars", Parameter: "0"),
        new("Browse.Preview", new KeyGesture(Key.Space), "Browse.Command.Preview", StatusGroupKey: "Browse.Command.Preview"),
        new("Browse.Facets", new KeyGesture(Key.F), "Browse.Command.Facets", StatusGroupKey: "Browse.Command.Facets"),
        new("Browse.Faces", new KeyGesture(Key.V), "Browse.Command.Faces"),
        new("Browse.AddFace", new KeyGesture(Key.N), "Browse.Command.AddFace", StatusGroupKey: "Browse.Command.AddFace"),
        new("Browse.Escape", new KeyGesture(Key.Escape), "Common.Cancel"),
    ]);

    private readonly PhotoFlowHost _host;
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private int _searchVersion;
    private List<SearchHit> _hits = [];
    private List<BrowseItemViewModel> _items = [];
    private int _shown;

    public BrowseViewModel(PhotoFlowHost host)
    {
        _host = host;
        Facets = new BrowseFacetsViewModel(host, SearchAsync);
        Editor = new PhotoEditorViewModel(host);
        Editor.Changed += async (_, _) => await Facets.LoadAsync(CancellationToken.None); // tag and people counts
        Editor.LargePhotoRequested += (_, _) => IsLargePreview = true;
        var any = Strings.Get("Browse.Filter.Any");
        YearOptions = [new OptionItem<int?>(null, any), .. Enumerable.Range(1990, DateTime.Today.Year - 1989).Reverse().Select(y => new OptionItem<int?>(y, y.ToString(CultureInfo.InvariantCulture)))];
        RatingOptions = [new OptionItem<int?>(null, any), .. Enumerable.Range(1, 5).Select(r => new OptionItem<int?>(r, Strings.Format("Browse.Filter.RatingValue", r)))];
        OutcomeOptions = [new OptionItem<CullingOutcome?>(null, any), .. Enum.GetValues<CullingOutcome>().Select(o => new OptionItem<CullingOutcome?>(o, Strings.Get("Outcome." + o)))];
    }

    /// <summary>The facets panel (tags in groups AND/OR, events, people, with counts).</summary>
    public BrowseFacetsViewModel Facets { get; }

    public override string Title => Strings.Get("Nav.Browse");

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Browse.Move" => MoveCommand,
        "Browse.Photoshop" => OpenInPhotoshopCommand,
        "Browse.Default" => OpenDefaultCommand,
        "Browse.Explorer" => ShowInExplorerCommand,
        "Browse.Tag" => Editor.StartTagCommand,
        "Browse.Caption" => Editor.StartCaptionCommand,
        "Browse.Similar" => FindSimilarCommand,
        "Browse.Stars" => SetStarsCommand,
        "Browse.Preview" => TogglePreviewCommand,
        "Browse.Facets" => Facets.ToggleCommand,
        "Browse.Faces" => Editor.ToggleFacesCommand,
        "Browse.AddFace" => Editor.StartAddFaceCommand,
        "Browse.Escape" => EscapeCommand,
        _ => base.ResolveCommand(id),
    };

    /// <summary>Asks the view to put the keyboard focus in the search field (Ctrl K).</summary>
    public event EventHandler? FocusSearchRequested;

    // --- Query ---

    public ObservableCollection<SearchSuggestion> Tokens { get; } = [];

    public ObservableCollection<SearchSuggestion> Suggestions { get; } = [];

    [ObservableProperty]
    private string? _queryText;

    /// <summary>Free text applied to the search ("che ride al tramonto"): words and meaning.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFreeText))]
    private string? _freeText;

    public bool HasFreeText => !string.IsNullOrWhiteSpace(FreeText);

    public IReadOnlyList<OptionItem<int?>> YearOptions { get; }

    public IReadOnlyList<OptionItem<int?>> RatingOptions { get; }

    public IReadOnlyList<OptionItem<CullingOutcome?>> OutcomeOptions { get; }

    public ObservableCollection<OptionItem<string?>> CameraOptions { get; } = [];

    /// <summary>The option shown by a combo box for a filter value.</summary>
    public OptionItem<int?> YearFromOption { get => YearOptions.First(o => o.Value == YearFrom); set { YearFrom = value?.Value; _ = SearchAsync(); } }

    public OptionItem<int?> YearToOption { get => YearOptions.First(o => o.Value == YearTo); set { YearTo = value?.Value; _ = SearchAsync(); } }

    public OptionItem<int?> RatingOption { get => RatingOptions.First(o => o.Value == MinRating); set { MinRating = value?.Value; _ = SearchAsync(); } }

    public OptionItem<CullingOutcome?> OutcomeOption { get => OutcomeOptions.First(o => o.Value == Outcome); set { Outcome = value?.Value; _ = SearchAsync(); } }

    public OptionItem<string?>? CameraOption { get => CameraOptions.FirstOrDefault(o => o.Value == Camera); set { Camera = value?.Value; _ = SearchAsync(); } }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YearFromOption))]
    private int? _yearFrom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YearToOption))]
    private int? _yearTo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraOption))]
    private string? _camera;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatingOption))]
    private int? _minRating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutcomeOption))]
    private CullingOutcome? _outcome;

    /// <summary>"Trova simili": the photo the results are similar to.</summary>
    [ObservableProperty]
    private long? _similarTo;

    // --- Results ---

    public ObservableCollection<BrowseRow> Rows { get; } = [];

    [ObservableProperty]
    private int _columns = 6;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyText = string.Empty;

    public ObservableCollection<NoResultOption> NoResultOptions { get; } = [];

    [ObservableProperty]
    private string? _coverageText;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private bool _isSearching;

    // --- Detail ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private BrowseItemViewModel? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    private Bitmap? _detailImage;

    /// <summary>Tags, caption and faces of the selected photo (shared with Revisione, P21).</summary>
    public PhotoEditorViewModel Editor { get; }

    [ObservableProperty]
    private string _detailName = string.Empty;

    [ObservableProperty]
    private string _detailPath = string.Empty;

    [ObservableProperty]
    private string _detailTaken = string.Empty;

    [ObservableProperty]
    private string _detailEvent = string.Empty;

    [ObservableProperty]
    private string _detailExposure = string.Empty;

    [ObservableProperty]
    private string _detailRating = string.Empty;

    [ObservableProperty]
    private bool _isLargePreview;

    [ObservableProperty]
    private string? _message;

    partial void OnColumnsChanged(int value) => BuildRows();

    public override async Task ActivateAsync()
    {
        await LoadSuggestionsAsync();
        await Editor.LoadTagSuggestionsAsync();
        await Facets.LoadAsync(CancellationToken.None);
        CameraOptions.Clear();
        CameraOptions.Add(new OptionItem<string?>(null, Strings.Get("Browse.Filter.Any")));
        foreach (var camera in await _host.Catalog.Assets.ListCamerasAsync(CancellationToken.None))
        {
            CameraOptions.Add(new OptionItem<string?>(camera, camera));
        }

        OnPropertyChanged(nameof(CameraOption));

        await SearchAsync();
    }

    /// <summary>From the search field of the title bar.</summary>
    public async Task SearchTextAsync(string text)
    {
        FreeText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        QueryText = null;
        await SearchAsync();
    }

    public void RequestSearchFocus() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    private async Task LoadSuggestionsAsync()
    {
        Suggestions.Clear();
        foreach (var person in await _host.Catalog.People.ListPersonsAsync(CancellationToken.None))
        {
            if (person.Person.Name is { } name && !person.Person.Ignored)
            {
                Suggestions.Add(new SearchSuggestion(SuggestionKind.Person, person.Person.Id, name, Strings.Format("Browse.PhotosCount", person.FaceCount)));
            }
        }

        foreach (var tag in await _host.Catalog.Tags.ListAsync(CancellationToken.None))
        {
            if (tag.PhotoCount == 0 || tag.Blacklisted)
            {
                continue;
            }

            var isEvent = tag.Key.StartsWith(Tagging.Vocabulary.TaggingStage.EventPrefix, StringComparison.Ordinal);
            var label = tag.Italian is { } it && (tag.Reviewed || isEvent) ? it : tag.English;
            Suggestions.Add(new SearchSuggestion(isEvent ? SuggestionKind.Event : SuggestionKind.Tag, tag.Id, label, Strings.Format("Browse.PhotosCount", tag.PhotoCount)));
        }
    }

    /// <summary>A suggestion chosen in the field becomes a token.</summary>
    public async Task AddTokenAsync(SearchSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (!Tokens.Contains(suggestion))
        {
            Tokens.Add(suggestion);
        }

        QueryText = null;
        await SearchAsync();
    }

    [RelayCommand]
    private async Task RemoveTokenAsync(SearchSuggestion? token)
    {
        if (token is not null)
        {
            Tokens.Remove(token);
            await SearchAsync();
        }
    }

    [RelayCommand]
    private async Task ClearFreeTextAsync()
    {
        FreeText = null;
        await SearchAsync();
    }

    /// <summary>Enter in the field: the text not matching a suggestion becomes the free text.</summary>
    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (!string.IsNullOrWhiteSpace(QueryText))
        {
            var exact = Suggestions.FirstOrDefault(s => string.Equals(s.Label, QueryText.Trim(), StringComparison.CurrentCultureIgnoreCase));
            if (exact is not null)
            {
                await AddTokenAsync(exact);
                return;
            }

            FreeText = QueryText.Trim();
            QueryText = null;
        }

        SimilarTo = null;
        await SearchAsync();
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        Tokens.Clear();
        Facets.Reset();
        (FreeText, QueryText, YearFrom, YearTo, Camera, MinRating, Outcome, SimilarTo) = (null, null, null, null, null, null, null, null);
        await SearchAsync();
    }

    [RelayCommand]
    private Task ApplyFiltersAsync() => SearchAsync();

    private SearchQuery BuildQuery(float[]? textEmbedding) => Facets.Apply(new SearchQuery(
        Tokens.Where(t => t.Kind == SuggestionKind.Person).Select(t => t.Id).ToList(),
        Tokens.Where(t => t.Kind != SuggestionKind.Person).Select(t => t.Id).ToList(),
        FreeText,
        textEmbedding,
        SimilarTo,
        YearFrom is { } from ? new DateTimeOffset(from, 1, 1, 0, 0, 0, TimeSpan.Zero) : null,
        YearTo is { } to ? new DateTimeOffset(to + 1, 1, 1, 0, 0, 0, TimeSpan.Zero) : null,
        Camera,
        MinRating,
        Outcome,
        _host.EmbeddingModelVersion,
        int.MaxValue));

    public async Task SearchAsync()
    {
        // One search at a time, and only the latest: two searches started together (a switch, then a click) used to
        // fill the grid with the photos of both.
        var version = Interlocked.Increment(ref _searchVersion);
        await _searchGate.WaitAsync();
        try
        {
            if (version == _searchVersion)
            {
                await SearchCoreAsync();
            }
        }
        finally
        {
            _searchGate.Release();
        }
    }

    private async Task SearchCoreAsync()
    {
        IsSearching = true;
        SearchQuery query;
        try
        {
            var clock = Stopwatch.StartNew();
            var embedding = HasFreeText ? await _host.EmbedQueryAsync(FreeText!, CancellationToken.None) : null;
            query = BuildQuery(embedding);
            var results = await Task.Run(() => _host.Catalog.Search.SearchAsync(query, CancellationToken.None));
            clock.Stop();
            _hits = [.. results.Hits];
            _shown = 0;
            _items = [];
            await ShowMoreAsync();
            ResultText = Strings.Format(results.ByMeaning ? "Browse.Results.Meaning" : "Browse.Results.Date",
                results.Total.ToString("N0", CultureInfo.CurrentCulture), clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture));
            IsEmpty = results.Total == 0;
            await BuildNoResultOptionsAsync(query);
            if (_host.EmbeddingModelVersion is { } model && HasFreeText)
            {
                var (embedded, total) = await _host.Catalog.Search.CoverageAsync(model, CancellationToken.None);
                CoverageText = embedded < total
                    ? Strings.Format("Browse.Coverage", embedded.ToString("N0", CultureInfo.CurrentCulture), total.ToString("N0", CultureInfo.CurrentCulture))
                    : null;
            }
            else
            {
                CoverageText = HasFreeText && _host.EmbeddingModelVersion is null ? Strings.Get("Browse.NoClip") : null;
            }

            Selected = _items.FirstOrDefault();
        }
        finally
        {
            IsSearching = false;
        }

        // The facets count on the whole result, after the grid is shown.
        await Facets.RefreshAsync(query, CancellationToken.None);
    }

    /// <summary>The next page of results (the grid shows at most <see cref="PageSize"/> more at a time).</summary>
    [RelayCommand]
    private async Task ShowMoreAsync()
    {
        var page = _hits.Skip(_shown).Take(PageSize).ToList();
        var assets = await _host.Catalog.Assets.GetManyAsync(page.Select(h => h.AssetId).ToList(), CancellationToken.None);
        var ratings = new Dictionary<long, int?>();
        foreach (var hit in page)
        {
            ratings[hit.AssetId] = (await _host.Catalog.Decisions.GetAsync(hit.AssetId, CancellationToken.None))?.Rating;
        }

        foreach (var hit in page)
        {
            var thumbnail = assets.TryGetValue(hit.AssetId, out var asset)
                ? new[] { asset.FullHash, asset.QuickHash }.OfType<string>().Select(_host.Thumbnails.PathFor).FirstOrDefault(File.Exists)
                : null;
            _items.Add(new BrowseItemViewModel(hit.AssetId, thumbnail, ratings[hit.AssetId] is { } r && r > 0 ? new string('★', r) : string.Empty));
        }

        _shown += page.Count;
        HasMore = _shown < _hits.Count;
        BuildRows();
    }

    private void BuildRows()
    {
        Rows.Clear();
        foreach (var chunk in _items.Chunk(Math.Max(1, Columns)))
        {
            Rows.Add(new BrowseRow(chunk));
        }
    }

    /// <summary>4e: for each filter in use, the count without it.</summary>
    private async Task BuildNoResultOptionsAsync(SearchQuery query)
    {
        NoResultOptions.Clear();
        if (_hits.Count > 0)
        {
            return;
        }

        EmptyText = Strings.Format("Browse.Empty.Title", string.Join(" · ", Tokens.Select(t => t.Label).Append(FreeText).OfType<string>()));
        async Task Option(string label, SearchQuery without, Func<Task> apply)
        {
            var count = (await _host.Catalog.Search.SearchAsync(without, CancellationToken.None)).Total;
            if (count > 0)
            {
                NoResultOptions.Add(new NoResultOption(Strings.Format("Browse.Empty.Without", label, count.ToString("N0", CultureInfo.CurrentCulture)), apply));
            }
        }

        foreach (var token in Tokens.ToList())
        {
            await Option(token.Label, query with { PersonIds = query.PersonIds.Where(id => token.Kind != SuggestionKind.Person || id != token.Id).ToList(), TagIds = query.TagIds.Where(id => token.Kind == SuggestionKind.Person || id != token.Id).ToList() },
                async () => { Tokens.Remove(token); await SearchAsync(); });
        }

        if (MinRating is { } rating)
        {
            await Option(Strings.Format("Browse.Filter.RatingValue", rating), query with { MinRating = null }, async () => { MinRating = null; await SearchAsync(); });
        }

        if (YearFrom is not null || YearTo is not null)
        {
            await Option(Strings.Get("Browse.Filter.Date"), query with { From = null, To = null }, async () => { (YearFrom, YearTo) = (null, null); await SearchAsync(); });
        }

        if (Camera is { } camera)
        {
            await Option(camera, query with { Camera = null }, async () => { Camera = null; await SearchAsync(); });
        }

        if (Outcome is { } outcome)
        {
            await Option(Strings.Get("Outcome." + outcome), query with { Outcome = null }, async () => { Outcome = null; await SearchAsync(); });
        }
    }

    [RelayCommand]
    private async Task ApplyNoResultOptionAsync(NoResultOption? option)
    {
        if (option is not null)
        {
            await option.Apply();
        }
    }

    /// <summary>"Cerca solo per significato su tutto l'archivio" (4e): the free text alone, every filter removed.</summary>
    [RelayCommand]
    private async Task MeaningOnlyAsync()
    {
        Tokens.Clear();
        (YearFrom, YearTo, Camera, MinRating, Outcome, SimilarTo) = (null, null, null, null, null, null);
        await SearchAsync();
    }

    // --- Navigation and detail ---

    [RelayCommand]
    private void Move(string? delta)
    {
        if (_items.Count == 0)
        {
            return;
        }

        var step = delta switch { "row" => Columns, "-row" => -Columns, _ => int.TryParse(delta, CultureInfo.InvariantCulture, out var d) ? d : 0 };
        var index = Selected is null ? 0 : _items.IndexOf(Selected);
        Selected = _items[Math.Clamp(index + step, 0, _items.Count - 1)];
    }

    public void Click(BrowseItemViewModel item) => Selected = item;

    partial void OnSelectedChanged(BrowseItemViewModel? oldValue, BrowseItemViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        _ = LoadDetailAsync(newValue);
    }

    private async Task LoadDetailAsync(BrowseItemViewModel? item)
    {
        _ = Editor.LoadAsync(item?.AssetId);
        if (item is null)
        {
            DetailImage = null;
            return;
        }

        var catalog = _host.Catalog;
        var asset = await catalog.Assets.GetAsync(item.AssetId, CancellationToken.None);
        if (asset is null || Selected != item)
        {
            return;
        }

        var path = await AssetFiles.LocateAsync(catalog, _host.FileSystem, item.AssetId, CancellationToken.None);
        DetailName = path is null ? $"#{item.AssetId}" : Path.GetFileName(path);
        DetailPath = path is null ? Strings.Get("Browse.Detail.NotFound") : Path.GetDirectoryName(path) ?? string.Empty;
        DetailTaken = asset.CapturedAt?.ToLocalTime().ToString("d MMM yyyy · HH:mm", CultureInfo.CurrentCulture) ?? "—";
        var exif = asset.Exif;
        DetailExposure = exif is null ? "—" : string.Join(" · ", new[]
        {
            exif.ExposureTimeSeconds is { } t ? (t >= 1 ? t.ToString("0.#", CultureInfo.CurrentCulture) + " s" : "1/" + Math.Round(1 / t).ToString(CultureInfo.InvariantCulture)) : null,
            exif.Aperture is { } f ? "f/" + f.ToString("0.#", CultureInfo.CurrentCulture) : null,
            exif.Iso is { } iso ? "ISO " + iso.ToString(CultureInfo.InvariantCulture) : null,
            exif.FocalLengthMm is { } mm ? mm.ToString("0", CultureInfo.CurrentCulture) + " mm" : null,
            exif.CameraModel,
        }.OfType<string>());
        var decision = await catalog.Decisions.GetAsync(item.AssetId, CancellationToken.None);
        var outcome = decision?.UserOutcome ?? decision?.AutomaticOutcome;
        DetailRating = $"{(decision?.Rating is { } r && r > 0 ? new string('★', r) : "—")} · {(outcome is { } o ? Strings.Get("Outcome." + o) : "—")}";

        var tags = await catalog.Tags.GetForAssetAsync(item.AssetId, CancellationToken.None);
        DetailEvent = tags.Where(t => t.Tag.Key.StartsWith(Tagging.Vocabulary.TaggingStage.EventPrefix, StringComparison.Ordinal)).Select(t => t.Tag.Italian ?? t.Tag.English).FirstOrDefault() ?? "—";

        var thumbnail = new[] { asset.FullHash, asset.QuickHash }.OfType<string>().Select(_host.Thumbnails.PathFor).FirstOrDefault(File.Exists);
        var image = thumbnail is null ? null : await Task.Run(() => UiBitmaps.FromFile(thumbnail, 1024));
        if (Selected != item)
        {
            return;
        }

        DetailImage = image;
        Editor.ImageSize = image is null ? default : new Size(image.PixelSize.Width, image.PixelSize.Height);
    }

    // --- Actions ---

    [RelayCommand]
    private async Task SetStarsAsync(string? value)
    {
        if (Selected is not { } item || !int.TryParse(value, CultureInfo.InvariantCulture, out var stars))
        {
            return;
        }

        var decision = await _host.Catalog.Decisions.GetAsync(item.AssetId, CancellationToken.None);
        await _host.Catalog.Decisions.SetUserAsync(item.AssetId, decision?.UserOutcome, stars == 0 ? null : stars, decision?.Label, CancellationToken.None);
        Message = await PhotoEditorViewModel.RewriteXmpAsync(_host, item.AssetId) ? null : Strings.Get("Browse.XmpFailed");
        await LoadDetailAsync(item);
    }

    /// <summary>Navigation to the regeneration (set by the shell).</summary>
    public IShellNavigator? Navigator { get; set; }

    /// <summary>Regeneration of the photos found (spec §5: the perimeter can be a search result).</summary>
    [RelayCommand]
    private void RegenerateResults()
    {
        if (Navigator is not null && _hits.Count > 0)
        {
            Navigator.ShowPage(new RegenerationViewModel(_host, Navigator, assetIds: [.. _hits.Select(h => h.AssetId)]));
        }
    }

    [RelayCommand]
    private async Task FindSimilarAsync()
    {
        if (Selected is { } item && _host.EmbeddingModelVersion is not null)
        {
            SimilarTo = item.AssetId;
            Tokens.Clear();
            FreeText = null;
            await SearchAsync();
            ResultText = Strings.Format("Browse.Results.Similar", _hits.Count.ToString("N0", CultureInfo.CurrentCulture));
        }
    }

    [RelayCommand]
    private async Task OpenInPhotoshopAsync()
    {
        if (await SelectedPathAsync() is not { } path)
        {
            return;
        }

        if (_host.Settings.PhotoshopPath is { } editor && File.Exists(editor))
        {
            Launch(new ProcessStartInfo(editor) { ArgumentList = { path }, UseShellExecute = false });
        }
        else
        {
            Message = Strings.Get("Browse.NoPhotoshop");
        }
    }

    [RelayCommand]
    private async Task OpenDefaultAsync()
    {
        if (await SelectedPathAsync() is { } path)
        {
            Launch(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task ShowInExplorerAsync()
    {
        if (await SelectedPathAsync() is not { } path)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Launch(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });
        }
        else
        {
            Launch(new ProcessStartInfo(Path.GetDirectoryName(path) ?? path) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void TogglePreview()
    {
        IsLargePreview = Selected is not null && !IsLargePreview;
        if (!IsLargePreview)
        {
            Editor.CancelAddFaceCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void Escape()
    {
        if (!Editor.Escape() && IsLargePreview)
        {
            IsLargePreview = false;
        }
    }

    private async Task<string?> SelectedPathAsync()
    {
        if (Selected is not { } item)
        {
            return null;
        }

        var path = await AssetFiles.LocateAsync(_host.Catalog, _host.FileSystem, item.AssetId, CancellationToken.None);
        if (path is null)
        {
            Message = Strings.Get("Browse.Detail.NotFound");
        }

        return path;
    }

    private void Launch(ProcessStartInfo start)
    {
        try
        {
            using var _ = Process.Start(start);
            Message = null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Message = Strings.Format("Browse.LaunchFailed", e.Message);
        }
    }
}
