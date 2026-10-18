// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.App.Services.Develop;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Develop;

namespace PhotoFlow.App.ViewModels;

/// <summary>What to export: the working selection, the best of the event, the whole event.</summary>
public sealed partial class ExportScopeOption(string key, string label, IReadOnlyList<long> assetIds) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    public IReadOnlyList<long> AssetIds { get; } = assetIds;

    public string CountText => AssetIds.Count.ToString("N0", CultureInfo.CurrentCulture);

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>A delivery recipe in the list (decision S12).</summary>
public sealed partial class ExportRecipeOption(ExportRecipe recipe) : ObservableObject
{
    public ExportRecipe Recipe { get; } = recipe;

    public string Label => Strings.Get(Recipe.LabelKey);

    public string Description => Strings.Get(Recipe.DescriptionKey);

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>An export of the queue as shown on the page.</summary>
public sealed partial class ExportRunViewModel(ExportRun run) : ObservableObject
{
    public ExportRun Run { get; } = run;

    public string Title => Run.Title;

    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _resultText = string.Empty;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _canOpenFolder;
    [ObservableProperty] private bool _isRunning;

    public void Refresh()
    {
        StateText = Strings.Get("Export.State." + Run.State);
        Percent = Run.Fraction * 100;
        Message = Run.State == ExportState.Running ? Run.Message : string.Empty;
        IsRunning = Run.State == ExportState.Running;
        CanCancel = Run.State is ExportState.Queued or ExportState.Running;
        CanOpenFolder = Run.LastFolder is not null;
        ResultText = Run.State switch
        {
            ExportState.Completed or ExportState.Cancelled when Run.Results.Count > 0 => Strings.Format("Export.Result", Run.Exported, Run.Skipped, Run.Failed),
            ExportState.Failed => Run.Error ?? string.Empty,
            _ => Strings.Format("Export.Photos", Run.Items.Count),
        };
        if (Run.Results.FirstOrDefault(r => r.Outcome == "errore") is { } failed && Run.State != ExportState.Running)
        {
            ResultText += " · " + Path.GetFileName(failed.Path) + ": " + string.Join("; ", failed.Notes);
        }
    }
}

/// <summary>
/// Export (decisions S11, S12): a page with the queue, never a modal window. What to export, a delivery recipe,
/// where, the name, what to do with existing files (chosen before starting, «altro nome» by default); the exports run
/// one at a time in the background while the user goes on working, each photo with its own settings.
/// </summary>
public sealed partial class ExportViewModel : PageViewModel, IDisposable
{
    private static readonly CommandTable Table = new(
    [
        new("Export.Start", new KeyGesture(Key.Enter, KeyModifiers.Control), "Export.Command.Start", StatusGroupKey: "Export.Command.Start"),
        new("Export.Scope", new KeyGesture(Key.D1, KeyModifiers.Alt), "Export.Command.Scope", StatusGroupKey: "Export.Command.Scope", Parameter: "0"),
        new("Export.Scope", new KeyGesture(Key.D2, KeyModifiers.Alt), "Export.Command.Scope", Parameter: "1"),
        new("Export.Scope", new KeyGesture(Key.D3, KeyModifiers.Alt), "Export.Command.Scope", Parameter: "2"),
    ]);

    private readonly PhotoFlowHost _host;
    private readonly IShellNavigator _navigator;
    private JsonObject? _defaults;
    private string? _eventName;

    public ExportViewModel(PhotoFlowHost host, IShellNavigator navigator)
    {
        _host = host;
        _navigator = navigator;
        foreach (var recipe in ExportRecipes.All)
        {
            Recipes.Add(new ExportRecipeOption(recipe));
        }

        SelectedRecipe = Recipes[0];
        host.Exports.Changed += OnRunChanged;
        foreach (var run in host.Exports.Runs)
        {
            var vm = new ExportRunViewModel(run);
            vm.Refresh();
            Runs.Add(vm);
        }
    }

    public override string Title => Strings.Get("Nav.Export");

    public override CommandTable? Commands => Table;

    public ObservableCollection<ExportScopeOption> Scopes { get; } = [];

    public ObservableCollection<ExportRecipeOption> Recipes { get; } = [];

    public ObservableCollection<ExportRunViewModel> Runs { get; } = [];

    public IReadOnlyList<DevelopOption> Formats { get; } =
    [
        new("jpeg", "JPEG"), new("tiff", "TIFF"), new("png", "PNG"), new("dng", Strings.Get("Export.Format.Dng")),
    ];

    public IReadOnlyList<DevelopOption> NonRawFormats { get; } = [new("tiff", "TIFF"), new("jpeg", "JPEG"), new("png", "PNG")];

    public IReadOnlyList<DevelopOption> BitDepths { get; } = [new("8", Strings.Get("Export.Bits.8")), new("16", Strings.Get("Export.Bits.16"))];

