// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Inference.Gpu;
using PhotoFlow.Inference.Models;

namespace PhotoFlow.App.ViewModels;

/// <summary>A model offered in the first run, with its license (P11).</summary>
public sealed partial class ModelChoiceViewModel(ModelInfo model, bool installed) : ObservableObject
{
    public ModelInfo Model { get; } = model;

    public string Name => Model.Name;

    public string LicenseText => Model.License.CommercialUse
        ? Strings.Format("Models.License.Commercial", Model.License.Spdx)
        : Strings.Get("Models.License.NonCommercialOnly");

    public string SizeText => Formats.Bytes(Model.DownloadBytes);

    public string LicenseUrl => Model.License.Source;

    public bool RequiresAcceptance => Model.License.RequiresAcceptance;

    [ObservableProperty]
    private bool _isInstalled = installed;

    [ObservableProperty]
    private bool _licenseAccepted;
}

/// <summary>
/// First run (board 3b): hardware and GPU pack, models with their licenses, first library. Nothing is downloaded
/// without the explicit acceptance of the licenses that require it (buffalo_l, NVIDIA).
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly PhotoFlowHost _host;
    private readonly Action _completed;

    public FirstRunViewModel(PhotoFlowHost host, Action completed)
    {
        _host = host;
        _completed = completed;
        var registry = host.Models;
        var gpu = host.Inference.Detected.GpuName is not null;
        Detector = new ModelChoiceViewModel(registry.Get(gpu ? "dfine-m-obj2coco" : "dfine-s-obj2coco"), false);
        Buffalo = new ModelChoiceViewModel(registry.Get("insightface-buffalo-l"), false);
        YuNet = new ModelChoiceViewModel(registry.Get("opencv-yunet-2023mar"), false);
        _libraryRoot = host.Settings.Import.PrimaryRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PhotoFlow");
    }

    public ModelChoiceViewModel Detector { get; }

    public ModelChoiceViewModel Buffalo { get; }

    public ModelChoiceViewModel YuNet { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseYuNet))]
    private bool _useBuffalo = true;

    public bool UseYuNet
    {
        get => !UseBuffalo;
        set => UseBuffalo = !value;
    }

    [ObservableProperty]
    private string _hardwareText = string.Empty;

    [ObservableProperty]
    private bool _gpuPackNeeded;

    [ObservableProperty]
    private bool _gpuPackInstalled;

    [ObservableProperty]
    private bool _nvidiaLicensesAccepted;

    [ObservableProperty]
    private string _libraryRoot;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _status = string.Empty;

    public string GpuPackSize => Formats.Bytes(GpuRuntimeManifest.Embedded().TotalBytes);

    public string CatalogLocation => Strings.Format("FirstRun.CatalogLocation", _host.Paths.CatalogPath);

    public bool ModelsReady => Detector.IsInstalled && (UseBuffalo ? Buffalo.IsInstalled : YuNet.IsInstalled);

    public Task RefreshAsync()
    {
        var detected = _host.Inference.Detected;
        var backend = detected.AvailableBackends.Contains(ExecutionBackend.Cuda) ? "CUDA" : "CPU";
        var gpu = detected.GpuName is { } name
            ? $"{name}{(detected.GpuMemoryBytes is { } memory ? " " + Formats.Bytes(memory) : string.Empty)}"
            : Strings.Get("FirstRun.NoGpu");
        HardwareText = $"{backend} · {gpu} · {Strings.Format("FirstRun.Cpus", detected.LogicalProcessorCount)}";
        GpuPackInstalled = _host.GpuRuntime.IsInstalled;
        GpuPackNeeded = detected.GpuName is not null && !GpuPackInstalled;
        NvidiaLicensesAccepted = _host.Settings.NvidiaLicensesAccepted;
        Detector.IsInstalled = _host.Models.IsInstalled(Detector.Model.Id);
        Buffalo.IsInstalled = _host.Models.IsInstalled(Buffalo.Model.Id);
        Buffalo.LicenseAccepted = _host.Settings.AcceptedModelLicenses.Contains(Buffalo.Model.Id);
        YuNet.IsInstalled = _host.Models.IsInstalled(YuNet.Model.Id);
        OnPropertyChanged(nameof(ModelsReady));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task InstallGpuPackAsync()
    {
        if (!NvidiaLicensesAccepted)
        {
            Status = Strings.Get("FirstRun.AcceptNvidia");
            return;
        }

        await RunAsync(async progress =>
        {
            await _host.UpdateSettingsAsync(s => s with { NvidiaLicensesAccepted = true }, CancellationToken.None);
            await _host.GpuRuntime.InstallAsync(true, progress, CancellationToken.None);
            Status = Strings.Get("FirstRun.GpuPackRestart");
        });
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task InstallModelsAsync()
    {
        var faces = UseBuffalo ? Buffalo : YuNet;
        if (faces.RequiresAcceptance && !faces.LicenseAccepted)
        {
            Status = Strings.Format("FirstRun.AcceptModelLicense", faces.Name);
            return;
        }

        await RunAsync(async progress =>
        {
            if (faces.RequiresAcceptance)
            {
                await _host.UpdateSettingsAsync(
                    s => s with { AcceptedModelLicenses = s.AcceptedModelLicenses.Append(faces.Model.Id).Distinct().ToList() }, CancellationToken.None);
            }

            foreach (var model in new[] { Detector, faces }.Where(m => !m.IsInstalled))
            {
                Status = Strings.Format("FirstRun.Downloading", model.Name);
                await _host.Models.InstallAsync(model.Model.Id, model.RequiresAcceptance && model.LicenseAccepted, progress, CancellationToken.None);
            }

            Status = Strings.Get("FirstRun.ModelsReady");
        });
        await RefreshAsync();
    }

    /// <summary>Offline package copied from another PC: only the licenses ticked here count as accepted.</summary>
    public async Task ImportOfflinePackageAsync(string folder)
    {
        var accepted = new[] { Buffalo, YuNet, Detector }.Where(m => !m.RequiresAcceptance || m.LicenseAccepted).Select(m => m.Model.Id).ToHashSet(StringComparer.Ordinal);
        await RunAsync(async _ =>
        {
            var (models, gpu) = await _host.ImportOfflinePackageAsync(folder, accepted, NvidiaLicensesAccepted, CancellationToken.None);
            Status = SettingsViewModel.OfflineMessage(models.Count, gpu);
        });
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        if (string.IsNullOrWhiteSpace(LibraryRoot) || !Path.IsPathFullyQualified(LibraryRoot))
        {
            Status = Strings.Get("FirstRun.LibraryInvalid");
            return;
        }

        var root = Path.TrimEndingDirectorySeparator(LibraryRoot.Trim());
        Directory.CreateDirectory(root);
        if (await _host.Catalog.Libraries.FindByRootAsync(root, CancellationToken.None) is null)
        {
            var name = Path.GetFileName(root) is { Length: > 0 } leaf ? leaf : root;
            await _host.Catalog.Libraries.AddAsync(new Library(0, name, root, WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.Import, null), CancellationToken.None);
        }

        await _host.UpdateSettingsAsync(s => s with { SetupCompleted = true, Import = s.Import with { PrimaryRoot = root } }, CancellationToken.None);
        _completed();
    }

    private async Task RunAsync(Func<IProgress<DownloadProgress>, Task> action)
    {
        IsBusy = true;
        DownloadProgress = 0;
        try
        {
            await action(new Progress<DownloadProgress>(p => DownloadProgress = p.TotalBytes > 0 ? (double)p.DownloadedBytes / p.TotalBytes : 0));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or TaskCanceledException)
        {
            Status = Strings.Format("FirstRun.DownloadFailed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
