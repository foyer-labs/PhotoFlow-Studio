// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.Analysis.Duplicates;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.App.ViewModels;

/// <summary>A group in the list of the left column (4f).</summary>
public sealed partial class DuplicateGroupItem(DuplicateGroup group, string title, string detail) : ObservableObject
{
    public DuplicateGroup Group { get; } = group;

    public string Title { get; } = title;

    public string Detail { get; } = detail;

    public bool IsExact => Group.Kind == DuplicateKind.Exact;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>A copy in the side-by-side comparison: what helps choosing which one to keep.</summary>
public sealed partial class DuplicateCopyViewModel(int number, DuplicateMember member) : ObservableObject
{
    public int Number { get; } = number;

    public DuplicateMember Member { get; } = member;

    public string Name => Path.GetFileName(Member.Location.RelativePath);

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private string _facts = string.Empty;

    [ObservableProperty]
    private string _extras = string.Empty;

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isKept;

    public bool IsRecommended { get; init; }

    public string StateText => Strings.Get(IsKept ? "Duplicates.Keep" : "Duplicates.Move");
}

/// <summary>
/// Duplicates (boards 4f, 4g; P5): exact copies and near duplicates found by <see cref="DuplicateFinder"/>; the copies
/// side by side with what matters (resolution, size, format, manual tags, rating, sidecar) and the one proposed to keep;
/// 1-4 choose which to keep, Enter moves the others into <c>_duplicati</c> of their library (never deleted), I marks
/// "not duplicates" (remembered), ↑ ↓ between groups, F5 checks again.
/// </summary>
public sealed partial class DuplicatesViewModel : PageViewModel
{
    public const string FolderName = "_duplicati";

    private static readonly CommandTable Table = new(
    [
        new("Duplicates.Group", new KeyGesture(Key.Up), "Duplicates.Command.Group", StatusGroupKey: "Duplicates.Command.Group", Parameter: "-1"),
        new("Duplicates.Group", new KeyGesture(Key.Down), "Duplicates.Command.Group", StatusGroupKey: "Duplicates.Command.Group", Parameter: "1"),
        new("Duplicates.Choose", new KeyGesture(Key.D1), "Duplicates.Command.Choose", StatusGroupKey: "Duplicates.Command.Choose", Parameter: "1"),
        new("Duplicates.Choose", new KeyGesture(Key.D2), "Duplicates.Command.Choose", Parameter: "2"),
        new("Duplicates.Choose", new KeyGesture(Key.D3), "Duplicates.Command.Choose", Parameter: "3"),
        new("Duplicates.Choose", new KeyGesture(Key.D4), "Duplicates.Command.Choose", Parameter: "4"),
        new("Duplicates.Confirm", new KeyGesture(Key.Enter), "Duplicates.Command.Confirm", StatusGroupKey: "Duplicates.Command.Confirm"),
        new("Duplicates.Ignore", new KeyGesture(Key.I), "Duplicates.Command.Ignore", StatusGroupKey: "Duplicates.Command.Ignore"),
        new("Duplicates.Rescan", new KeyGesture(Key.F5), "Duplicates.Command.Rescan", StatusGroupKey: "Duplicates.Command.Rescan"),
    ]);

    private readonly PhotoFlowHost _host;

    public DuplicatesViewModel(PhotoFlowHost host) => _host = host;

    public override string Title => Strings.Get("Nav.Duplicates");

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Duplicates.Group" => MoveGroupCommand,
        "Duplicates.Choose" => ChooseCommand,
        "Duplicates.Confirm" => ConfirmCommand,
        "Duplicates.Ignore" => IgnoreCommand,
        "Duplicates.Rescan" => RescanCommand,
        _ => base.ResolveCommand(id),
    };

    public ObservableCollection<DuplicateGroupItem> ExactGroups { get; } = [];

    public ObservableCollection<DuplicateGroupItem> NearGroups { get; } = [];

    public ObservableCollection<DuplicateCopyViewModel> Copies { get; } = [];

    [ObservableProperty]
    private DuplicateGroupItem? _selected;

    [ObservableProperty]
    private string _exactTitle = string.Empty;

    [ObservableProperty]
    private string _nearTitle = string.Empty;

    [ObservableProperty]
    private string _groupTitle = string.Empty;

    [ObservableProperty]
    private string _groupDetail = string.Empty;

