// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;

namespace PhotoFlow.App.ViewModels;

/// <summary>Window shell: navigation, title bar, status bar, and the page of the selected section.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IShellNavigator, IDisposable
{
    private readonly PhotoFlowHost? _host;
    private readonly Dictionary<string, PageViewModel> _pages = [];
    /// <summary>At or below this width the navigation collapses to icons automatically (board 04).</summary>
    public const double AutoCompactWidth = 1280;

    public MainWindowViewModel(PhotoFlowHost? host = null)
    {
        _host = host;
        Commands = GlobalCommands.Create();
        _globalHints = Commands.CreateStatusHints();
        _statusHints = _globalHints;
        MainSections = SectionCatalog.All.Where(s => !s.IsSecondary).ToList();
        SecondarySections = SectionCatalog.All.Where(s => s.IsSecondary).ToList();
        _selectedSection = SectionCatalog.All[0];
        _currentPage = PageFor(_selectedSection);
    }

    /// <summary>Content of the main area: the page of the section, or a flow opened from it (import procedure).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    private PageViewModel _currentPage;

    public string PageTitle => CurrentPage.Title;

    public void ShowSection(string sectionId) => Navigate(sectionId);

    public void ShowPage(PageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        CurrentPage = page;
        _ = page.ActivateAsync();
    }

    public void OpenReview(long jobId)
    {
        var section = SectionCatalog.All.First(s => s.Id == SectionCatalog.Review);
        if (PageFor(section) is ReviewViewModel review)
        {
            review.JobId = jobId;
        }

        Navigate(section.Id);
    }

    public void Dispose()
    {
        foreach (var page in _pages.Values.OfType<IDisposable>())
        {
            page.Dispose();
        }
    }

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        StatusHints = [.. value.Commands?.CreateStatusHints() ?? [], .. _globalHints];
        OnPropertyChanged(nameof(LibraryName));
    }

    partial void OnSelectedSectionChanged(Section value)
    {
        CurrentPage = PageFor(value);
        _ = CurrentPage.ActivateAsync();
    }

    private PageViewModel PageFor(Section section)
    {
        if (_pages.TryGetValue(section.Id, out var page))
        {
            return page;
        }

        page = (_host, section.Id) switch
        {
            ({ } host, SectionCatalog.Home) => new HomeViewModel(host, this, (card, folder) => new ImportWizardViewModel(host, this, card, folder)),
            ({ } host, SectionCatalog.Jobs) => new JobsViewModel(host, this),
            ({ } host, SectionCatalog.Review) => new ReviewViewModel(host, this),
            ({ } host, SectionCatalog.Libraries) => new LibrariesViewModel(host, this),
            ({ } host, SectionCatalog.People) => new PeopleViewModel(host, this),
            ({ } host, SectionCatalog.Profiles) => new ProfilesViewModel(host),
            ({ } host, SectionCatalog.Browse) => new BrowseViewModel(host) { Navigator = this },
            ({ } host, SectionCatalog.Duplicates) => new DuplicatesViewModel(host),
            ({ } host, SectionCatalog.Vocabulary) => new VocabularyViewModel(host),
            ({ } host, SectionCatalog.Settings) => new SettingsViewModel(host) { Navigator = this },
            _ => new PlaceholderPageViewModel(section, PlaceholderPageViewModel.Phases.GetValueOrDefault(section.Id, "1")),
        };
        _pages[section.Id] = page;
        return page;
    }

    public event EventHandler? SearchFocusRequested;

    public CommandTable Commands { get; }

    private readonly IReadOnlyList<StatusHint> _globalHints;

    /// <summary>Hints of the current page first, then the global ones (both from their command tables).</summary>
    [ObservableProperty]
    private IReadOnlyList<StatusHint> _statusHints;

    public IReadOnlyList<Section> MainSections { get; }

    public IReadOnlyList<Section> SecondarySections { get; }

    public string LibraryName => _host?.Settings.Import.PrimaryRoot is { } root ? Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) : Strings.Get("TitleBar.NoLibrary");

    /// <summary>Right side of the status bar: jobs, inference backend, catalog (board 3a).</summary>
    [ObservableProperty]
    private string _statusText = Strings.Get("Status.Ready");

    public void RefreshStatus()
    {
        if (_host is null)
        {
            return;
        }

        var setup = _host.DescribeAnalysis();
        var restore = _host.RestoreOutcome switch
        {
            { Restored: true } r => Strings.Format("Status.Restored", Path.GetFileName(r.Backup)) + " · ",
            { } r => Strings.Format("Status.RestoreFailed", r.Error ?? string.Empty) + " · ",
            null => string.Empty,
        };
        var update = _host.Updates.AvailableVersion is { } version ? Strings.Format("Status.UpdateAvailable", version) + " · " : string.Empty;
        StatusText = update + restore + Strings.Format("Status.Summary", setup.Backend == Core.Inference.ExecutionBackend.Cuda ? "CUDA" : "CPU",
            Strings.Get(setup.EyesAvailable ? "Status.Faces.Eyes" : setup.FaceDetector is null ? "Status.Faces.None" : "Status.Faces.NoEyes"));
    }

    public string SearchShortcut => CommandTable.FormatGesture(Commands.Commands.First(c => c.Id == GlobalCommands.FocusSearch).Gesture);

    public string NavigationToggleIcon => IsNavigationCompact ? "PF.Icon.ChevronRight" : "PF.Icon.ChevronLeft";

    public string NavigationToggleTip => Strings.Get(IsNavigationCompact ? "Nav.Expand" : "Nav.Collapse");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedMainSection), nameof(SelectedSecondarySection))]
    private Section _selectedSection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNavigationCompact), nameof(NavigationToggleIcon), nameof(NavigationToggleTip))]
    private bool _isNavigationCollapsedByUser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNavigationCompact), nameof(NavigationToggleIcon), nameof(NavigationToggleTip))]
    private double _windowWidth = double.MaxValue;

    public bool IsNavigationCompact => IsNavigationCollapsedByUser || WindowWidth <= AutoCompactWidth;

    /// <summary>Selection proxy for the main list (null when a secondary section is active).</summary>
    public Section? SelectedMainSection
    {
        get => SelectedSection.IsSecondary ? null : SelectedSection;
        set
        {
            if (value is not null)
            {
                SelectedSection = value;
            }
        }
    }

    /// <summary>Selection proxy for the secondary list.</summary>
    public Section? SelectedSecondarySection
    {
        get => SelectedSection.IsSecondary ? SelectedSection : null;
        set
        {
            if (value is not null)
            {
                SelectedSection = value;
            }
        }
    }

    public ICommand ResolveCommand(string id) => id switch
    {
        GlobalCommands.Navigate => NavigateCommand,
        GlobalCommands.FocusSearch => FocusSearchCommand,
        GlobalCommands.OpenSettings => OpenSettingsCommand,
        GlobalCommands.ImportFolder => ImportFolderCommand,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown command."),
    };

    [RelayCommand]
    private void Navigate(string? sectionId)
    {
        var section = SectionCatalog.All.FirstOrDefault(s => s.Id == sectionId);
        if (section is null)
        {
            return;
        }

        if (section == SelectedSection)
        {
            // Back to the section page from a flow opened inside it (e.g. the import procedure).
            CurrentPage = PageFor(section);
            _ = CurrentPage.ActivateAsync();
        }
        else
        {
            SelectedSection = section;
        }
    }

    [RelayCommand]
    private void FocusSearch()
    {
        // In Consultazione Ctrl K goes to its own search field (tokens, board 4d).
        if (CurrentPage is BrowseViewModel browse)
        {
            browse.RequestSearchFocus();
        }
        else
        {
            SearchFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Text of the search field in the title bar ("Cerca ovunque").</summary>
    [ObservableProperty]
    private string? _globalSearchText;

    /// <summary>Enter in the title bar: Consultazione with that text.</summary>
    [RelayCommand]
    private async Task SearchEverywhereAsync()
    {
        var text = GlobalSearchText;
        GlobalSearchText = null;
        ShowSection(SectionCatalog.Browse);
        if (CurrentPage is BrowseViewModel browse && !string.IsNullOrWhiteSpace(text))
        {
            await browse.SearchTextAsync(text);
        }
    }

    [RelayCommand]
    private void OpenSettings() => Navigate(SectionCatalog.Settings);

    /// <summary>Ctrl I from anywhere: Home, then the folder picker of its view.</summary>
    [RelayCommand]
    private void ImportFolder()
    {
        if (_host is null)
        {
            return;
        }

        Navigate(SectionCatalog.Home);
        if (CurrentPage is HomeViewModel { IsNormal: true } home)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => home.RequestFolderCommand.Execute(null));
        }
    }

    [RelayCommand]
    private void ToggleNavigation() => IsNavigationCollapsedByUser = !IsNavigationCollapsedByUser;
}
