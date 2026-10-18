// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.App.Services.Vlm;
using PhotoFlow.Analysis.Quality;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Inference.Gpu;
using PhotoFlow.Inference.Models;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Ingest;

namespace PhotoFlow.App.ViewModels;

/// <summary>A model of the manifest in the Models tab (board 5g).</summary>
public sealed partial class ModelRowViewModel(ModelInfo model, bool available) : ObservableObject
{
    public ModelInfo Model { get; } = model;

    public string Function => Strings.Get("Models.Function." + Model.Function);

    public string Name => Model.Name;

    public string LicenseText => Model.License.CommercialUse
        ? Strings.Format("Models.License.Commercial", Model.License.Spdx)
        : Strings.Get("Models.License.NonCommercialOnly");

    public string LicenseUrl => Model.License.Source;

    public string SizeText => Formats.Bytes(Model.DownloadBytes);

    /// <summary>False for models of later phases: shown, not downloadable ("Arriva con la fase N", F1-6).</summary>
    public bool IsAvailable { get; } = available;

    public bool RequiresAcceptance => Model.License.RequiresAcceptance && IsAvailable;

    public bool CanInstall => IsAvailable && !IsInstalled && !IsManualConvert;

    /// <summary>Downloaded by the user and converted here (phase 5b, U2): no download button, a conversion instead.</summary>
    public bool IsManualConvert => Model.ManualConvert && IsAvailable;

    /// <summary>Only downloaded models can be verified against the manifest (a converted one has other files).</summary>
    public bool CanVerify => IsInstalled && !Model.ManualConvert;

    public bool CanConvert => IsManualConvert && !IsConverting;

    /// <summary>The license can be ticked until the model is there (a converted model is accepted before its download).</summary>
    public bool CanAcceptLicense => IsAvailable && !IsInstalled;

    /// <summary>PhotoFlow fetches the original files of a model to convert into its source folder (pinned, SHA-256 checked).</summary>
    public bool CanDownloadSource => IsManualConvert && !IsInstalled && !IsConverting;

    /// <summary>The files to put in the source folder.</summary>
    public string SourceFilesText => string.Join(", ", Model.Files.Select(f => f.Path));

    /// <summary>Where the original files come from and how big they are, then what the conversion needs.</summary>
    public string ManualNote => Strings.Format(Model.Function == ModelFunction.Vlm ? "Settings.Models.ManualNote" : "Settings.Models.ManualNoteSmall",
        Model.License.Source.Replace("https://huggingface.co/", string.Empty, StringComparison.Ordinal), SizeText);

    /// <summary>Where the user puts the original files.</summary>
    [ObservableProperty]
    private string _sourceFolder = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConvert), nameof(CanRemoveEnvironment), nameof(CanDownloadSource))]
    private bool _isConverting;

    /// <summary>The private Python environment of the conversion is there (~5 GB, removable once converted).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveEnvironment))]
    private bool _environmentExists;

    public bool CanRemoveEnvironment => IsManualConvert && EnvironmentExists && !IsConverting && !ConfirmingRemoval;

    /// <summary>The removal of the environment waits for a second, explicit confirmation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveEnvironment))]
    private bool _confirmingRemoval;

    /// <summary>The step of the conversion, or its outcome.</summary>
    [ObservableProperty]
    private string? _conversionText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(CanVerify), nameof(Status), nameof(CanAcceptLicense), nameof(CanDownloadSource), nameof(CanUse))]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _licenseAccepted;

    [ObservableProperty]
    private string? _verification;

    /// <summary>The model the analysis uses now for its function.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUse))]
    private bool _isInUse;

    /// <summary>Another installed model of a function with alternatives (detection, faces) can replace the one in use.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUse))]
    private bool _hasAlternative;

    public bool CanUse => IsInstalled && HasAlternative && !IsInUse;

    public string Status => !IsAvailable
        ? Strings.Format("Settings.ComesWithPhase", Model.Phase.ToString(CultureInfo.InvariantCulture))
        : Verification ?? Strings.Get(IsInstalled ? "Models.Status.Installed" : Model.ManualConvert ? "Models.Status.ToConvert" : "Models.Status.NotDownloaded");

    partial void OnVerificationChanged(string? value) => OnPropertyChanged(nameof(Status));
}

/// <summary>A choice of a combo box with a localized label.</summary>
public sealed record SettingChoice<T>(T Value, string Label);

/// <summary>What the view asks the user to pick for the settings.</summary>
public enum SettingsPick
{
    OfflinePackage,
    ExifTool,
    Editor,
    ExportCatalog,
    ImportCatalog,
    ImportRoot,
    MoveCatalog,
    OpenCatalog,
}

/// <summary>A library of a catalog to import, with the folder where its photos are now (spec §9: remapping).</summary>
public sealed partial class ImportLibraryRow(PackageLibrary library) : ObservableObject
{
    public PackageLibrary Library { get; } = library;

    public string Title => $"{Library.Name} · {Library.Assets.ToString("N0", CultureInfo.CurrentCulture)}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Found))]
    private string _newRoot = library.RootPath;

    /// <summary>"✓" when the folder exists here, "?" otherwise.</summary>
    public string Found => Directory.Exists(NewRoot) ? "✓" : "?";
}