    [ObservableProperty]
    private string _recommendation = string.Empty;

    [ObservableProperty]
    private string _lastScanText = string.Empty;

    [ObservableProperty]
    private string? _progressText;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isScanning;

    public bool IsEmpty => !IsScanning && ExactGroups.Count == 0 && NearGroups.Count == 0;

    public bool HasGroups => ExactGroups.Count + NearGroups.Count > 0;

    private bool _scanned;

    public override async Task ActivateAsync()
    {
        if (!_scanned)
        {
            await RescanAsync();
        }
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        IsScanning = true;
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var finder = new DuplicateFinder(_host.Catalog, _host.Thumbnails, _host.ImageOperations);
            var progress = new Progress<(int Done, int Total)>(p => ProgressText = Strings.Format("Duplicates.Scanning", p.Done.ToString("N0", CultureInfo.CurrentCulture), p.Total.ToString("N0", CultureInfo.CurrentCulture)));
            var scan = await Task.Run(() => finder.ScanAsync(_host.EmbeddingModelVersion, progress, CancellationToken.None));
            _scanned = true;
            ExactGroups.Clear();
            NearGroups.Clear();
            foreach (var group in scan.Exact)
            {
                ExactGroups.Add(new DuplicateGroupItem(group, Strings.Format("Duplicates.GroupTitle", Path.GetFileName(group.Members[0].Location.RelativePath), group.Members.Count),
                    Strings.Get("Duplicates.SameContent")));
            }

            foreach (var group in scan.Near)
            {
                NearGroups.Add(new DuplicateGroupItem(group, Strings.Format("Duplicates.GroupTitleNear", Path.GetFileNameWithoutExtension(group.Members[group.Recommended].Location.RelativePath), group.Members.Count),
                    Strings.Format("Duplicates.Similarity", group.Similarity.ToString("0.00", CultureInfo.CurrentCulture))));
            }

            ExactTitle = Strings.Format("Duplicates.Exact", ExactGroups.Count);
            NearTitle = Strings.Format("Duplicates.Near", NearGroups.Count);
            var excluded = (await _host.Catalog.Duplicates.ListExclusionsAsync(CancellationToken.None)).Count;
            LastScanText = Strings.Format("Duplicates.LastScan", scan.At.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture),
                scan.Checked.ToString("N0", CultureInfo.CurrentCulture), excluded);
            if (_host.EmbeddingModelVersion is null)
            {
                Message = Strings.Get("Duplicates.NoClip");
            }

