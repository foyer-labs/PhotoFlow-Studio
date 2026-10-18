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
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;

namespace PhotoFlow.App.ViewModels;

/// <summary>A library of the list on the left (board 5a).</summary>
public sealed partial class LibraryItemViewModel(Library library) : ObservableObject
{
    public Library Library { get; set; } = library;

    public string Name => Library.Name;

    [ObservableProperty]
    private string _count = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;
}

/// <summary>
/// Libraries (boards 5a, 5b2): the folders where photos live, their formats, the first in-place analysis in background
/// (low priority, files only read) and the XMP policy — switching an archive to embedded XMP needs an explicit
/// confirmation with the number of files involved (D21).
/// </summary>
public sealed partial class LibrariesViewModel : PageViewModel
{
    private static readonly HashSet<string> RawFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "NEF", "NRW", "CR2", "CR3", "ARW", "RAF", "DNG", "ORF", "RW2", "PEF", "SRW",
    };

    private static readonly CommandTable Table = new(
    [
        new("Libraries.New", new KeyGesture(Key.N, KeyModifiers.Control), "Libraries.Command.New", StatusGroupKey: "Libraries.Command.New"),
        new("Libraries.Analyze", new KeyGesture(Key.Enter, KeyModifiers.Control), "Libraries.Command.Analyze", StatusGroupKey: "Libraries.Command.Analyze"),
        new("Libraries.Refresh", new KeyGesture(Key.F5), "Libraries.Command.Refresh", StatusGroupKey: "Libraries.Command.Refresh"),
    ]);

    private readonly PhotoFlowHost _host;
    private readonly IShellNavigator _navigator;

    /// <summary>Raised when the view should open the file picker of a .pfcatalog (board 5b2).</summary>
    public event EventHandler? CatalogPickRequested;

    [RelayCommand]
    private void RequestImportCatalog() => CatalogPickRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Regeneration of the selected library (spec §5).</summary>
    [RelayCommand]
    private void Regenerate()
    {
        if (Selected is { } library)
        {
            _navigator.ShowPage(new RegenerationViewModel(_host, _navigator, libraryId: library.Library.Id));
        }
    }

    /// <summary>The import continues in Settings › Catalog (remapping of the folders, password of the fingerprints).</summary>
    public void ImportCatalog(string path)
    {
        _host.PendingCatalogImport = path;
        _navigator.ShowSection(SectionCatalog.Settings);
    }

    public LibrariesViewModel(PhotoFlowHost host, IShellNavigator navigator)
    {
        _host = host;
        _navigator = navigator;
        Profiles = host.Profiles.All.Select(p => new ProfileChoice(p)).ToList();
        _analysisProfile = Profiles.First(p => p.Profile.Id == "archive");
    }

    public override string Title => Strings.Get("Nav.Libraries");

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Libraries.New" => RequestNewCommand,
        "Libraries.Analyze" => AnalyzeCommand,
        "Libraries.Refresh" => RefreshCommand,
        _ => base.ResolveCommand(id),
    };

    /// <summary>The view shows the folder picker and calls <see cref="AddLibraryAsync"/>.</summary>
    public event EventHandler? FolderPickRequested;

    public ObservableCollection<LibraryItemViewModel> Libraries { get; } = [];

    public IReadOnlyList<ProfileChoice> Profiles { get; }

    public IReadOnlyList<ArchiveOrder> Orders { get; } = [ArchiveOrder.NewestFirst, ArchiveOrder.ByFolder];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(PolicyEmbedded), nameof(PolicySidecar), nameof(PolicyCatalog), nameof(IsReadOnly))]
    private LibraryItemViewModel? _selected;

    [ObservableProperty]
    private string _formats = string.Empty;

    [ObservableProperty]
    private ArchiveOrder _order = ArchiveOrder.NewestFirst;

    [ObservableProperty]
    private ProfileChoice _analysisProfile;

    [ObservableProperty]
    private string? _message;

    /// <summary>Pending switch to embedded XMP, waiting for the confirmation (D21).</summary>
    [ObservableProperty]
    private string? _embedConfirmation;

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => Libraries.Count == 0;

    public bool PolicyEmbedded
    {
        get => Selected?.Library.XmpPolicy == WritePolicy.EmbeddedForNonRaw;
        set
        {
            if (value)
            {
                _ = RequestPolicyAsync(WritePolicy.EmbeddedForNonRaw);
            }
        }
    }

    public bool PolicySidecar
    {
        get => Selected?.Library.XmpPolicy == WritePolicy.SidecarOnly;
        set
        {
            if (value)
            {
                _ = RequestPolicyAsync(WritePolicy.SidecarOnly);
            }
        }
    }

    public bool PolicyCatalog
    {
        get => Selected?.Library.XmpPolicy == WritePolicy.CatalogOnly;
        set
        {
            if (value)
            {
                _ = RequestPolicyAsync(WritePolicy.CatalogOnly);
            }
        }
    }

    public bool IsReadOnly
    {
        get => Selected?.Library.IsReadOnly ?? false;
        set => _ = SetReadOnlyAsync(value);
    }

    public override async Task ActivateAsync()
    {
        // Read first, then replace the list without awaiting in between (overlapping refreshes stay consistent).
        var items = new List<LibraryItemViewModel>();
        foreach (var library in await _host.Catalog.Libraries.ListAsync(CancellationToken.None))
        {
            items.Add(new LibraryItemViewModel(library)
            {
                Count = (await _host.Catalog.Libraries.CountAssetsAsync(library.Id, CancellationToken.None)).ToString("N0", CultureInfo.CurrentCulture),
                Detail = $"{library.RootPath} · {Strings.Get(library.IsReadOnly ? "Library.Policy.CatalogOnly" : "Library.Policy." + library.XmpPolicy)}",
            });
        }

        var selectedId = Selected?.Library.Id;
        Libraries.Clear();
        foreach (var item in items)
        {
            Libraries.Add(item);
        }

        Selected = Libraries.FirstOrDefault(l => l.Library.Id == selectedId) ?? Libraries.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedChanged(LibraryItemViewModel? value)
    {
        EmbedConfirmation = null;
        _ = LoadFormatsAsync(value);
    }

    /// <summary>A new library on a chosen folder: an existing archive, so sidecar only (D21), analysis offered.</summary>
    public async Task AddLibraryAsync(string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(folder);
        if (await _host.Catalog.Libraries.FindByRootAsync(root, CancellationToken.None) is { } existing)
        {
            await ActivateAsync();
            Selected = Libraries.FirstOrDefault(l => l.Library.Id == existing.Id);
            return;
        }

        var name = Path.GetFileName(root) is { Length: > 0 } leaf ? leaf : root;
        var id = await _host.Catalog.Libraries.AddAsync(new Library(0, name, root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), CancellationToken.None);
        await ActivateAsync();
        Selected = Libraries.FirstOrDefault(l => l.Library.Id == id);
    }

    [RelayCommand]
    private void RequestNew() => FolderPickRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private Task RefreshAsync() => ActivateAsync();

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (Selected is not { } library)
        {
            return;
        }

        if (!_host.FileSystem.DirectoryExists(library.Library.RootPath))
        {
            Message = Strings.Format("Libraries.RootMissing", library.Library.RootPath);
            return;
        }

        await _host.StartArchiveAnalysisAsync(library.Library.Id, AnalysisProfile.Profile.Id, Order, CancellationToken.None);
        _navigator.ShowSection(SectionCatalog.Jobs);
    }

    [RelayCommand]
    private async Task ConfirmEmbedAsync()
    {
        EmbedConfirmation = null;
        await ApplyPolicyAsync(WritePolicy.EmbeddedForNonRaw);
    }

    [RelayCommand]
    private void CancelEmbed()
    {
        EmbedConfirmation = null;
        OnPropertyChanged(nameof(PolicyEmbedded));
        OnPropertyChanged(nameof(PolicySidecar));
        OnPropertyChanged(nameof(PolicyCatalog));
    }

    private async Task RequestPolicyAsync(WritePolicy policy)
    {
        if (Selected is not { } library || library.Library.XmpPolicy == policy)
        {
            return;
        }

        if (policy == WritePolicy.EmbeddedForNonRaw)
        {
            // D21: writing inside the originals of a library needs the number of files and the _original copy stated.
            var formats = await _host.Catalog.Libraries.CountFormatsAsync(library.Library.Id, CancellationToken.None);
            var files = formats.Where(f => !RawFormats.Contains(f.Key)).Sum(f => f.Value);
            EmbedConfirmation = Strings.Format("Libraries.EmbedConfirm", files.ToString("N0", CultureInfo.CurrentCulture));
            return;
        }

        await ApplyPolicyAsync(policy);
    }

    private async Task ApplyPolicyAsync(WritePolicy policy)
    {
        if (Selected is not { } library)
        {
            return;
        }

        await _host.Catalog.Libraries.UpdatePolicyAsync(library.Library.Id, policy, library.Library.IsReadOnly, CancellationToken.None);
        library.Library = library.Library with { XmpPolicy = policy };
        await ActivateAsync();
    }

    private async Task SetReadOnlyAsync(bool value)
    {
        if (Selected is not { } library || library.Library.IsReadOnly == value)
        {
            return;
        }

        await _host.Catalog.Libraries.UpdatePolicyAsync(library.Library.Id, library.Library.XmpPolicy, value, CancellationToken.None);
        await ActivateAsync();
    }

    private async Task LoadFormatsAsync(LibraryItemViewModel? library)
    {
        if (library is null)
        {
            Formats = string.Empty;
            return;
        }

        var formats = await _host.Catalog.Libraries.CountFormatsAsync(library.Library.Id, CancellationToken.None);
        var total = formats.Values.Sum();
        Formats = total == 0
            ? Strings.Get("Libraries.NotAnalyzed")
            : string.Join(" · ", formats.OrderByDescending(f => f.Value).Select(f => string.Create(CultureInfo.CurrentCulture, $"{f.Key} {(double)f.Value / total:P0}")));
    }
}