    public IReadOnlyList<DevelopOption> Policies { get; } =
    [
        new("rename", Strings.Get("Export.Policy.Rename")), new("skip", Strings.Get("Export.Policy.Skip")), new("overwrite", Strings.Get("Export.Policy.Overwrite")),
    ];

    [ObservableProperty] private ExportScopeOption? _selectedScope;
    [ObservableProperty] private ExportRecipeOption? _selectedRecipe;
    [ObservableProperty] private DevelopOption? _format;
    [ObservableProperty] private DevelopOption? _bitDepth;
    [ObservableProperty] private double _jpegQuality = 92;
    [ObservableProperty] private double _maxSide;
    [ObservableProperty] private bool _stripGps;
    [ObservableProperty] private bool _cameraRaw = true;
    [ObservableProperty] private bool _copyMetadata = true;
    [ObservableProperty] private string _subfolder = string.Empty;
    [ObservableProperty] private string _nameTemplate = "{nome}";
    [ObservableProperty] private bool _nextToOriginals = true;
    [ObservableProperty] private string? _folder;
    [ObservableProperty] private DevelopOption? _policy;
    [ObservableProperty] private DevelopOption? _nonRawFormat;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isPreparing;

    public bool IsDng => Format?.Key == "dng";

    public bool IsJpeg => Format?.Key == "jpeg";

    public bool HasBits => Format?.Key is "tiff" or "png";

    public bool IsCustom => SelectedRecipe?.Recipe.Id == ExportRecipes.Custom;

    public bool ChooseFolder => !NextToOriginals;

    partial void OnNextToOriginalsChanged(bool value) => OnPropertyChanged(nameof(ChooseFolder));

    partial void OnSelectedRecipeChanged(ExportRecipeOption? oldValue, ExportRecipeOption? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }

        if (newValue is null)
        {
            return;
        }

        newValue.IsActive = true;
        var r = newValue.Recipe;
        Format = Formats.First(f => f.Key == r.Format);
        BitDepth = BitDepths.First(b => b.Key == r.BitDepth.ToString(CultureInfo.InvariantCulture));
        JpegQuality = r.JpegQuality;
        MaxSide = r.MaxSide;
        StripGps = r.StripGps;
        CameraRaw = r.CameraRaw;
        Subfolder = r.Folder;
        NameTemplate = r.NameTemplate;
        Policy ??= Policies[0];
        NonRawFormat ??= NonRawFormats[0];
        OnPropertyChanged(nameof(IsCustom));
        UpdateSummary();
    }

    partial void OnFormatChanged(DevelopOption? value)
    {
        OnPropertyChanged(nameof(IsDng));
        OnPropertyChanged(nameof(IsJpeg));
        OnPropertyChanged(nameof(HasBits));
        UpdateSummary();
    }

    partial void OnSelectedScopeChanged(ExportScopeOption? oldValue, ExportScopeOption? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
        }

