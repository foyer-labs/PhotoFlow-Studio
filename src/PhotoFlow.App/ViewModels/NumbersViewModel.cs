// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Tagging.Ocr;

namespace PhotoFlow.App.ViewModels;

/// <summary>Kind of a list on the left of the Numbers view (board 2c).</summary>
public enum NumberListKind
{
    ToResolve,
    InList,
    Other,
}

/// <summary>A row of the left column: a doubt to resolve ("6 or 9"), a number of the entry list, or the other photos.</summary>
public sealed partial class NumberGroupViewModel(NumberListKind kind, string key, string title, string subtitle, IReadOnlyList<(long AssetId, int Vehicle)> vehicles) : ObservableObject
{
    public NumberListKind Kind { get; } = kind;

    public string Key { get; } = key;

    public string Title { get; } = title;

    public string Subtitle { get; } = subtitle;

    public IReadOnlyList<(long AssetId, int Vehicle)> Vehicles { get; } = vehicles;

    public string Count => Vehicles.Count.ToString("N0", CultureInfo.CurrentCulture);

    public bool IsDoubt => Kind == NumberListKind.ToResolve;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>A vehicle of a photo in the centre of the view: enlarged crop, readings, current number.</summary>
public sealed partial class NumberPhotoViewModel(long assetId, int vehicle, string name, long? burstId) : ObservableObject
{
    public long AssetId { get; } = assetId;

    public int Vehicle { get; } = vehicle;

    public string Name { get; } = name;

    public long? BurstId { get; } = burstId;

    public ObservableCollection<string> Readings { get; } = [];

    [ObservableProperty]
    private VehicleNumber? _number;

    [ObservableProperty]
    private IImage? _crop;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _state = string.Empty;

    [ObservableProperty]
    private string _others = string.Empty;
}

/// <summary>An entry of the list shown in the right panel (candidates of the photo).</summary>
public sealed record NumberEntryViewModel(string Number, string Drivers, string Details);

/// <summary>
/// Numbers view (board 2c, spec §8): the vehicles whose number is in doubt grouped by doubt ("6 or 9", partial), the
/// numbers of the entry list with their photos, the photos with no number or a number out of the list. Fast correction
/// from the keyboard: 0-9 type the number, Enter confirms (the typed number or the first candidate) and moves on, Del
/// says there is no number, ↑ ↓ go through the candidates and the numbers of the list, Ctrl Z undoes; the correction can
/// be propagated to the rest of the burst. The user's numbers are manual: never replaced by a new analysis (I5). The
/// entry list of the job is imported (CSV, Excel), edited as text and attached here; the numbers are then matched again.
/// </summary>
public sealed partial class NumbersViewModel : PageViewModel
{
    public const int MaxCrops = 60;