/// <summary>
/// Settings (boards 5f-5j) in tabs: hardware and GPU pack, models with licenses and offline package, catalog and
/// backups, general (language, theme, exiftool, path template), culling mode. Changes are saved at once; functions of
/// later phases are visible and disabled (F1-6). Licenses are never accepted on the user's behalf.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public const int HardwareTab = 0;
    public const int ModelsTab = 1;
    public const int CatalogTab = 2;
    public const int GeneralTab = 3;
    public const int CullingTab = 4;

    private static readonly CommandTable Table = new(
    [
        new("Settings.NextTab", new KeyGesture(Key.Tab, KeyModifiers.Control), "Settings.Command.NextTab", StatusGroupKey: "Settings.Command.NextTab"),
        new("Settings.PreviousTab", new KeyGesture(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift), "Settings.Command.PreviousTab"),
    ]);

    private static readonly PathTemplateContext Example = new("Matrimonio Sara e Luca", new DateOnly(2026, 10, 12), 1, "Z5_2", "DSC_0001", "NEF");

    private readonly PhotoFlowHost _host;
    private bool _loading;

    public SettingsViewModel(PhotoFlowHost host)
    {
        _host = host;
        Tabs = ["Settings.Tab.Hardware", "Settings.Tab.Models", "Settings.Tab.Catalog", "Settings.Tab.General", "Settings.Tab.Culling"];
        Languages =
        [
            new(null, Strings.Get("Settings.Language.System")),
            new("it", "Italiano"),
            new("en", "English"),
        ];
        Themes = Enum.GetValues<ThemePreference>().Select(t => new SettingChoice<ThemePreference>(t, Strings.Get("Settings.Theme." + t))).ToList();
        Models = new ObservableCollection<ModelRowViewModel>(host.Models.Models
            .OrderBy(m => m.Phase).ThenBy(m => m.Function)
            .Select(m => new ModelRowViewModel(m, PhotoFlowHost.ImplementedAdapters.Contains(m.Adapter))));
        _pathTemplate = host.Settings.Import.PathTemplate;
        _language = Languages.FirstOrDefault(l => l.Value == host.Settings.Language) ?? Languages[0];
        _theme = Themes.First(t => t.Value == host.Settings.Theme);
        UpdateTemplateExample();
    }

    public override string Title => Strings.Get("Nav.Settings");

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Settings.NextTab" => NextTabCommand,
        "Settings.PreviousTab" => PreviousTabCommand,
        _ => base.ResolveCommand(id),
    };

    /// <summary>The view shows a folder or file picker and calls back (offline package, exiftool).</summary>
    public event EventHandler<SettingsPick>? PickRequested;

    /// <summary>Applies a theme to the running application (set by the view; no-op in tests).</summary>
    public Action<ThemePreference>? ApplyTheme { get; set; }

    public IReadOnlyList<string> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHardware), nameof(IsModels), nameof(IsCatalog), nameof(IsGeneral), nameof(IsCulling))]
    private int _selectedTab;

    public bool IsHardware => SelectedTab == HardwareTab;

    public bool IsModels => SelectedTab == ModelsTab;

    public bool IsCatalog => SelectedTab == CatalogTab;

    public bool IsGeneral => SelectedTab == GeneralTab;

    public bool IsCulling => SelectedTab == CullingTab;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    // Hardware (5f)

    [ObservableProperty]
    private string _backendText = string.Empty;

    [ObservableProperty]
    private string _gpuText = string.Empty;

    [ObservableProperty]
    private string _cpuText = string.Empty;

    [ObservableProperty]
    private bool _hasGpu;

    [ObservableProperty]
    private bool _gpuPackInstalled;

    [ObservableProperty]
    private bool _nvidiaLicensesAccepted;

    public string GpuPackSize => Formats.Bytes(GpuRuntimeManifest.Embedded().TotalBytes);

    public string WorkersText => Strings.Format("Settings.Hardware.Workers",
        PipelineDefaults.DefaultAnalysisWorkers, Math.Max(1, PipelineDefaults.DefaultAnalysisWorkers / 2));

    // Models (5g)

    public ObservableCollection<ModelRowViewModel> Models { get; }

    [ObservableProperty]
    private string _modelsFolderText = string.Empty;

    /// <summary>The models that can make the fingerprints of the people now (P22): installed and accepted.</summary>
    public ObservableCollection<SettingChoice<string>> Recognizers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeRecognizer))]
    private SettingChoice<string>? _selectedRecognizer;

    /// <summary>The recognizer the analysis uses now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeRecognizer), nameof(HasRecognizerChoice))]
    private string? _recognizerInUse;

    /// <summary>There is a face detector and more than one recognizer to pick.</summary>
    public bool HasRecognizerChoice => RecognizerInUse is not null && Recognizers.Count > 1;

    public bool CanChangeRecognizer => SelectedRecognizer is { } choice && choice.Value != RecognizerInUse;

    // Catalog (5h)

    [ObservableProperty]
    private string _catalogText = string.Empty;

    [ObservableProperty]
    private string _schemaText = string.Empty;

    [ObservableProperty]
    private string _lastBackupText = string.Empty;

    public string BackupsText => Strings.Format("Settings.Catalog.Keep", MaintenanceService.KeepBackups, _host.Paths.BackupsDirectory);

    public ObservableCollection<SettingChoice<CatalogBackup>> Backups { get; } = [];

    [ObservableProperty]
    private SettingChoice<CatalogBackup>? _selectedBackup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingRestore))]
    private string? _pendingRestoreText;

    public bool HasPendingRestore => PendingRestoreText is not null;

    // General (5i)

    public IReadOnlyList<SettingChoice<string?>> Languages { get; }

    [ObservableProperty]
    private SettingChoice<string?> _language;

    public IReadOnlyList<SettingChoice<ThemePreference>> Themes { get; }

    [ObservableProperty]
    private SettingChoice<ThemePreference> _theme;

    [ObservableProperty]
    private string _exifToolText = string.Empty;

    [ObservableProperty]
    private string _exifToolHealth = string.Empty;

    [ObservableProperty]
    private string _pathTemplate;

    [ObservableProperty]
    private string _templateExample = string.Empty;

    [ObservableProperty]
    private string? _templateError;

    public IReadOnlyList<string> TemplateVariables { get; } = ["{yyyy}", "{MM}", "{yyyy-MM-dd}", "{evento}", "{seq:0000}", "{camera}", "{nomeoriginale}", "{ext}"];

    // Benchmark (5f)

    public ObservableCollection<BenchmarkRow> BenchmarkRows { get; } = [];

    [ObservableProperty]
    private string _benchmarkTitle = string.Empty;

    [ObservableProperty]
    private string _benchmarkEstimates = string.Empty;

    [ObservableProperty]
    private string? _benchmarkProgress;

    public bool HasBenchmark => BenchmarkRows.Count > 0;

    [RelayCommand]
    private async Task RunBenchmarkAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => BenchmarkProgress = Strings.Format("Settings.Benchmark.Running", p.Done, p.Total));
            var result = await Task.Run(() => new BenchmarkRunner(_host).RunAsync(BenchmarkRunner.DefaultPhotos, progress, CancellationToken.None));
            await _host.UpdateSettingsAsync(s => s with { Benchmark = result }, CancellationToken.None);
            await ShowBenchmarkAsync(result);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or ImageDecodeException)
        {
            Message = Strings.Format("Settings.Benchmark.Failed", e.Message);
        }
        finally
        {
            BenchmarkProgress = null;
            IsBusy = false;
        }
    }

    private async Task ShowBenchmarkAsync(BenchmarkResult? result)
    {
        BenchmarkRows.Clear();
        if (result is null)
        {
            BenchmarkTitle = Strings.Get("Settings.Benchmark.Never");
            BenchmarkEstimates = string.Empty;
            OnPropertyChanged(nameof(HasBenchmark));
            return;
        }

        foreach (var stage in result.Stages)
        {
            BenchmarkRows.Add(new BenchmarkRow(
                Strings.Get("Settings.Benchmark.Stage." + stage.Key),
                stage.MsPerPhoto is { } ms ? ms.ToString("N0", CultureInfo.CurrentCulture) : Strings.Get("Settings.Benchmark.NoModel"),
                stage.MsPerPhoto is null ? "—" : Formats.Bytes(stage.PeakRamBytes),
                stage.PeakVramBytes is { } vram ? Formats.Bytes(vram) : "—"));
        }

        BenchmarkTitle = Strings.Format("Settings.Benchmark.Title", result.Photos, result.At.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture), result.Backend);
        var archive = 0;
        foreach (var library in await _host.Catalog.Libraries.ListAsync(CancellationToken.None))
        {
            archive += await _host.Catalog.Libraries.CountAssetsAsync(library.Id, CancellationToken.None);
        }

        BenchmarkEstimates = Strings.Format("Settings.Benchmark.Estimates",
            Formats.Duration(result.EstimateImport(1000)),
            archive.ToString("N0", CultureInfo.CurrentCulture),
            Formats.Duration(result.EstimateArchive(archive)),
            result.ParallelMsPerPhoto.ToString("N0", CultureInfo.CurrentCulture),
            result.Workers);
        OnPropertyChanged(nameof(HasBenchmark));
    }

    // Culling (5j)

    public bool PrudentMode => _host.Settings.PrudentMode;

    public bool NormalMode => !_host.Settings.PrudentMode;

    /// <summary>All three metrics of the labelled set are within target (D23).</summary>
    [ObservableProperty]
    private bool _normalModeAllowed;

    public ObservableCollection<string> CullingMetrics { get; } = [];

    [ObservableProperty]
    private string _cullingNote = string.Empty;

    [RelayCommand]
    private async Task SetCullingModeAsync(string? mode)
    {
        var prudent = mode != "Normal";
        if (!prudent && !NormalModeAllowed)
        {
            Message = Strings.Get("Settings.Culling.Locked");
            OnPropertyChanged(nameof(PrudentMode));
            OnPropertyChanged(nameof(NormalMode));
            return;
        }

        await _host.UpdateSettingsAsync(s => s with { PrudentMode = prudent }, CancellationToken.None);
        OnPropertyChanged(nameof(PrudentMode));
        OnPropertyChanged(nameof(NormalMode));
    }

    private async Task RefreshCullingAsync()
    {
        var metrics = await LabelingViewModel.ComputeMetricsAsync(_host.Catalog);
        NormalModeAllowed = metrics.NormalModeAllowed;
        CullingMetrics.Clear();
        foreach (var metric in new[] { metrics.WrongDiscards, metrics.Intercepted, metrics.ToReview })
        {
            var (low, high) = metric.Proportion.Interval;
            CullingMetrics.Add(Strings.Format("Settings.Culling.Metric",
                Strings.Get("Metric." + metric.Key),
                metric.Proportion.Total == 0 ? "—" : $"{low.ToString("P1", CultureInfo.CurrentCulture)} – {high.ToString("P1", CultureInfo.CurrentCulture)}",
                Strings.Format(metric.LowerIsBetter ? "Metric.TargetBelow" : "Metric.TargetAbove", metric.Target.ToString("P0", CultureInfo.CurrentCulture)),
                Strings.Get("Metric.Verdict." + metric.Verdict)));
        }

        CullingNote = Strings.Format(metrics.NormalModeAllowed ? "Settings.Culling.Allowed" : "Settings.Culling.NotYet", metrics.Labeled.ToString("N0", CultureInfo.CurrentCulture));
    }

    // Encryption of the face fingerprints (D12)

    public string EncryptionState => _host.FingerprintKeys.State switch
    {
        FingerprintKeyState.Locked => Strings.Get("Settings.Encryption.Locked"),
        FingerprintKeyState.None => Strings.Get("Settings.Encryption.None"),
        _ => Strings.Get(_host.FingerprintKeys.HasPassword ? "Settings.Encryption.WithPassword" : "Settings.Encryption.AccountOnly"),
    };

    [ObservableProperty]
    private string _encryptionPassword = string.Empty;

    [ObservableProperty]
    private string _encryptionConfirm = string.Empty;

    /// <summary>Adds or replaces the password that opens the key on other accounts or PCs (PBKDF2, 600k iterations).</summary>
    [RelayCommand]
    private async Task SetEncryptionPasswordAsync()
    {
        if (EncryptionPassword.Length < 8)
        {
            Message = Strings.Get("Settings.Encryption.TooShort");
            return;
        }

        if (EncryptionPassword != EncryptionConfirm)
        {
            Message = Strings.Get("Settings.Encryption.Mismatch");
            return;
        }

        var keys = _host.FingerprintKeys;
        if (keys.State == FingerprintKeyState.Locked)
        {
            Message = Strings.Get("Settings.Encryption.LockedHint");
            return;
        }

        var password = EncryptionPassword;
        await Task.Run(() =>
        {
            if (keys.State == FingerprintKeyState.None)
            {
                keys.Create(password);
            }
            else
            {
                keys.SetPassword(password);
            }
        });
        EncryptionPassword = string.Empty;
        EncryptionConfirm = string.Empty;
        Message = Strings.Get("Settings.Encryption.PasswordSet");
        OnPropertyChanged(nameof(EncryptionState));
    }

    public override async Task ActivateAsync()
    {
        _loading = true;
        try
        {
            var detected = _host.Inference.Detected;
            HasGpu = detected.GpuName is not null;
            GpuPackInstalled = _host.GpuRuntime.IsInstalled;
            NvidiaLicensesAccepted = _host.Settings.NvidiaLicensesAccepted;
            BackendText = Strings.Get(detected.AvailableBackends.Contains(ExecutionBackend.Cuda) ? "Settings.Hardware.Cuda"
                : HasGpu ? "Settings.Hardware.CudaMissing" : "Settings.Hardware.CpuOnly");
            GpuText = detected.GpuName is { } gpu
                ? $"{gpu}{(detected.GpuMemoryBytes is { } memory ? " · " + Formats.Bytes(memory) : string.Empty)}"
                : Strings.Get("FirstRun.NoGpu");
            CpuText = Strings.Format("FirstRun.Cpus", detected.LogicalProcessorCount);

            foreach (var row in Models)
            {
                row.IsInstalled = _host.Models.IsInstalled(row.Model.Id);
                row.LicenseAccepted = _host.Settings.AcceptedModelLicenses.Contains(row.Model.Id);
                if (row.Model.ManualConvert)
                {
                    row.SourceFolder = _host.Models.SourceDirectory(row.Model.Id);
                    row.EnvironmentExists = _host.CreateVlmConversion().EnvironmentExists;
                }
            }

            UpdateModelsInUse();

            var installedBytes = Models.Where(m => m.IsInstalled).Sum(m => m.Model.DownloadBytes);
            ModelsFolderText = Strings.Format("Settings.Models.Folder", Formats.Bytes(installedBytes), _host.Paths.ModelsDirectory);

            await RefreshCatalogAsync();
            await RefreshCullingAsync();
            if (_host.PendingCatalogImport is { } package)
            {
                _host.PendingCatalogImport = null;
                SelectedTab = CatalogTab;
                await LoadPackageAsync(package);
            }

            await ShowBenchmarkAsync(_host.Settings.Benchmark);
            if (_host.Updates.AvailableVersion is { } version)
            {
                ShowUpdate(version);
            }
            else if (!_host.Updates.IsInstalled)
            {
                UpdateText = Strings.Get("Settings.Updates.NotInstalled");
            }
            OnPropertyChanged(nameof(EncryptionState));
            await CheckExifToolAsync();
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private void NextTab() => SelectedTab = (SelectedTab + 1) % Tabs.Count;

    [RelayCommand]
    private void PreviousTab() => SelectedTab = (SelectedTab + Tabs.Count - 1) % Tabs.Count;

    // Hardware

    [RelayCommand]
    private async Task InstallGpuPackAsync()
    {
        if (!NvidiaLicensesAccepted)
        {
            Message = Strings.Get("FirstRun.AcceptNvidia");
            return;
        }

        await RunAsync(async progress =>
        {
            await _host.UpdateSettingsAsync(s => s with { NvidiaLicensesAccepted = true }, CancellationToken.None);
            await _host.GpuRuntime.InstallAsync(true, progress, CancellationToken.None);
            Message = Strings.Get("FirstRun.GpuPackRestart");
        });
        GpuPackInstalled = _host.GpuRuntime.IsInstalled;
    }

    // Models

    [RelayCommand]
    private async Task InstallModelAsync(ModelRowViewModel? row)
    {
        if (row is not { CanInstall: true })
        {
            return;
        }

        if (row.RequiresAcceptance && !row.LicenseAccepted)
        {
            Message = Strings.Format("FirstRun.AcceptModelLicense", row.Name);
            return;
        }

        await RunAsync(async progress =>
        {
            if (row.RequiresAcceptance)
            {
                await _host.UpdateSettingsAsync(
                    s => s with { AcceptedModelLicenses = s.AcceptedModelLicenses.Append(row.Model.Id).Distinct().ToList() }, CancellationToken.None);
            }

            Message = Strings.Format("FirstRun.Downloading", row.Name);
            await _host.Models.InstallAsync(row.Model.Id, row.RequiresAcceptance && row.LicenseAccepted, progress, CancellationToken.None);
            Message = Strings.Get("Settings.Models.Restart");
        });
        row.IsInstalled = _host.Models.IsInstalled(row.Model.Id);
    }

    private CancellationTokenSource? _conversion;

    /// <summary>The license is recorded as accepted only when the user ticked it and asked for the model.</summary>
    private Task RecordAcceptanceAsync(ModelRowViewModel row) => row.RequiresAcceptance && row.LicenseAccepted
        ? _host.UpdateSettingsAsync(s => s with { AcceptedModelLicenses = s.AcceptedModelLicenses.Append(row.Model.Id).Distinct().ToList() }, CancellationToken.None)
        : Task.CompletedTask;

    /// <summary>
    /// "Scarica i file originali" (P22): the original files of a model to convert go into its source folder (pinned
    /// addresses, SHA-256 checked, files already there and intact kept), after the license when it needs acceptance.
    /// </summary>
    [RelayCommand]
    private async Task DownloadSourceAsync(ModelRowViewModel? row)
    {
        if (row is not { CanDownloadSource: true })
        {
            return;
        }

        if (row.RequiresAcceptance && !row.LicenseAccepted)
        {
            Message = Strings.Format("FirstRun.AcceptModelLicense", row.Name);
            return;
        }

        await RunAsync(async progress =>
        {
            await RecordAcceptanceAsync(row);
            Message = Strings.Format("FirstRun.Downloading", row.Name);
            await _host.Models.DownloadSourceAsync(row.Model.Id, row.RequiresAcceptance && row.LicenseAccepted, progress, CancellationToken.None);
            Message = Strings.Format("Settings.Models.SourceReady", row.Name);
        });
    }

    /// <summary>Opens the folder where the user puts the original files of a model to convert (made when missing).</summary>
    [RelayCommand]
    private void OpenSourceFolder(ModelRowViewModel? row)
    {
        if (row is not { IsManualConvert: true })
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(row.SourceFolder);
            using var _ = Process.Start(new ProcessStartInfo(row.SourceFolder) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message = Strings.Format("Browse.LaunchFailed", e.Message);
        }
    }

    /// <summary>
    /// Verifies the original files in the source folder and converts them (phase 5b, U2-U3): only with a GPU that can
    /// run the model (P16). The steps are shown on the row; the original files are only read.
    /// </summary>
    [RelayCommand]
    private async Task ConvertModelAsync(ModelRowViewModel? row)
    {
        if (row is not { CanConvert: true })
        {
            return;
        }

        if (row.RequiresAcceptance && !row.LicenseAccepted)
        {
            row.ConversionText = Strings.Format("FirstRun.AcceptModelLicense", row.Name);
            return;
        }

        // The VLM converts and runs on the GPU only (P16); the face recognizer converts on the CPU.
        if (row.Model.Function == ModelFunction.Vlm && !_host.VlmGpuReady)
        {
            row.ConversionText = Strings.Get("Vlm.GpuRequired");
            return;
        }

        await RecordAcceptanceAsync(row);
        _conversion = new CancellationTokenSource();
        row.IsConverting = true;
        IsBusy = true;
        try
        {
            var progress = new Progress<VlmConversionProgress>(p => row.ConversionText = VlmStageText(p));
            var conversion = _host.CreateVlmConversion();
            var token = _conversion.Token;
            await Task.Run(() => conversion.ConvertAsync(row.Model.Id, progress, token), token);
            row.IsInstalled = _host.Models.IsInstalled(row.Model.Id);
            row.ConversionText = Strings.Get(await _host.ReloadModelsAsync(CancellationToken.None)
                ? row.Model.Function == ModelFunction.Vlm ? "Vlm.Ready" : "Settings.Models.Converted"
                : "Settings.Models.Restart");
            UpdateModelsInUse();
        }
        catch (OperationCanceledException)
        {
            row.ConversionText = Strings.Get("Vlm.Cancelled");
        }
        catch (VlmConversionException e)
        {
            row.ConversionText = e.Stage == VlmConversionStage.Python
                ? Strings.Get("Vlm.Error.Python")
                : Strings.Format("Vlm.Error.Stopped", e.Message, e.LogPath ?? "—");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            row.ConversionText = Strings.Format("Vlm.Error.Stopped", e.Message, "—");
        }
        finally
        {
            row.IsConverting = false;
            row.EnvironmentExists = _host.CreateVlmConversion().EnvironmentExists;
            IsBusy = false;
            _conversion.Dispose();
            _conversion = null;
        }
    }

    /// <summary>First step: the row asks to confirm the removal of the environment.</summary>
    [RelayCommand]
    private static void RequestRemoveVlmEnvironment(ModelRowViewModel? row)
    {
        if (row is { CanRemoveEnvironment: true })
        {
            row.ConfirmingRemoval = true;
        }
    }

    [RelayCommand]
    private static void CancelRemoveVlmEnvironment(ModelRowViewModel? row)
    {
        if (row is not null)
        {
            row.ConfirmingRemoval = false;
        }
    }

    /// <summary>Removes the private Python environment of the conversion once confirmed (internal files only, B1).</summary>
    [RelayCommand]
    private async Task RemoveVlmEnvironmentAsync(ModelRowViewModel? row)
    {
        if (row is not { ConfirmingRemoval: true, IsConverting: false })
        {
            return;
        }

        row.ConfirmingRemoval = false;
        IsBusy = true;
        try
        {
            var conversion = _host.CreateVlmConversion();
            await Task.Run(conversion.RemoveEnvironment);
            row.ConversionText = Strings.Get("Vlm.EnvironmentRemoved");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            row.ConversionText = Strings.Format("Vlm.Error.Stopped", e.Message, "—");
        }
        finally
        {
            row.EnvironmentExists = _host.CreateVlmConversion().EnvironmentExists;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelConversion() => _conversion?.Cancel();

    private static string VlmStageText(VlmConversionProgress p) => p.Stage switch
    {
        VlmConversionStage.Verify => Strings.Format("Vlm.Stage.Verify", (int)Math.Round((p.Fraction ?? 0) * 100)),
        VlmConversionStage.Convert => Strings.Format("Vlm.Stage.Convert", (int)Math.Round((p.Fraction ?? 0) * 100), p.Detail ?? string.Empty),
        VlmConversionStage.Done => Strings.Get("Vlm.Stage.Done"),
        _ => Strings.Get("Vlm.Stage." + p.Stage),
    };

    [RelayCommand]
    private async Task VerifyModelAsync(ModelRowViewModel? row)
    {
        if (row is not { IsInstalled: true })
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await Task.Run(() => _host.Models.VerifyAsync(row.Model.Id, CancellationToken.None));
            row.Verification = Strings.Get(ok ? "Models.Status.Verified" : "Models.Status.VerifyFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RequestOfflinePackage() => PickRequested?.Invoke(this, SettingsPick.OfflinePackage);

    /// <summary>
    /// Installs from a folder copied from another PC: models (licenses that need acceptance only if already accepted
    /// here) and the GPU pack archives (only with the NVIDIA licenses accepted). Every file is verified; the folder is
    /// only read.
    /// </summary>
    public async Task ImportOfflinePackageAsync(string folder)
    {
        await RunAsync(async _ =>
        {
            var accepted = Models.Where(m => m.LicenseAccepted).Select(m => m.Model.Id).ToHashSet(StringComparer.Ordinal);
            var (models, gpu) = await _host.ImportOfflinePackageAsync(folder, accepted, NvidiaLicensesAccepted, CancellationToken.None);
            Message = OfflineMessage(models.Count, gpu);
        });
        await ActivateAsync();
    }

    /// <summary>Outcome of an offline import, shared with the first run.</summary>
    public static string OfflineMessage(int models, bool gpu) => models == 0 && !gpu
        ? Strings.Get("Settings.Models.OfflineNothing")
        : Strings.Format("Settings.Models.OfflineDone", models, Strings.Get(gpu ? "Settings.Models.OfflineGpu" : "Settings.Models.OfflineNoGpu"));

    // Catalog

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        IsBusy = true;
        try
        {
            var backup = await _host.Maintenance.BackupNowAsync(_host.Catalog, CancellationToken.None);
            Message = Strings.Format("Settings.Catalog.BackupDone", Path.GetFileName(backup));
            await RefreshCatalogAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = Strings.Format("Settings.Catalog.BackupFailed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RequestRestore()
    {
        if (SelectedBackup is not { } backup)
        {
            return;
        }

        _host.Restore.Request(backup.Value.Path);
        UpdatePendingRestore();
    }

    [RelayCommand]
    private void CancelRestore()
    {
        _host.Restore.Cancel();
        UpdatePendingRestore();
    }

    // General

    [RelayCommand]
    private Task CheckExifToolAsync() => CheckExifToolCoreAsync();

    [RelayCommand]
    private void RequestExifTool() => PickRequested?.Invoke(this, SettingsPick.ExifTool);

    /// <summary>Uses another exiftool after checking that it answers; applied at the next start.</summary>
    public async Task UseExifToolAsync(string path)
    {
        try
        {
            await using var probe = new ExifToolSession(new ExifToolOptions(path) { CommandTimeout = TimeSpan.FromSeconds(15) });
            var version = (await probe.ExecuteAsync(["-ver"], CancellationToken.None)).Output.Trim();
            if (!double.TryParse(version, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                throw new InvalidOperationException(version);
            }

            await _host.UpdateSettingsAsync(s => s with { ExifToolPath = path }, CancellationToken.None);
            Message = Strings.Format("Settings.ExifTool.Chosen", version);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Message = Strings.Format("Settings.ExifTool.Invalid", e.Message);
        }
    }

    // Editor for "Apri in Photoshop" (board 4d, Ctrl E)

    public string EditorText => _host.Settings.PhotoshopPath ?? Strings.Get("Settings.Editor.None");

    [ObservableProperty]
    private string? _xmpSyncText;

    /// <summary>Navigation to the regeneration after a change of model (set by the shell).</summary>
    public IShellNavigator? Navigator { get; set; }

    private void UpdateModelsInUse()
    {
        var setup = _host.DescribeAnalysis();
        var inUse = new HashSet<string?>(StringComparer.Ordinal) { setup.ObjectDetector, setup.FaceDetector, setup.FaceRecognizer, _host.ActiveVlmModelId };
        foreach (var row in Models)
        {
            row.IsInUse = inUse.Contains(row.Model.Id);
            row.HasAlternative = row.Model.Function is ModelFunction.ObjectDetection or ModelFunction.Vlm
                || (row.Model.Function == ModelFunction.FaceRecognition && _host.ModelUsable(row.Model.Id))
                || (row.Model.Function == ModelFunction.Faces && row.Model.Id != PhotoFlowHost.SFaceModelId
                    && (row.Model.Id != "insightface-buffalo-l" || _host.Settings.AcceptedModelLicenses.Contains(row.Model.Id)));
        }

        Recognizers.Clear();
        foreach (var id in PhotoFlowHost.FaceRecognizers.Where(_host.ModelUsable))
        {
            Recognizers.Add(new SettingChoice<string>(id, Strings.Get("Settings.Models.Recognizer." + id)));
        }

        RecognizerInUse = setup.FaceDetector is null ? null : setup.FaceRecognizer;
        SelectedRecognizer = Recognizers.FirstOrDefault(r => r.Value == RecognizerInUse);
        OnPropertyChanged(nameof(HasRecognizerChoice));
    }

    /// <summary>
    /// "Riconoscimento delle persone" (P22): the recognizer chosen apart from the detector; then the regeneration of the
    /// faces on the whole catalog, where the user can also start again from zero with the people.
    /// </summary>
    [RelayCommand]
    private async Task UseRecognizerAsync()
    {
        if (SelectedRecognizer is not { } choice || choice.Value == RecognizerInUse)
        {
            return;
        }

        var loaded = await _host.ChooseModelAsync(ModelFunction.FaceRecognition, choice.Value, CancellationToken.None);
        UpdateModelsInUse();
        await ShowRegenerationAsync(ModelFunction.FaceRecognition, choice.Label, loaded, null);
    }

    /// <summary>
    /// "Usa questo modello" (board 5g): the model replaces the one in use; then the regeneration, with a trial on a
    /// sample and the preview of the differences before anything changes (manual tags and decisions never touched).
    /// </summary>
    [RelayCommand]
    private async Task UseModelAsync(ModelRowViewModel? row)
    {
        if (row is null || !row.CanUse)
        {
            return;
        }

        var loaded = await _host.ChooseModelAsync(row.Model.Function, row.Model.Id, CancellationToken.None);
        UpdateModelsInUse();
        await ShowRegenerationAsync(row.Model.Function, row.Name, loaded, row);
    }

    private async Task ShowRegenerationAsync(ModelFunction function, string name, bool loaded, ModelRowViewModel? row)
    {
        var parts = function switch
        {
            ModelFunction.Vlm => ReanalysisParts.Captions, // the Italian captions of level D, never the user's (I5)
            ModelFunction.FaceRecognition => ReanalysisParts.Faces,
            _ => ReanalysisParts.Culling | ReanalysisParts.Faces,
        };
        var note = Strings.Format(loaded ? "Regeneration.NewModel" : "Regeneration.NewModelAtRestart", name);
        if (Navigator is not null && loaded)
        {
            // A new recognizer makes new fingerprints for every face: the whole catalog (P22).
            Navigator.ShowPage(function == ModelFunction.FaceRecognition
                ? new RegenerationViewModel(_host, Navigator, parts: parts, note: note, allCatalog: true)
                : new RegenerationViewModel(_host, Navigator, libraryId: (await _host.Catalog.Libraries.ListAsync(CancellationToken.None)).FirstOrDefault()?.Id,
                    parts: parts, note: note));
        }
        else if (row is not null)
        {
            row.Verification = note;
        }
        else
        {
            Message = note;
        }
    }

    // --- Export / import of the catalog (spec §9, board 5h) ---

    [ObservableProperty]
    private bool _exportThumbnails = true;

    [ObservableProperty]
    private bool _exportFingerprints;

    [ObservableProperty]
    private string? _exportPassword;

    [ObservableProperty]
    private string? _exportConfirm;

    [ObservableProperty]
    private string? _packageText;

    public ObservableCollection<ImportLibraryRow> ImportLibraries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImporting))]
    private string? _importPath;

    public bool IsImporting => ImportPath is not null;

    [ObservableProperty]
    private bool _importNeedsPassword;

    [ObservableProperty]
    private string? _importPassword;

    private ImportLibraryRow? _rootRow;

    [RelayCommand]
    private void RequestExport()
    {
        if (ExportFingerprints && (string.IsNullOrEmpty(ExportPassword) || ExportPassword != ExportConfirm))
        {
            PackageText = Strings.Get(string.IsNullOrEmpty(ExportPassword) ? "Settings.Catalog.ExportPasswordMissing" : "Settings.Encryption.Mismatch");
            return;
        }

        PickRequested?.Invoke(this, SettingsPick.ExportCatalog);
    }

    /// <summary>The file name proposed for an export.</summary>
    public static string ExportFileName => $"PhotoFlow-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{CatalogPackage.Extension}";

    public async Task ExportCatalogAsync(string path)
    {
        IsBusy = true;
        PackageText = Strings.Get("Settings.Catalog.Exporting");
        try
        {
            var summary = await Task.Run(() => _host.ExportCatalogAsync(path, new CatalogExportOptions(ExportThumbnails, ExportFingerprints ? ExportPassword : null), CancellationToken.None));
            PackageText = Strings.Format("Settings.Catalog.Exported", Path.GetFileName(path), summary.Assets.ToString("N0", CultureInfo.CurrentCulture),
                summary.Fingerprints.ToString("N0", CultureInfo.CurrentCulture), Formats.Bytes(summary.Bytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PackageText = Strings.Format("Settings.Catalog.ExportFailed", e.Message);
        }
        finally
        {
            ExportPassword = ExportConfirm = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RequestImport() => PickRequested?.Invoke(this, SettingsPick.ImportCatalog);

    /// <summary>Reads the description of a .pfcatalog: its libraries to remap, whether it holds fingerprints.</summary>
    public async Task LoadPackageAsync(string path)
    {
        try
        {
            var manifest = await Task.Run(() => CatalogPackage.ReadManifest(path));
            ImportLibraries.Clear();
            foreach (var library in manifest.Libraries)
            {
                ImportLibraries.Add(new ImportLibraryRow(library));
            }

            ImportNeedsPassword = manifest.HasFingerprints;
            ImportPath = path;
            PackageText = Strings.Format("Settings.Catalog.ImportReady", Path.GetFileName(path), manifest.CreatedAt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        }
        catch (CatalogPackageException e)
        {
            ImportPath = null;
            PackageText = e.Message;
        }
    }

    [RelayCommand]
    private void ChooseRoot(ImportLibraryRow? row)
    {
        _rootRow = row;
        if (row is not null)
        {
            PickRequested?.Invoke(this, SettingsPick.ImportRoot);
        }
    }

    public void UseRoot(string folder)
    {
        if (_rootRow is not null)
        {
            _rootRow.NewRoot = folder;
        }
    }

    [RelayCommand]
    private void CancelImport()
    {
        ImportPath = null;
        ImportLibraries.Clear();
        PackageText = null;
    }

    /// <summary>Prepares the import: it replaces the current catalog at the next start (the current one is kept).</summary>
    [RelayCommand]
    private async Task ConfirmImportAsync()
    {
        if (ImportPath is not { } path)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var roots = ImportLibraries.Where(r => r.NewRoot != r.Library.RootPath).ToDictionary(r => r.Library.Id, r => r.NewRoot);
            await Task.Run(() => _host.ImportCatalogAsync(path, roots, ImportNeedsPassword ? ImportPassword : null, CancellationToken.None));
            ImportPath = null;
            ImportLibraries.Clear();
            PackageText = Strings.Get(ImportNeedsPassword && string.IsNullOrEmpty(ImportPassword) ? "Settings.Catalog.ImportedNoFingerprints" : "Settings.Catalog.Imported");
            UpdatePendingRestore();
        }
        catch (Exception e) when (e is CatalogPackageException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PackageText = e.Message;
        }
        finally
        {
            ImportPassword = null;
            IsBusy = false;
        }
    }

    /// <summary>"Sincronizza ora": imports the XMP changes made by other programs (spec §4).</summary>
    [RelayCommand]
    private async Task SyncXmpAsync()
    {
        XmpSyncText = Strings.Get("Settings.Xmp.Syncing");
        var report = await Task.Run(() => _host.SyncExternalXmpAsync(null, CancellationToken.None));
        XmpSyncText = report is null
            ? Strings.Get("Settings.Xmp.SyncFailed")
            : Strings.Format("Settings.Xmp.SyncResult", report.Read.ToString("N0", CultureInfo.CurrentCulture), report.Changed.ToString("N0", CultureInfo.CurrentCulture),
                report.Log.Count(l => l.Winner == Core.Catalog.XmpSyncWinner.Catalog).ToString("N0", CultureInfo.CurrentCulture),
                report.Log.Count(l => l.Field == "region").ToString("N0", CultureInfo.CurrentCulture));
    }

    /// <summary>Face regions named by other programs → people of the gallery (spec §4).</summary>
    [RelayCommand]
    private async Task ImportNamedRegionsAsync()
    {
        var (people, faces) = await Task.Run(() => _host.CreateXmpSync().ImportNamedRegionsAsync(CancellationToken.None));
        XmpSyncText = Strings.Format("Settings.Xmp.PeopleImported", people.ToString(CultureInfo.CurrentCulture), faces.ToString(CultureInfo.CurrentCulture));
    }

    /// <summary>Captions in <c>dc:description</c> (spec §8, off by default); applies to the next XMP writes.</summary>
    public bool WriteCaptions
    {
        get => _host.Settings.Xmp.WriteCaptions;
        set
        {
            if (value != _host.Settings.Xmp.WriteCaptions)
            {
                _ = _host.UpdateSettingsAsync(s => s with { Xmp = s.Xmp with { WriteCaptions = value } }, CancellationToken.None);
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private void RequestEditor() => PickRequested?.Invoke(this, SettingsPick.Editor);

    public async Task UseEditorAsync(string path)
    {
        await _host.UpdateSettingsAsync(s => s with { PhotoshopPath = path }, CancellationToken.None);
        OnPropertyChanged(nameof(EditorText));
    }

    [RelayCommand]
    private async Task ClearEditorAsync()
    {
        await _host.UpdateSettingsAsync(s => s with { PhotoshopPath = null }, CancellationToken.None);
        OnPropertyChanged(nameof(EditorText));
    }

    [RelayCommand]
    private async Task UseBundledExifToolAsync()
    {
        await _host.UpdateSettingsAsync(s => s with { ExifToolPath = null }, CancellationToken.None);
        Message = Strings.Get("Settings.ExifTool.BundledNext");
    }

    partial void OnLanguageChanged(SettingChoice<string?> value)
    {
        if (!_loading && value.Value != _host.Settings.Language)
        {
            _ = SaveAsync(s => s with { Language = value.Value }, Strings.Get("Settings.Language.Restart"));
        }
    }

    partial void OnThemeChanged(SettingChoice<ThemePreference> value)
    {
        ApplyTheme?.Invoke(value.Value);
        if (!_loading && value.Value != _host.Settings.Theme)
        {
            _ = SaveAsync(s => s with { Theme = value.Value }, null);
        }
    }

    partial void OnPathTemplateChanged(string value)
    {
        UpdateTemplateExample();
        if (TemplateError is null && value != _host.Settings.Import.PathTemplate)
        {
            _ = SaveAsync(s => s with { Import = s.Import with { PathTemplate = value } }, null);
        }
    }

    [RelayCommand]
    private void ResetTemplate() => PathTemplate = Sources.Ingest.PathTemplate.Default;

    private void UpdateTemplateExample()
    {
        try
        {
            TemplateExample = Strings.Format("Settings.Template.Example", new Sources.Ingest.PathTemplate(PathTemplate).Render(Example));
            TemplateError = null;
        }
        catch (ArgumentException e)
        {
            TemplateError = Strings.Format("Settings.Template.Invalid", e.Message);
        }
    }

    private async Task SaveAsync(Func<AppSettings, AppSettings> change, string? message)
    {
        await _host.UpdateSettingsAsync(change, CancellationToken.None);
        if (message is not null)
        {
            Message = message;
        }
    }

    private async Task RefreshCatalogAsync()
    {
        var catalog = new FileInfo(_host.Paths.CatalogPath);
        CatalogText = Strings.Format("Settings.Catalog.Location", catalog.FullName, Formats.Bytes(catalog.Exists ? catalog.Length : 0));
        var version = _host.Catalog is Data.SqliteCatalog sqlite ? await sqlite.GetSchemaVersionAsync(CancellationToken.None) : 0;
        SchemaText = Strings.Format("Settings.Catalog.Schema", version);
        Backups.Clear();
        foreach (var backup in _host.Restore.ListBackups())
        {
            Backups.Add(new(backup, $"{backup.CreatedAt.ToString("g", CultureInfo.CurrentCulture)} · {Path.GetFileName(backup.Path)} · {Formats.Bytes(backup.Bytes)}"));
        }

        LastBackupText = Backups.FirstOrDefault() is { } last
            ? Strings.Format("Settings.Catalog.LastBackup", last.Value.CreatedAt.ToString("g", CultureInfo.CurrentCulture))
            : Strings.Get("Settings.Catalog.NoBackup");
        SelectedBackup = Backups.FirstOrDefault();
        UpdatePendingRestore();
        UpdateCatalogChange();
    }

    /// <summary>A move or another catalog waiting for the next start, or the outcome of the last one.</summary>
    [ObservableProperty]
    private string? _catalogChangeText;

    [ObservableProperty]
    private bool _hasPendingCatalogChange;

    public bool CanChangeCatalogLocation => _host.CatalogLocation is not null;

    [RelayCommand]
    private void RequestMoveCatalog() => PickRequested?.Invoke(this, SettingsPick.MoveCatalog);

    [RelayCommand]
    private void RequestOpenCatalog() => PickRequested?.Invoke(this, SettingsPick.OpenCatalog);

    /// <summary>Moves the catalog into <paramref name="folder"/> at the next start; the original stays where it is.</summary>
    public void MoveCatalogTo(string folder)
    {
        try
        {
            _host.CatalogLocation?.RequestMove(folder);
        }
        catch (InvalidOperationException e)
        {
            Message = Strings.Format("Settings.Catalog.ChangeRefused", e.Message);
        }

        UpdateCatalogChange();
    }

    /// <summary>Uses another existing catalog from the next start; the current one stays as it is.</summary>
    public async Task OpenCatalogAsync(string file)
    {
        if (_host.CatalogLocation is not { } location)
        {
            return;
        }

        try
        {
            await location.RequestOpenAsync(file, CancellationToken.None);
        }
        catch (InvalidOperationException e)
        {
            Message = Strings.Format("Settings.Catalog.ChangeRefused", e.Message);
        }

        UpdateCatalogChange();
    }

    [RelayCommand]
    private void CancelCatalogChange()
    {
        _host.CatalogLocation?.CancelPending();
        UpdateCatalogChange();
    }

    private void UpdateCatalogChange()
    {
        var location = _host.CatalogLocation;
        HasPendingCatalogChange = location?.Pending is not null;
        CatalogChangeText = location?.Pending is { } pending
            ? Strings.Format(pending.Move ? "Settings.Catalog.MovePending" : "Settings.Catalog.OpenPending", pending.Target)
            : location?.LastResult?.Split('|') is [var kind, var detail, var before]
                ? Strings.Format("Settings.Catalog.Result." + kind, detail, before)
                : null;
    }

    // Updates (spec §11)

    public string VersionText => Strings.Format("Settings.Updates.Version", AppUpdates.CurrentVersion);

    public bool CheckUpdates
    {
        get => _host.Settings.CheckUpdates;
        set
        {
            if (value != _host.Settings.CheckUpdates)
            {
                _ = SaveAsync(s => s with { CheckUpdates = value }, null);
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    private string? _updateText;

    [ObservableProperty]
    private bool _canInstallUpdate;

    [RelayCommand]
    private async Task CheckUpdatesNowAsync()
    {
        if (!_host.Updates.IsInstalled)
        {
            UpdateText = Strings.Get("Settings.Updates.NotInstalled");
            return;
        }

        IsBusy = true;
        try
        {
            var version = await Task.Run(_host.Updates.CheckAsync);
            ShowUpdate(version);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            UpdateText = Strings.Format("Settings.Updates.Failed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Downloads the update and restarts PhotoFlow on the new version; never while a job runs.</summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_host.Jobs.AnyRunning)
        {
            UpdateText = Strings.Get("Settings.Updates.JobRunning");
            return;
        }

        IsBusy = true;
        try
        {
            await _host.Updates.DownloadAsync(p => Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateText = Strings.Format("Settings.Updates.Downloading", p)), CancellationToken.None);
            _host.Updates.ApplyAndRestart();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            UpdateText = Strings.Format("Settings.Updates.Failed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowUpdate(string? version)
    {
        CanInstallUpdate = version is not null;
        UpdateText = version is null ? Strings.Get("Settings.Updates.UpToDate") : Strings.Format("Settings.Updates.Available", version);
    }

    private void UpdatePendingRestore() =>
        PendingRestoreText = _host.Restore.Pending is { } pending ? Strings.Format("Settings.Catalog.RestorePending", Path.GetFileName(pending)) : null;

    private async Task CheckExifToolCoreAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var version = (await _host.ExifTool.ExecuteAsync(["-ver"], CancellationToken.None)).Output.Trim();
            ExifToolText = Strings.Format(_host.Settings.ExifToolPath is null ? "Settings.ExifTool.Bundled" : "Settings.ExifTool.Custom", version, _host.ExifTool.ExecutablePath);
            ExifToolHealth = Strings.Format("Settings.ExifTool.Healthy", watch.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            ExifToolText = _host.ExifTool.ExecutablePath;
            ExifToolHealth = Strings.Format("Settings.ExifTool.Unhealthy", e.Message);
        }
    }

    private async Task RunAsync(Func<IProgress<DownloadProgress>, Task> action)
    {
        IsBusy = true;
        Progress = 0;
        try
        {
            await action(new Progress<DownloadProgress>(p => Progress = p.TotalBytes > 0 ? (double)p.DownloadedBytes / p.TotalBytes : 0));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
        {
            Message = Strings.Format("FirstRun.DownloadFailed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A row of the benchmark table (board 5f).</summary>
public sealed record BenchmarkRow(string Stage, string Time, string Ram, string Vram);