            Selected = ExactGroups.Concat(NearGroups).FirstOrDefault();
        }
        finally
        {
            IsScanning = false;
            ProgressText = null;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasGroups));
        }
    }

    private List<DuplicateGroupItem> All() => [.. ExactGroups, .. NearGroups];

    [RelayCommand]
    private void MoveGroup(string? delta)
    {
        var all = All();
        if (all.Count == 0 || !int.TryParse(delta, CultureInfo.InvariantCulture, out var d))
        {
            return;
        }

        Selected = all[Math.Clamp((Selected is null ? 0 : all.IndexOf(Selected)) + d, 0, all.Count - 1)];
    }

    public void Click(DuplicateGroupItem item) => Selected = item;

    partial void OnSelectedChanged(DuplicateGroupItem? oldValue, DuplicateGroupItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        _ = LoadCopiesAsync(newValue);
    }

    private async Task LoadCopiesAsync(DuplicateGroupItem? item)
    {
        Copies.Clear();
        if (item is null)
        {
            GroupTitle = GroupDetail = Recommendation = string.Empty;
            return;
        }

        var group = item.Group;
        GroupTitle = item.Title;
        GroupDetail = Strings.Format(group.Kind == DuplicateKind.Exact ? "Duplicates.ExactDetail" : "Duplicates.NearDetail",
            All().IndexOf(item) + 1, All().Count, group.Similarity.ToString("0.00", CultureInfo.CurrentCulture));
        var libraries = (await _host.Catalog.Libraries.ListAsync(CancellationToken.None)).ToDictionary(l => l.Id);
        var copies = group.Members.Take(4).Select((m, i) => new DuplicateCopyViewModel(i + 1, m) { IsRecommended = i == group.Recommended, IsKept = i == group.Recommended }).ToList();
        foreach (var copy in copies)
        {
            Copies.Add(copy);
        }

        foreach (var copy in copies)
        {
            var asset = await _host.Catalog.Assets.GetAsync(copy.Member.AssetId, CancellationToken.None);
            var path = libraries.TryGetValue(copy.Member.Location.LibraryId, out var library) ? Path.Combine(library.RootPath, copy.Member.Location.RelativePath) : null;
            copy.Folder = path is null ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
            var size = path is not null && File.Exists(path) ? new FileInfo(path).Length : 0;
            copy.Facts = string.Join(" · ", new[]
            {
                asset?.Width is { } w && asset.Height is { } h ? $"{w} × {h}" : null,
                size > 0 ? DuplicateFinder.Megabytes(size) : null,
                asset?.Format,
            }.OfType<string>());
            var manual = (await _host.Catalog.Tags.GetForAssetAsync(copy.Member.AssetId, CancellationToken.None)).Count(t => t.Assignment.Source == TagSource.Manual && t.Assignment.State == AssetTagState.Active);
            var rating = (await _host.Catalog.Decisions.GetAsync(copy.Member.AssetId, CancellationToken.None))?.Rating;
            var sidecar = path is not null && File.Exists(Path.ChangeExtension(path, ".xmp"));
            copy.Extras = string.Join(" · ", new[]
            {
                manual > 0 ? Strings.Format("Duplicates.ManualTags", manual) : Strings.Get("Duplicates.NoTags"),
                rating is > 0 ? new string('★', rating.Value) : null,
                sidecar ? Strings.Get("Duplicates.Sidecar") : null,
            }.OfType<string>());
            var thumbnail = asset is null ? null : new[] { asset.FullHash, asset.QuickHash }.OfType<string>().Select(_host.Thumbnails.PathFor).FirstOrDefault(File.Exists);
            copy.Image = thumbnail is null ? null : await Task.Run(() => UiBitmaps.FromFile(thumbnail, 640));
        }

        var recommended = copies.FirstOrDefault(c => c.IsRecommended);
        Recommendation = recommended is null ? string.Empty : Strings.Format("Duplicates.Recommended", recommended.Number, recommended.Name);
    }

    /// <summary>1-4: the copy to keep; the others will be moved.</summary>
    [RelayCommand]
    private void Choose(string? number)
    {
        if (int.TryParse(number, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= Copies.Count)
        {
            foreach (var copy in Copies)
            {
                copy.IsKept = copy.Number == n;
            }
        }
    }

    /// <summary>Enter: the copies not kept move into <c>_duplicati</c> of their library (P5); nothing is deleted.</summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Selected is not { } item || Copies.All(c => !c.IsKept))
        {
            return;
        }

        var group = item.Group;
        var kept = Copies.First(c => c.IsKept).Member;
        var locations = new List<AssetLocation>();
        foreach (var member in group.Members.Where(m => m != kept))
        {
            if (group.Kind == DuplicateKind.Exact)
            {
                locations.Add(member.Location);
            }
            else
            {
                locations.AddRange(await _host.Catalog.Assets.GetLocationsAsync(member.AssetId, CancellationToken.None));
            }
        }

        var (moved, skipped) = await Task.Run(() => new LibraryMover(_host.Catalog, _host.FileSystem, FolderName).MoveAsync(locations, CancellationToken.None));
        Message = Strings.Format("Duplicates.Moved", moved, skipped);
        RemoveSelected();
    }

    /// <summary>I: "not duplicates", never proposed again.</summary>
    [RelayCommand]
    private async Task IgnoreAsync()
    {
        if (Selected is { } item)
        {
            await _host.Catalog.Duplicates.ExcludeAsync(item.Group.Key, CancellationToken.None);
            Message = Strings.Get("Duplicates.Ignored");
            RemoveSelected();
        }
    }

    private void RemoveSelected()
    {
        if (Selected is not { } item)
        {
            return;
        }

        var all = All();
        var index = all.IndexOf(item);
        ExactGroups.Remove(item);
        NearGroups.Remove(item);
        ExactTitle = Strings.Format("Duplicates.Exact", ExactGroups.Count);
        NearTitle = Strings.Format("Duplicates.Near", NearGroups.Count);
        var rest = All();
        Selected = rest.Count == 0 ? null : rest[Math.Min(index, rest.Count - 1)];
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasGroups));
    }
}