    private static readonly CommandTable Table = new(
    [
        .. Enumerable.Range(0, 10).Select(d => new CommandDefinition("Numbers.Digit", new KeyGesture(Key.D0 + d), "Numbers.Command.Type", StatusGroupKey: "Numbers.Command.Type", Parameter: d.ToString(CultureInfo.InvariantCulture))),
        .. Enumerable.Range(0, 10).Select(d => new CommandDefinition("Numbers.Digit", new KeyGesture(Key.NumPad0 + d), "Numbers.Command.Type", Parameter: d.ToString(CultureInfo.InvariantCulture))),
        new("Numbers.Erase", new KeyGesture(Key.Back), "Numbers.Command.Erase"),
        new("Numbers.Confirm", new KeyGesture(Key.Enter), "Numbers.Command.Confirm", StatusGroupKey: "Numbers.Command.Confirm"),
        new("Numbers.None", new KeyGesture(Key.Delete), "Numbers.Command.None", StatusGroupKey: "Numbers.Command.None"),
        new("Numbers.Cycle", new KeyGesture(Key.Up), "Numbers.Command.Cycle", StatusGroupKey: "Numbers.Command.Cycle", Parameter: "-1"),
        new("Numbers.Cycle", new KeyGesture(Key.Down), "Numbers.Command.Cycle", StatusGroupKey: "Numbers.Command.Cycle", Parameter: "1"),
        new("Numbers.Move", new KeyGesture(Key.Left), "Numbers.Command.Move", StatusGroupKey: "Numbers.Command.Move", Parameter: "-1"),
        new("Numbers.Move", new KeyGesture(Key.Right), "Numbers.Command.Move", StatusGroupKey: "Numbers.Command.Move", Parameter: "1"),
        new("Numbers.Group", new KeyGesture(Key.PageUp), "Numbers.Command.Group", StatusGroupKey: "Numbers.Command.Group", Parameter: "-1"),
        new("Numbers.Group", new KeyGesture(Key.PageDown), "Numbers.Command.Group", StatusGroupKey: "Numbers.Command.Group", Parameter: "1"),
        new("Numbers.Propagate", new KeyGesture(Key.P), "Numbers.Command.Propagate", StatusGroupKey: "Numbers.Command.Propagate"),
        new("Numbers.Undo", new KeyGesture(Key.Z, KeyModifiers.Control), "Review.Command.Undo", StatusGroupKey: "Review.Command.Undo"),
        new("Numbers.Exit", new KeyGesture(Key.Escape), "Numbers.Command.Exit", StatusGroupKey: "Numbers.Command.Exit"),
    ]);

    private readonly PhotoFlowHost _host;
    private readonly IShellNavigator? _navigator;
    private readonly long? _jobId;
    private readonly Stack<IReadOnlyList<(long AssetId, int Vehicle, VehicleNumber? Before)>> _undo = new();
    private readonly Dictionary<long, Bitmap?> _thumbnails = [];
    private Dictionary<(long AssetId, int Vehicle), VehicleNumber> _numbers = [];
    private Dictionary<long, string> _names = [];
    private Dictionary<long, long> _bursts = [];
    private IReadOnlyList<Entry> _entries = [];
    private List<string> _suggestions = [];
    private int _suggestion = -1;
    private bool _attaching = true;

    public NumbersViewModel(PhotoFlowHost host, IShellNavigator? navigator, long? jobId)
    {
        _host = host;
        _navigator = navigator;
        _jobId = jobId;
    }

    public override string Title => Strings.Get("Numbers.Title");

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Numbers.Digit" => TypeDigitCommand,
        "Numbers.Erase" => EraseCommand,
        "Numbers.Confirm" => ConfirmCommand,
        "Numbers.None" => NoNumberCommand,
        "Numbers.Cycle" => CycleCommand,
        "Numbers.Move" => MoveCommand,
        "Numbers.Group" => MoveGroupCommand,
        "Numbers.Propagate" => TogglePropagateCommand,
        "Numbers.Undo" => UndoCommand,
        "Numbers.Exit" => ExitCommand,
        _ => base.ResolveCommand(id),
    };

    /// <summary>Raised when the view should open the file picker of an entry list.</summary>
    public event EventHandler? ImportRequested;

    public ObservableCollection<NumberGroupViewModel> ToResolve { get; } = [];

    public ObservableCollection<NumberGroupViewModel> InList { get; } = [];

    public ObservableCollection<NumberGroupViewModel> Other { get; } = [];

    public ObservableCollection<NumberPhotoViewModel> Photos { get; } = [];

    public ObservableCollection<NumberEntryViewModel> Candidates { get; } = [];

    public ObservableCollection<EntryList> Lists { get; } = [];

    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private NumberGroupViewModel? _group;

    [ObservableProperty]
    private NumberPhotoViewModel? _current;

    [ObservableProperty]
    private string _typed = string.Empty;

    [ObservableProperty]
    private string _typedEntry = string.Empty;

    [ObservableProperty]
    private bool _propagate = true;

    [ObservableProperty]
    private EntryList? _list;

    [ObservableProperty]
    private string _listInfo = string.Empty;