        _ = UpdateSummaryAsync();
    }

    public override async Task ActivateAsync()
    {
        Message = null;
        var set = _host.WorkingSet;
        Scopes.Clear();
        if (!set.IsEmpty)
        {
            Scopes.Add(new("selection", Strings.Format("Export.Scope.Selection", set.Origin), set.AssetIds));
        }

        var jobs = await _host.Catalog.Jobs.ListRecentAsync(1, CancellationToken.None);
        if (jobs.FirstOrDefault() is { } job)
        {
            _eventName = job.EventName;
            var best = await DevelopSources.BestOfJobAsync(_host.Catalog, job.Id, CancellationToken.None);
            var name = job.EventName ?? Strings.Get("Job.Unnamed");
            Scopes.Add(new("best", Strings.Format("Export.Scope.Best", name), best));
            var items = await _host.Catalog.Jobs.GetItemsAsync(job.Id, CancellationToken.None);
            Scopes.Add(new("event", Strings.Format("Export.Scope.Event", name),
                [.. items.Where(i => i.AssetId is not null).Select(i => i.AssetId!.Value).Distinct()]));
        }

        _eventName = set.EventName ?? _eventName;
        SelectedScope = Scopes.FirstOrDefault(s => s.AssetIds.Count > 0) ?? Scopes.FirstOrDefault();
        await UpdateSummaryAsync();
    }

    private void UpdateSummary() => _ = UpdateSummaryAsync();

    private async Task UpdateSummaryAsync()
    {
        if (SelectedScope is not { } scope)
        {
            Summary = Strings.Get("Export.Empty");
            return;
        }

        var developed = await _host.Catalog.Develop.ListDevelopedAsync(scope.AssetIds.ToList(), CancellationToken.None);
        var exported = await _host.Catalog.Develop.LastExportsAsync(scope.AssetIds.ToList(), CancellationToken.None);
        var never = scope.AssetIds.Count - developed.Count;
        Summary = Strings.Format("Export.Summary", scope.AssetIds.Count, developed.Count, never, exported.Count)
                  + (IsDng ? " " + Strings.Get("Export.DngNote") : string.Empty);
    }

    // ------------------------------------------------------------------ commands

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Export.Start" => StartCommand,
        "Export.Scope" => ChooseScopeCommand,
        _ => base.ResolveCommand(id),
    };

    [RelayCommand]
    private void ChooseScope(string? index)
    {
        if (int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < Scopes.Count)
        {
            SelectedScope = Scopes[i];
        }
    }

    [RelayCommand]
    private void ChooseRecipe(ExportRecipeOption? recipe)
    {
        if (recipe is not null)
        {
            SelectedRecipe = recipe;
        }
    }

    [RelayCommand]
    private void ChooseScopeOption(ExportScopeOption? scope)
    {
        if (scope is not null)
        {
            SelectedScope = scope;
        }
    }

    /// <summary>The <c>output</c> settings of the engine from the choices of the page.</summary>
    public JsonObject Output()
    {
        var folder = NextToOriginals ? Subfolder.Trim() : Path.Combine(Folder ?? string.Empty, Subfolder.Trim());
        return new JsonObject
        {
            ["format"] = Format?.Key ?? "jpeg",
            ["bit_depth"] = int.Parse(BitDepth?.Key ?? "8", CultureInfo.InvariantCulture),
            ["jpeg_quality"] = (int)Math.Round(JpegQuality),
            ["max_side"] = (int)Math.Round(MaxSide),
            ["strip_gps"] = StripGps,
            ["camera_raw"] = CameraRaw,
            ["copy_metadata"] = CopyMetadata,
            ["folder"] = folder,
            ["name_template"] = string.IsNullOrWhiteSpace(NameTemplate) ? "{nome}" : NameTemplate.Trim(),
            ["overwrite"] = false,
        };
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (SelectedScope is not { AssetIds.Count: > 0 } scope || SelectedRecipe is not { } recipe)
        {
            Message = Strings.Get("Export.Empty");
            return;
        }

        if (!NextToOriginals && string.IsNullOrWhiteSpace(Folder))
        {
            Message = Strings.Get("Export.ChooseFolderFirst");
            return;
        }

        IsPreparing = true;
        try
        {
            _defaults ??= (await _host.DevelopEngine.HelloAsync(CancellationToken.None)).Defaults;
            var items = new List<DevelopExportItem>();
            var seq = 0;
            foreach (var id in scope.AssetIds)
            {
                if (await AssetFiles.LocateAsync(_host.Catalog, _host.FileSystem, id, CancellationToken.None) is not { } path)
                {
                    continue;
                }

                seq++;
                var stored = await _host.Catalog.Develop.GetSettingsAsync(id, CancellationToken.None);
                var settings = stored is not null ? JsonNode.Parse(stored) as JsonObject : null;
                var decision = await _host.Catalog.Decisions.GetAsync(id, CancellationToken.None);
                var values = new Dictionary<string, string>
                {
                    ["evento"] = _eventName ?? string.Empty,
                    ["stelle"] = (decision?.Rating ?? 0).ToString(CultureInfo.InvariantCulture),
                    ["seq"] = seq.ToString("000", CultureInfo.InvariantCulture),
                };
                items.Add(new DevelopExportItem(id, path, DevelopJson.ForEngine(settings ?? (JsonObject)_defaults.DeepClone()), values, Auto: settings is null));
            }

            if (items.Count == 0)
            {
                Message = Strings.Get("Develop.Missing");
                return;
            }

            var title = Strings.Format("Export.RunTitle", recipe.Label, items.Count, scope.Label);
            _host.Exports.Enqueue(title, recipe.Recipe.Id, items, Output(), Policy?.Key ?? "rename", NonRawFormat?.Key ?? "tiff");
            Message = Strings.Format("Export.Queued", items.Count);
        }
        catch (DevelopEngineException e)
        {
            Message = e.Message;
        }
        finally
        {
            IsPreparing = false;
        }
    }

    [RelayCommand]
    private static void Cancel(ExportRunViewModel? run) => run?.Run.Cancel();

    [RelayCommand]
    private static void OpenFolder(ExportRunViewModel? run)
    {
        if (run?.Run.LastFolder is { } folder && Directory.Exists(folder))
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    /// <summary>The folder chosen in the picker of the view.</summary>
    public void SetFolder(string folder)
    {
        Folder = folder;
        NextToOriginals = false;
    }

    private void OnRunChanged(object? sender, ExportRun run) => Ui.Post(() =>
    {
        var vm = Runs.FirstOrDefault(r => r.Run == run);
        if (vm is null)
        {
            vm = new ExportRunViewModel(run);
            Runs.Insert(0, vm);
        }

        vm.Refresh();
        if (run.State == ExportState.Completed)
        {
            _ = UpdateSummaryAsync();
        }
    });

    public void Dispose() => _host.Exports.Changed -= OnRunChanged;
}