    [ObservableProperty]
    private string _inListTitle = string.Empty;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isEmpty;

    public bool HasJob => _jobId is not null;

    public override async Task ActivateAsync()
    {
        if (_jobId is not { } jobId || await _host.Catalog.Jobs.GetAsync(jobId, CancellationToken.None) is not { } job)
        {
            IsEmpty = true;
            return;
        }

        var items = (await _host.Catalog.Jobs.GetItemsAsync(jobId, CancellationToken.None)).Where(i => i.AssetId is not null).ToList();
        _names = items.GroupBy(i => i.AssetId!.Value).ToDictionary(g => g.Key, g => Path.GetFileName(g.First().SourcePath));
        _bursts = (await _host.Catalog.Groups.ListAsync(jobId, CancellationToken.None)).Where(g => g.Group.Type == GroupType.Burst)
            .SelectMany(g => g.Members.Select(m => (m.AssetId, g.Group.Id))).GroupBy(x => x.AssetId).ToDictionary(g => g.Key, g => g.First().Id);
        Header = job.EventName ?? Path.GetFileName(job.Source?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;
        Lists.Clear();
        foreach (var list in await _host.Catalog.Motorsport.ListListsAsync(CancellationToken.None))
        {
            Lists.Add(list);
        }

        var listId = await _host.Catalog.Motorsport.GetJobListAsync(jobId, CancellationToken.None);
        _attaching = false;
        List = Lists.FirstOrDefault(l => l.Id == listId);
        _attaching = true;
        await ReloadAsync();
    }

    /// <summary>Numbers of the job, the lists on the left, the selection kept when possible.</summary>
    private async Task ReloadAsync()
    {
        if (_jobId is not { } jobId)
        {
            return;
        }

        _entries = List is { } list ? await _host.Catalog.Motorsport.GetEntriesAsync(list.Id, CancellationToken.None) : [];
        _numbers = (await _host.Catalog.Motorsport.GetJobNumbersAsync(jobId, CancellationToken.None)).ToDictionary(n => (n.AssetId, n.Vehicle));
        var selectedKey = Group?.Key;
        var selectedPhoto = Current is { } c ? (c.AssetId, c.Vehicle) : default;

        ToResolve.Clear();
        foreach (var doubt in _numbers.Values.Where(n => n.State == NumberState.ToReview)
            .GroupBy(DoubtKey).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var first = doubt.First();
            ToResolve.Add(new NumberGroupViewModel(NumberListKind.ToResolve, "doubt:" + doubt.Key, doubt.Key.Length == 0 ? "?" : doubt.Key,
                Strings.Get(IsConfusion(first.Candidates) ? "Numbers.Doubt.Confusion" : first.Candidates.Count > 0 ? "Numbers.Doubt.Partial" : "Numbers.Doubt.Unknown"),
                Ordered(doubt)));
        }

        InList.Clear();
        var certain = _numbers.Values.Where(n => n.State == NumberState.Certain && n.Number is not null).ToLookup(n => n.Number!, StringComparer.Ordinal);
        foreach (var entry in _entries.OrderByDescending(e => certain[e.Number].Count()).ThenBy(e => e.Number.Length).ThenBy(e => e.Number, StringComparer.Ordinal))
        {
            InList.Add(new NumberGroupViewModel(NumberListKind.InList, "number:" + entry.Number, entry.Number, Describe(entry), Ordered(certain[entry.Number])));
        }

        // Without a list the certain numbers read with high confidence are kept as they are.
        foreach (var number in certain.Where(g => _entries.All(e => e.Number != g.Key)).OrderBy(g => g.Key.Length).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            InList.Add(new NumberGroupViewModel(NumberListKind.InList, "number:" + number.Key, number.Key, Strings.Get("Numbers.NotInList"), Ordered(number)));
        }

        InListTitle = Strings.Format("Numbers.InList", InList.Count(g => g.Vehicles.Count > 0).ToString(CultureInfo.CurrentCulture), _entries.Count.ToString(CultureInfo.CurrentCulture));
        Other.Clear();
        Other.Add(new NumberGroupViewModel(NumberListKind.Other, "none", Strings.Get("Numbers.NoNumber"), string.Empty,
            Ordered(_numbers.Values.Where(n => n.State == NumberState.NoNumber && n.Vehicle == 0))));
        Other.Add(new NumberGroupViewModel(NumberListKind.Other, "out", Strings.Get("Numbers.OutOfList"), string.Empty,
            Ordered(_numbers.Values.Where(n => n.State == NumberState.OutOfList))));

        var vehicles = _numbers.Count;
        Summary = Strings.Format("Numbers.Summary", _names.Count.ToString("N0", CultureInfo.CurrentCulture), vehicles.ToString("N0", CultureInfo.CurrentCulture),
            ToResolve.Sum(g => g.Vehicles.Count).ToString("N0", CultureInfo.CurrentCulture), _entries.Count.ToString(CultureInfo.CurrentCulture));
        IsEmpty = vehicles == 0;
        UpdateListInfo();

        var groups = AllGroups().ToList();
        var group = groups.FirstOrDefault(g => g.Key == selectedKey && g.Vehicles.Count > 0)
            ?? groups.FirstOrDefault(g => g.Kind == NumberListKind.ToResolve)
            ?? groups.FirstOrDefault(g => g.Vehicles.Count > 0);
        await SelectGroupAsync(group, selectedPhoto);
    }

    private List<(long AssetId, int Vehicle)> Ordered(IEnumerable<VehicleNumber> numbers) =>
        [.. numbers.OrderBy(n => _names.GetValueOrDefault(n.AssetId) ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.AssetId).ThenBy(n => n.Vehicle)
            .Select(n => (n.AssetId, n.Vehicle))];

    /// <summary>"6 o 9": the candidates of the doubt (the first three), the same doubt for every photo that has it.</summary>
    private static string DoubtKey(VehicleNumber number) =>
        string.Join(Strings.Get("Numbers.Or"), number.Candidates.Take(3).Select(c => c.Number).Order(StringComparer.Ordinal));

    /// <summary>Two candidates of the same length that differ by digits often confused (6/9, 1/7).</summary>
    private static bool IsConfusion(IReadOnlyList<NumberCandidate> candidates)
    {
        static bool Pair(char a, char b) => a == b || (a, b) is ('6', '9') or ('9', '6') or ('1', '7') or ('7', '1');
        return candidates.Count >= 2 && candidates.Take(3).SelectMany((a, i) => candidates.Take(3).Skip(i + 1).Select(b => (a.Number, b.Number)))
            .Any(p => p.Item1.Length == p.Item2.Length && p.Item1 != p.Item2 && p.Item1.Zip(p.Item2).All(c => Pair(c.First, c.Second)));
    }

    private static string Describe(Entry entry) =>
        entry.IsCrew
            ? Strings.Format("Numbers.Crew", entry.Drivers.Count.ToString(CultureInfo.CurrentCulture), entry.Team ?? string.Empty)
            : string.Join(" · ", new[] { entry.Drivers.FirstOrDefault(), entry.Team }.Where(s => !string.IsNullOrEmpty(s)));

    private IEnumerable<NumberGroupViewModel> AllGroups() => ToResolve.Concat(InList).Concat(Other);

    [RelayCommand]
    private Task SelectGroupAsync(NumberGroupViewModel? group) => SelectGroupAsync(group, default);

    private async Task SelectGroupAsync(NumberGroupViewModel? group, (long AssetId, int Vehicle) keep)
    {
        foreach (var g in AllGroups())
        {
            g.IsSelected = g == group;
        }

        Group = group;
        Photos.Clear();
        if (group is null)
        {
            Current = null;
            UpdateCandidates();
            return;
        }

        foreach (var (assetId, vehicle) in group.Vehicles.Take(MaxCrops))
        {
            var photo = new NumberPhotoViewModel(assetId, vehicle, _names.GetValueOrDefault(assetId) ?? assetId.ToString(CultureInfo.InvariantCulture),
                _bursts.TryGetValue(assetId, out var burst) ? burst : null);
            Refresh(photo);
            Photos.Add(photo);
        }

        Select(Photos.FirstOrDefault(p => (p.AssetId, p.Vehicle) == keep) ?? Photos.FirstOrDefault());
        foreach (var photo in Photos.ToList())
        {
            await LoadAsync(photo);
            if (Group != group)
            {
                return;
            }
        }
    }

    private void Refresh(NumberPhotoViewModel photo)
    {
        photo.Number = _numbers.GetValueOrDefault((photo.AssetId, photo.Vehicle));
        photo.State = photo.Number is { } n
            ? Strings.Format("Numbers.State." + n.State, n.Number ?? string.Empty) + (n.Origin == NumberOrigin.Automatic ? string.Empty : " · " + Strings.Get("Numbers.Origin." + n.Origin))
            : string.Empty;
        photo.Others = string.Join("  ", _numbers.Values.Where(o => o.AssetId == photo.AssetId && o.Vehicle != photo.Vehicle && o.State == NumberState.Certain)
            .Select(o => Strings.Format("Numbers.OtherVehicle", o.Number ?? string.Empty)));
    }

    /// <summary>Readings and the crop of the vehicle (from the thumbnail; the selected photo from the preview).</summary>
    private async Task LoadAsync(NumberPhotoViewModel photo)
    {
        if (photo.Readings.Count == 0)
        {
            foreach (var reading in (await _host.Catalog.Motorsport.GetReadingsAsync(photo.AssetId, CancellationToken.None)).Where(r => r.Vehicle == photo.Vehicle)
                .OrderByDescending(r => r.Confidence).Take(6))
            {
                photo.Readings.Add($"{reading.Text} · {reading.Confidence.ToString("0.00", CultureInfo.CurrentCulture)}");
            }
        }

        if (photo.Crop is null && photo.Number is { } number && await ThumbnailAsync(photo.AssetId) is { } bitmap)
        {
            photo.Crop = CropOf(bitmap, number.Box);
        }
    }

    private async Task<Bitmap?> ThumbnailAsync(long assetId)
    {
        if (_thumbnails.TryGetValue(assetId, out var cached))
        {
            return cached;
        }

        var asset = await _host.Catalog.Assets.GetAsync(assetId, CancellationToken.None);
        var path = asset is null ? null : new[] { asset.FullHash, asset.QuickHash }.OfType<string>().Select(_host.Thumbnails.PathFor).FirstOrDefault(File.Exists);
        Bitmap? bitmap = null;
        if (path is not null)
        {
            bitmap = await Task.Run(() =>
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
            });
        }

        _thumbnails[assetId] = bitmap;
        return bitmap;
    }

    /// <summary>The vehicle with a small margin; the whole photo for a vehicle saved without its box.</summary>
    public static CroppedBitmap? CropOf(Bitmap bitmap, BoundingBox box)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var (w, h) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var x = (int)Math.Clamp((box.X1 - 0.02f) * w, 0, w - 1);
        var y = (int)Math.Clamp((box.Y1 - 0.02f) * h, 0, h - 1);
        var width = (int)Math.Clamp((box.X2 + 0.02f) * w, x + 1, w) - x;
        var height = (int)Math.Clamp((box.Y2 + 0.02f) * h, y + 1, h) - y;
        return width < 2 || height < 2 ? null : new CroppedBitmap(bitmap, new PixelRect(x, y, width, height));
    }

    private void Select(NumberPhotoViewModel? photo)
    {
        foreach (var p in Photos)
        {
            p.IsSelected = p == photo;
        }

        Current = photo;
        Typed = string.Empty;
        _suggestions = photo?.Number is { } number
            ? [.. number.Candidates.Select(c => c.Number).Concat(_entries.Select(e => e.Number)).Distinct(StringComparer.Ordinal)]
            : [.. _entries.Select(e => e.Number)];
        _suggestion = -1;
        UpdateCandidates();
    }

    [RelayCommand]
    private void SelectPhoto(NumberPhotoViewModel? photo) => Select(photo);

    partial void OnTypedChanged(string value) => UpdateCandidates();

    /// <summary>The right panel: the entries of the candidates of the photo, or of the number being typed.</summary>
    private void UpdateCandidates()
    {
        Candidates.Clear();
        var numbers = Typed.Length > 0
            ? _entries.Where(e => e.Number.StartsWith(Typed, StringComparison.Ordinal)).Select(e => e.Number).Take(6)
            : Current?.Number?.Candidates.Select(c => c.Number) ?? [];
        foreach (var number in numbers.Distinct(StringComparer.Ordinal))
        {
            if (_entries.FirstOrDefault(e => e.Number == number) is { } entry)
            {
                Candidates.Add(new NumberEntryViewModel(entry.Number, entry.IsCrew ? string.Join(" · ", entry.Drivers) : entry.Drivers.FirstOrDefault() ?? string.Empty,
                    string.Join(" · ", new[] { entry.Team, entry.Category, entry.Car }.Where(s => !string.IsNullOrEmpty(s)))));
            }
        }

        TypedEntry = Typed.Length == 0 ? string.Empty
            : _entries.FirstOrDefault(e => e.Number == Typed) is { } typed ? Describe(typed)
            : _entries.Count > 0 ? Strings.Get("Numbers.NotInList") : string.Empty;
    }

    [RelayCommand]
    private void TypeDigit(string? digit)
    {
        if (Current is null || digit is not { Length: 1 } || !char.IsAsciiDigit(digit[0]))
        {
            return;
        }

        Typed = Typed.Length >= NumberMatcher.MaxDigits ? digit : Typed + digit;
    }

    [RelayCommand]
    private void Erase() => Typed = Typed.Length > 0 ? Typed[..^1] : Typed;

    /// <summary>↑ ↓: the candidates of the photo first, then the numbers of the list.</summary>
    [RelayCommand]
    private void Cycle(string? delta)
    {
        if (Current is null || _suggestions.Count == 0 || !int.TryParse(delta, CultureInfo.InvariantCulture, out var d))
        {
            return;
        }

        _suggestion = _suggestion < 0 ? (d > 0 ? 0 : _suggestions.Count - 1) : (_suggestion + d + _suggestions.Count) % _suggestions.Count;
        Typed = _suggestions[_suggestion];
    }

    /// <summary>Enter: the typed number, or the first candidate, becomes the user's number; then the next photo.</summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Current is not { } photo)
        {
            return;
        }

        var number = Typed.Length > 0 ? Typed.TrimStart('0') is { Length: > 0 } n ? n : "0"
            : photo.Number is { State: NumberState.Certain, Number: { } current } ? current
            : photo.Number?.Candidates.FirstOrDefault()?.Number;
        if (number is null)
        {
            StatusMessage = Strings.Get("Numbers.TypeFirst");
            return;
        }

        await SetAsync(photo, number);
    }

    /// <summary>Del: the vehicle has no readable number.</summary>
    [RelayCommand]
    private Task NoNumberAsync() => Current is { } photo ? SetAsync(photo, null) : Task.CompletedTask;

    private async Task SetAsync(NumberPhotoViewModel photo, string? number)
    {
        var targets = new List<(long AssetId, int Vehicle)> { (photo.AssetId, photo.Vehicle) };
        if (Propagate && number is not null && photo.Vehicle == 0 && photo.BurstId is { } burst)
        {
            // The rest of the burst: the main vehicle of the photos whose number is not certain nor the user's.
            targets.AddRange(_bursts.Where(b => b.Value == burst && b.Key != photo.AssetId).Select(b => b.Key)
                .Where(a => _numbers.GetValueOrDefault((a, 0)) is not { } n || (n.Origin != NumberOrigin.Manual && n.State != NumberState.Certain))
                .Select(a => (a, 0)));
        }

        var before = targets.Select(t => (t.AssetId, t.Vehicle, _numbers.GetValueOrDefault(t))).ToList();
        foreach (var (assetId, vehicle) in targets)
        {
            await _host.Catalog.Motorsport.SetManualNumberAsync(assetId, vehicle, number, CancellationToken.None);
            await MotorsportTags.ApplyAsync(_host.Catalog, assetId, _entries.Count > 0 ? _entries : null, CancellationToken.None);
        }

        _undo.Push(before);
        StatusMessage = Strings.Format(number is null ? "Numbers.SetNone" : targets.Count > 1 ? "Numbers.SetBurst" : "Numbers.Set", photo.Name, number ?? string.Empty, (targets.Count - 1).ToString(CultureInfo.CurrentCulture));
        var next = Photos.IndexOf(photo) + 1;
        var keep = next < Photos.Count ? (Photos[next].AssetId, Photos[next].Vehicle) : default;
        await ReloadKeepingAsync(keep);
    }

    /// <summary>After a change: the same list stays open and the selection moves to <paramref name="keep"/> (or the first left).</summary>
    private async Task ReloadKeepingAsync((long AssetId, int Vehicle) keep)
    {
        var key = Group?.Key;
        if (Current is { } current && keep == default)
        {
            keep = (current.AssetId, current.Vehicle);
        }

        await ReloadAsync();
        if (Group?.Key == key && Photos.FirstOrDefault(p => (p.AssetId, p.Vehicle) == keep) is { } photo)
        {
            Select(photo);
        }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (!_undo.TryPop(out var before))
        {
            return;
        }

        foreach (var (assetId, vehicle, number) in before)
        {
            await _host.Catalog.Motorsport.PutNumberAsync(assetId, vehicle, number, CancellationToken.None);
            await MotorsportTags.ApplyAsync(_host.Catalog, assetId, _entries.Count > 0 ? _entries : null, CancellationToken.None);
        }

        StatusMessage = Strings.Get("Numbers.Undone");
        var target = (before[0].AssetId, before[0].Vehicle);
        await ReloadAsync();
        var group = Group is { } g && g.Vehicles.Contains(target) ? g : AllGroups().FirstOrDefault(x => x.Vehicles.Contains(target)) ?? Group;
        await SelectGroupAsync(group, target);
    }

    [RelayCommand]
    private async Task MoveAsync(string? delta)
    {
        if (Current is not { } photo || !int.TryParse(delta, CultureInfo.InvariantCulture, out var d))
        {
            return;
        }

        var next = Photos[Math.Clamp(Photos.IndexOf(photo) + d, 0, Photos.Count - 1)];
        Select(next);
        await LoadAsync(next);
    }

    [RelayCommand]
    private async Task MoveGroupAsync(string? delta)
    {
        var groups = AllGroups().Where(g => g.Vehicles.Count > 0).ToList();
        if (groups.Count == 0 || !int.TryParse(delta, CultureInfo.InvariantCulture, out var d))
        {
            return;
        }

        var index = Group is null ? -1 : groups.FindIndex(g => g.Key == Group.Key);
        await SelectGroupAsync(groups[Math.Clamp(index + d, 0, groups.Count - 1)]);
    }

    [RelayCommand]
    private void TogglePropagate() => Propagate = !Propagate;

    /// <summary>Esc: first clears the typed number, then goes back to the review.</summary>
    [RelayCommand]
    private void Exit()
    {
        if (Typed.Length > 0)
        {
            Typed = string.Empty;
            return;
        }

        if (_navigator is null)
        {
            return;
        }

        if (_jobId is { } jobId)
        {
            _navigator.OpenReview(jobId);
        }
        else
        {
            _navigator.ShowSection(SectionCatalog.Review);
        }
    }

    // --- Entry list ---

    private void UpdateListInfo() =>
        ListInfo = List is { } list
            ? Strings.Format("Numbers.List.Info", list.SourceFile is { } file ? Path.GetFileName(file) : Strings.Get("Numbers.List.Manual"), _entries.Count.ToString(CultureInfo.CurrentCulture))
            : Strings.Get("Numbers.List.None");

    /// <summary>Another list for the job (or none): the numbers are matched again (user's numbers kept).</summary>
    partial void OnListChanged(EntryList? value)
    {
        if (_attaching && _jobId is { } jobId)
        {
            _ = AttachAsync(jobId, value?.Id);
        }
    }

    private async Task AttachAsync(long jobId, long? listId)
    {
        await _host.Catalog.Motorsport.SetJobListAsync(jobId, listId, CancellationToken.None);
        await MatchAgainAsync();
    }

    private async Task MatchAgainAsync()
    {
        if (_jobId is not { } jobId || await _host.Catalog.Jobs.GetAsync(jobId, CancellationToken.None) is not { } job)
        {
            return;
        }

        await Task.Run(() => new NumbersStage(_host.Catalog).RunAsync(new JobContext(jobId, job.Type, job.ProfileId ?? "motorsport"), null, CancellationToken.None));
        _undo.Clear();
        await ReloadAsync();
    }

    [RelayCommand]
    private void Import() => ImportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>An entry list from a CSV or Excel file: saved in the catalog, attached to the job, numbers matched again.</summary>
    public async Task ImportFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IReadOnlyList<Entry> entries;
        try
        {
            entries = await EntryListImporter.ReadAsync(path, CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            StatusMessage = Strings.Format("Numbers.List.ImportFailed", Path.GetFileName(path), e.Message);
            return;
        }

        if (entries.Count == 0)
        {
            StatusMessage = Strings.Format("Numbers.List.ImportEmpty", Path.GetFileName(path));
            return;
        }

        var id = await _host.Catalog.Motorsport.CreateListAsync(Path.GetFileNameWithoutExtension(path), path, CancellationToken.None);
        await _host.Catalog.Motorsport.ReplaceEntriesAsync(id, entries, CancellationToken.None);
        var list = (await _host.Catalog.Motorsport.ListListsAsync(CancellationToken.None)).First(l => l.Id == id);
        Lists.Add(list);
        StatusMessage = Strings.Format("Numbers.List.Imported", list.Name, entries.Count.ToString(CultureInfo.CurrentCulture));
        List = list;
    }

    /// <summary>Edit: the list as text, one entry per line (number; drivers; team; category; car).</summary>
    [RelayCommand]
    private void Edit()
    {
        var text = new StringBuilder();
        foreach (var entry in _entries)
        {
            text.Append(entry.Number).Append(';').Append(string.Join(" / ", entry.Drivers)).Append(';').Append(entry.Team).Append(';')
                .Append(entry.Category).Append(';').Append(entry.Car).Append('\n');
        }

        EditText = text.ToString();
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    /// <summary>Saves the edited text: in the list of the job, or in a new list when the job has none.</summary>
    [RelayCommand]
    private async Task SaveEditAsync()
    {
        var entries = EntryListImporter.Parse(EntryListImporter.ReadCsv(EditText.Replace("\r", string.Empty, StringComparison.Ordinal)));
        var id = List?.Id ?? await _host.Catalog.Motorsport.CreateListAsync(Header, null, CancellationToken.None);
        await _host.Catalog.Motorsport.ReplaceEntriesAsync(id, entries, CancellationToken.None);
        IsEditing = false;
        StatusMessage = Strings.Format("Numbers.List.Saved", entries.Count.ToString(CultureInfo.CurrentCulture));
        if (List is null)
        {
            var list = (await _host.Catalog.Motorsport.ListListsAsync(CancellationToken.None)).First(l => l.Id == id);
            Lists.Add(list);
            List = list;
        }
        else
        {
            await MatchAgainAsync();
        }
    }
}
