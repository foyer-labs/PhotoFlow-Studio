// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.App.Services.Develop;
using PhotoFlow.App.Views.Develop;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Develop;
using PhotoFlow.Core.Paths;

namespace PhotoFlow.App.ViewModels;

/// <summary>A photo of the filmstrip of Develop.</summary>
public sealed partial class DevelopPhotoItem(long assetId, string name, string? thumbnailPath) : ObservableObject
{
    public long AssetId { get; } = assetId;

    public string Name { get; } = name;

    public string? ThumbnailPath { get; } = thumbnailPath;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDeveloped;

    [ObservableProperty]
    private bool _isExported;

    /// <summary>Preview with the noise reduction already computed in the background (M8).</summary>
    [ObservableProperty]
    private bool _isPrepared;

    /// <summary>Group of photos in the same light (M7), and whether this photo starts it.</summary>
    [ObservableProperty]
    private int _groupIndex;

    [ObservableProperty]
    private bool _isGroupStart;

    [ObservableProperty]
    private string _groupText = string.Empty;

    public void LoadThumbnail()
    {
        if (Thumbnail is null && ThumbnailPath is { } path && File.Exists(path))
        {
            Thumbnail = UiBitmaps.FromFile(path, 160);
        }
    }
}

/// <summary>A choice of a combo box (key and label).</summary>
public sealed record DevelopOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Develop (decisions S6–S10): the photos of the working selection in the filmstrip, the photo with before/after,
/// the panel with histogram and curve, light, colour, tones, noise. Every change is saved in the catalog and the
/// preview follows: first the development, then (if the noise reduction is on) the whole photo cleaned.
/// </summary>
public sealed partial class DevelopViewModel : PageViewModel, IPhotoSource, IDisposable
{
    private const int PreviewSide = 2560;

    private static readonly CommandTable Table = new(
    [
        new("Develop.Photo", new KeyGesture(Key.Left), "Develop.Command.Photo", StatusGroupKey: "Develop.Command.Photo", Parameter: "-1"),
        new("Develop.Photo", new KeyGesture(Key.Right), "Develop.Command.Photo", Parameter: "1"),
        new("Develop.Photo", new KeyGesture(Key.PageUp), "Develop.Command.Photo", Parameter: "-1"),
        new("Develop.Photo", new KeyGesture(Key.PageDown), "Develop.Command.Photo", Parameter: "1"),
        new("Develop.Zoom", new KeyGesture(Key.Space), "Develop.Command.Zoom", StatusGroupKey: "Develop.Command.Zoom"),
        new("Develop.Compare", new KeyGesture(Key.Y), "Develop.Command.Compare", StatusGroupKey: "Develop.Command.Compare"),
        new("Develop.Noise", new KeyGesture(Key.R), "Develop.Command.Noise", StatusGroupKey: "Develop.Command.Noise"),
        new("Develop.Pick", new KeyGesture(Key.W), "Develop.Command.Pick", StatusGroupKey: "Develop.Command.Pick"),
        new("Develop.Copy", new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift), "Develop.Command.Copy", StatusGroupKey: "Develop.Command.Copy"),
        new("Develop.Paste", new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift), "Develop.Command.Paste"),
        new("Develop.ApplySelected", new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift), "Develop.Command.ApplySelected",
            StatusGroupKey: "Develop.Command.ApplySelected"),
        new("Develop.Undo", new KeyGesture(Key.Z, KeyModifiers.Control), "Develop.Command.Undo", StatusGroupKey: "Develop.Command.Undo"),
        new("Develop.Redo", new KeyGesture(Key.Y, KeyModifiers.Control), "Develop.Command.Redo"),
        new("Develop.Editor", new KeyGesture(Key.E, KeyModifiers.Control), "Develop.Command.Editor", StatusGroupKey: "Develop.Command.Editor"),
        new("Develop.Escape", new KeyGesture(Key.Escape), "Develop.Command.Escape"),
        new("Develop.Crop", new KeyGesture(Key.C), "Develop.Command.Crop", StatusGroupKey: "Develop.Command.Crop"),
        new("Develop.SelectAll", new KeyGesture(Key.A, KeyModifiers.Control), "Review.Command.SelectAll", StatusGroupKey: "Review.Command.SelectAll"),
        new("Develop.SelectGroup", new KeyGesture(Key.G), "Develop.Command.SelectGroup", StatusGroupKey: "Develop.Command.SelectGroup"),
        new("Develop.SyncGroup", new KeyGesture(Key.G, KeyModifiers.Control | KeyModifiers.Shift), "Develop.Command.SyncGroup", StatusGroupKey: "Develop.Command.SyncGroup"),
        new("Develop.CropDone", new KeyGesture(Key.Enter), "Develop.Command.CropDone"),
    ]);

    private readonly PhotoFlowHost _host;
    private readonly IShellNavigator _navigator;
    private readonly InternalFileCleaner _cleaner;
    private readonly Dictionary<long, (Stack<string> Undo, Stack<string> Redo)> _history = [];
    private DevelopEngineInfo? _engineInfo;
    private JsonObject _settings = [];
    private JsonObject _opened = [];
    private JsonObject? _clipboard;
    private string? _path;
    private DevelopPhoto? _photo;
    private bool _loadingPanel;
    private int _workingSetVersion = -1;
    private int _workingSetSeen;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _fullCts;
    private CancellationTokenSource? _histogramCts;
    private CancellationTokenSource? _saveCts;
    private Point _centre;

    /// <summary>Size of the output (after rotation, straightening and crop) at full resolution, as the engine reports it.</summary>
    private (int Width, int Height) _frame;

    public DevelopViewModel(PhotoFlowHost host, IShellNavigator navigator)
    {
        _host = host;
        _navigator = navigator;
        _cleaner = new InternalFileCleaner(host.Paths);
        _cache = new PreviewCache(host.Paths);
        host.WorkingSet.Changed += OnWorkingSetChanged;
    }

    private readonly PreviewCache _cache;

    public override string Title => Strings.Get("Nav.Develop");

    public override CommandTable? Commands => Table;

    public ObservableCollection<DevelopPhotoItem> Photos { get; } = [];

    public ObservableCollection<DevelopOption> Methods { get; } = [];

    public ObservableCollection<DevelopOption> Presets { get; } = [];

    public IReadOnlyList<DevelopOption> WhiteBalanceModes { get; } =
    [
        new("camera", Strings.Get("Develop.Wb.Camera")),
        new("auto", Strings.Get("Develop.Wb.Auto")),
        new("daylight", Strings.Get("Develop.Wb.Daylight")),
        new("temperature", Strings.Get("Develop.Wb.Custom")),
    ];

    public IReadOnlyList<DevelopOption> DemosaicOptions { get; } =
    [
        new("AHD", "AHD"), new("AAHD", "AAHD"), new("DCB", "DCB"), new("DHT", "DHT"), new("PPG", "PPG"), new("VNG", "VNG"),
    ];

    // ------------------------------------------------------------------ state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto), nameof(IsEmpty))]
    private DevelopPhotoItem? _active;

    [ObservableProperty]
    private string _origin = string.Empty;

    [ObservableProperty]
    private string _counters = string.Empty;

    [ObservableProperty]
    private string? _engineProblem;

    [ObservableProperty]
    private string _photoTitle = string.Empty;

    [ObservableProperty]
    private string _photoFacts = string.Empty;

    [ObservableProperty]
    private string _noiseFacts = string.Empty;

    [ObservableProperty]
    private string _autoText = string.Empty;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasPhoto => Active is not null && _photo is not null;

    public bool IsEmpty => Photos.Count == 0;

    // Viewer
    [ObservableProperty]
    private Bitmap? _before;

    [ObservableProperty]
    private Bitmap? _after;

    [ObservableProperty]
    private Bitmap? _difference;

    [ObservableProperty]
    private Rect _region;

    [ObservableProperty]
    private bool _actualSize;

    [ObservableProperty]
    private CompareMode _compareMode = CompareMode.Split;

    [ObservableProperty]
    private bool _showOriginal;

    [ObservableProperty]
    private bool _isPicking;

    [ObservableProperty]
    private string _afterLabel = string.Empty;

    [ObservableProperty]
    private double _noiseProgress;

    [ObservableProperty]
    private bool _isComputing;

    [ObservableProperty]
    private int[][]? _histogram;

    /// <summary>Viewport of the viewer in physical pixels (set by the view).</summary>
    public PixelSize Viewport { get; set; } = new(1600, 1000);

    // Panel: light
    [ObservableProperty] private double _exposure;
    [ObservableProperty] private double _highlights;
    [ObservableProperty] private double _shadows;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private bool _autoBrightness = true;

    // Panel: colour
    [ObservableProperty] private DevelopOption? _whiteBalance;
    [ObservableProperty] private double _temperature = 5500;
    [ObservableProperty] private double _tint;
    [ObservableProperty] private double _saturation;

    // Panel: tones
    [ObservableProperty] private double _contrast;

    /// <summary>Points of the tone curve (the view keeps the editor in sync).</summary>
    [ObservableProperty]
    private IReadOnlyList<Point> _curve = [new(0, 0), new(1, 1)];

    // Panel: noise
    [ObservableProperty] private bool _noiseEnabled = true;
    [ObservableProperty] private double _grain = 1;
    [ObservableProperty] private double _colorNoise = 1;
    [ObservableProperty] private double _detail;
    [ObservableProperty] private DevelopOption? _method;
    [ObservableProperty] private bool _sensorStage = true;
    [ObservableProperty] private bool _hotPixels;
    [ObservableProperty] private DevelopOption? _demosaic;
    [ObservableProperty] private DevelopOption? _preset;
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private string? _downloadText;

    // Panel: geometry (M1). In crop mode the preview shows the whole straightened frame and the viewer draws the crop.
    [ObservableProperty] private bool _isCropping;
    [ObservableProperty] private Rect _cropRect = new(0, 0, 1, 1);
    [ObservableProperty] private double _angle;
    [ObservableProperty] private DevelopOption? _aspect;

    public IReadOnlyList<DevelopOption> AspectOptions { get; } =
    [
        new("original", Strings.Get("Develop.Aspect.Original")), new("free", Strings.Get("Develop.Aspect.Free")),
        new("3:2", "3:2"), new("4:3", "4:3"), new("4:5", "4:5"), new("1:1", "1:1"), new("16:9", "16:9"),
    ];

    /// <summary>Width / height in pixels the crop keeps (0 = free), for the viewer.</summary>
    public double CropAspectRatio => AspectRatio(Aspect?.Key);

    public bool IsRaw => _photo?.IsRaw == true;

    public string NoiseBadge => !NoiseEnabled ? Strings.Get("Develop.Noise.Off")
        : IsComputing ? Strings.Format("Develop.Noise.Computing", (NoiseProgress * 100).ToString("0", CultureInfo.CurrentCulture))
        : Strings.Format("Develop.Noise.On", Method?.Label ?? string.Empty);

    public int SelectedCount => Photos.Count(p => p.IsSelected);

    public string ApplySelectedText => Strings.Format("Develop.ApplySelected", Math.Max(0, SelectedCount - (Active?.IsSelected == true ? 1 : 0)));

    // ------------------------------------------------------------------ activation and the working selection

    public override async Task ActivateAsync()
    {
        try
        {
            _engineInfo ??= await _host.DevelopEngine.HelloAsync(CancellationToken.None);
            EngineProblem = null;
            if (Methods.Count == 0)
            {
                foreach (var a in _engineInfo.Algorithms)
                {
                    Methods.Add(new(a.Key, a.Available || !a.Neural ? a.Label : Strings.Format("Develop.Method.ToDownload", a.Label, a.SizeMb)));
                }

                foreach (var (name, _) in _engineInfo.Presets)
                {
                    Presets.Add(new(name, name));
                }
            }
        }
        catch (DevelopEngineException e)
        {
            EngineProblem = e.Message;
            return;
        }

        if (_workingSetVersion != _workingSetSeen || Photos.Count == 0 && !_host.WorkingSet.IsEmpty)
        {
            _workingSetSeen = _workingSetVersion;
            await LoadWorkingSetAsync();
        }
    }

    private void OnWorkingSetChanged(object? sender, EventArgs e) => _workingSetVersion++;

    private async Task LoadWorkingSetAsync()
    {
        var set = _host.WorkingSet;
        Origin = set.Origin;
        Photos.Clear();
        var assets = await _host.Catalog.Assets.GetManyAsync(set.AssetIds.ToList(), CancellationToken.None);
        var developed = await _host.Catalog.Develop.ListDevelopedAsync(set.AssetIds.ToList(), CancellationToken.None);
        var exported = await _host.Catalog.Develop.LastExportsAsync(set.AssetIds.ToList(), CancellationToken.None);
        foreach (var id in set.AssetIds)
        {
            if (!assets.TryGetValue(id, out var asset))
            {
                continue;
            }

            var thumbnail = new[] { asset.FullHash, asset.QuickHash }.OfType<string>().Select(_host.Thumbnails.PathFor).FirstOrDefault(File.Exists);
            var locations = await _host.Catalog.Assets.GetLocationsAsync(id, CancellationToken.None);
            var name = locations.Count > 0 ? Path.GetFileName(locations[0].RelativePath) : id.ToString(CultureInfo.InvariantCulture);
            var item = new DevelopPhotoItem(id, name, thumbnail)
            {
                IsDeveloped = developed.Contains(id),
                IsExported = exported.ContainsKey(id),
            };
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DevelopPhotoItem.IsSelected))
                {
                    OnPropertyChanged(nameof(SelectedCount));
                    OnPropertyChanged(nameof(ApplySelectedText));
                }
            };
            item.LoadThumbnail();
            Photos.Add(item);
        }

        // Groups in the same light (M7), in shooting order.
        var shots = Photos.Select(p => assets.TryGetValue(p.AssetId, out var a) ? (a.CapturedAt, DevelopGroups.Ev100(a.Exif)) : ((DateTimeOffset?)null, (double?)null)).ToList();
        var groups = DevelopGroups.Assign(shots);
        for (var i = 0; i < Photos.Count; i++)
        {
            Photos[i].GroupIndex = groups[i];
            Photos[i].IsGroupStart = i == 0 || groups[i] != groups[i - 1];
        }

        foreach (var group in Photos.GroupBy(p => p.GroupIndex))
        {
            var text = Strings.Format("Develop.Group", (group.Key + 1).ToString(CultureInfo.CurrentCulture), group.Count().ToString(CultureInfo.CurrentCulture));
            foreach (var photo in group)
            {
                photo.GroupText = text;
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(GroupCountText));
        UpdateCounters();
        var active = Photos.FirstOrDefault(p => p.AssetId == set.ActiveId) ?? Photos.FirstOrDefault();
        Active = null;
        Active = active;
    }

    private void UpdateCounters() => Counters = Strings.Format("Develop.Counters", Photos.Count, Photos.Count(p => p.IsDeveloped), Photos.Count(p => p.IsExported));

    /// <summary>Uses the best and kept photos of the most recent job (empty page).</summary>
    [RelayCommand]
    private async Task UseLatestBestAsync()
    {
        var jobs = await _host.Catalog.Jobs.ListRecentAsync(5, CancellationToken.None);
        foreach (var job in jobs)
        {
            var ids = await DevelopSources.BestOfJobAsync(_host.Catalog, job.Id, CancellationToken.None);
            if (ids.Count > 0)
            {
                _host.WorkingSet.Set(Strings.Format("Develop.Origin.Review", job.EventName ?? Strings.Get("Job.Unnamed"), ids.Count), ids, null, job.EventName);
                await ActivateAsync();
                return;
            }
        }

        Message = Strings.Get("Develop.Empty.NoBest");
    }

    // ------------------------------------------------------------------ the open photo

    partial void OnActiveChanged(DevelopPhotoItem? oldValue, DevelopPhotoItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
            _host.WorkingSet.SetActive(newValue.AssetId);
            _ = OpenAsync(newValue);
        }
    }

    private async Task OpenAsync(DevelopPhotoItem item)
    {
        CancelRenders();
        await FlushSaveAsync();
        _photo = null;
        OnPropertyChanged(nameof(HasPhoto));
        Before = After = Difference = null;
        Histogram = null;
        IsBusy = true;
        Message = null;
        try
        {
            var path = await AssetFiles.LocateAsync(_host.Catalog, _host.FileSystem, item.AssetId, CancellationToken.None);
            if (Active != item)
            {
                return;
            }

            if (path is null)
            {
                Message = Strings.Get("Develop.Missing");
                return;
            }

            if (_path is { } previous && previous != path)
            {
                _ = _host.DevelopEngine.CloseAsync(previous, CancellationToken.None);
            }

            var stored = await _host.Catalog.Develop.GetSettingsAsync(item.AssetId, CancellationToken.None) is { } json
                ? JsonNode.Parse(json) as JsonObject
                : null;
            var photo = await _host.DevelopEngine.OpenAsync(path, stored ?? BaseSettings(), CancellationToken.None);
            if (Active != item)
            {
                return;
            }

            _path = path;
            _photo = photo;
            var settings = stored ?? photo.Settings;
            AutoText = string.Empty;
            if (stored is null && !photo.HasSidecarSettings)
            {
                // Never developed: the preset suggested by the measured noise (decision S8, «Automatico»).
                settings = await _host.DevelopEngine.ApplyPresetAsync(photo.SuggestedPreset, settings, CancellationToken.None);
                AutoText = Strings.Format("Develop.Auto", photo.SuggestedPreset, photo.NoiseLevel, photo.Iso is { } iso ? iso.ToString("0", CultureInfo.CurrentCulture) : "—");
            }

            _settings = settings;
            Json.Section(_settings, "studio")["noise_enabled"] ??= true;
            _opened = (JsonObject)_settings.DeepClone();
            if (!_history.ContainsKey(item.AssetId))
            {
                _history[item.AssetId] = (new Stack<string>(), new Stack<string>());
            }

            PhotoTitle = item.Name;
            PhotoFacts = string.Join(" · ", new[]
            {
                $"{photo.Width} × {photo.Height}", photo.Camera, photo.Iso is { } i ? "ISO " + i.ToString("0", CultureInfo.CurrentCulture) : null,
            }.Where(s => !string.IsNullOrEmpty(s)));
            NoiseFacts = Strings.Format("Develop.NoiseMeasured", photo.NoiseLevel, photo.NoiseDetail);
            LoadPanel();
            OnPropertyChanged(nameof(HasPhoto));
            OnPropertyChanged(nameof(IsRaw));
            ActualSize = false;
            _frame = default;
            IsCropping = false;
            _centre = new Point(photo.Width / 2.0, photo.Height / 2.0);
            OnPropertyChanged(nameof(SyncGroupText));
            OnPropertyChanged(nameof(ApplyUndevelopedText));
            ScheduleHistogram();
            await RefreshAsync(full: true);
            item.IsPrepared = true;
            PrefetchAround(item);
        }
        catch (DevelopEngineException e)
        {
            Message = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Settings for a photo never developed: engine defaults, with the noise method used last.</summary>
    private JsonObject BaseSettings()
    {
        var settings = (JsonObject?)_engineInfo?.Defaults.DeepClone() ?? [];
        if (_settings["denoise"] is JsonObject denoise && denoise["algorithm"] is { } algorithm)
        {
            Json.Section(settings, "denoise")["algorithm"] = algorithm.DeepClone();
        }

        return settings;
    }

    private void LoadPanel()
    {
        _loadingPanel = true;
        try
        {
            var tone = Json.Section(_settings, "tone");
            var develop = Json.Section(_settings, "develop");
            var denoise = Json.Section(_settings, "denoise");
            Exposure = Json.Double(tone["exposure_ev"]);
            Highlights = Json.Double(tone["highlights"]);
            Shadows = Json.Double(tone["shadows"]);
            Brightness = Json.Double(tone["brightness"]);
            Contrast = Json.Double(tone["contrast"]);
            Saturation = Json.Double(tone["saturation"]);
            AutoBrightness = Json.Bool(tone["auto_brightness"], true);
            Curve = (tone["curve"] as JsonArray ?? []).OfType<JsonArray>().Select(p => new Point(Json.Double(p[0]), Json.Double(p[1]))).ToList() is { Count: >= 2 } c
                ? c
                : [new(0, 0), new(1, 1)];
            var mode = Json.String(develop["wb_mode"], "camera");
            WhiteBalance = WhiteBalanceModes.FirstOrDefault(m => m.Key == mode) ?? WhiteBalanceModes[0];
            ShowWhiteBalanceEquivalent(mode, Json.Double(develop["temperature"], 5500), Json.Double(develop["tint"]));
            Demosaic = DemosaicOptions.FirstOrDefault(o => o.Key == Json.String(develop["demosaic"], "AHD")) ?? DemosaicOptions[0];
            NoiseEnabled = Json.Bool(Json.Section(_settings, "studio")["noise_enabled"], true);
            Grain = Json.Double(denoise["luma_strength"], 1);
            ColorNoise = Json.Double(denoise["chroma_strength"], 1);
            Detail = Json.Double(denoise["detail"]);
            Method = Methods.FirstOrDefault(m => m.Key == Json.String(denoise["algorithm"], "wavelet")) ?? Methods.FirstOrDefault();
            SensorStage = Json.String(denoise["raw_stage"], "sensor") == "sensor";
            HotPixels = Json.Double(denoise["hot_pixel_threshold"]) > 0;
            var geometry = Json.Section(_settings, "geometry");
            Angle = Json.Double(geometry["angle"]);
            CropRect = CropOf(geometry);
            Aspect = AspectOptions.FirstOrDefault(o => o.Key == Json.String(geometry["aspect"], "original")) ?? AspectOptions[0];
            Preset = null;
            UpdateDownload();
        }
        finally
        {
            _loadingPanel = false;
        }

        OnPropertyChanged(nameof(NoiseBadge));
    }

    private void ShowWhiteBalanceEquivalent(string mode, double temperature, double tint)
    {
        if (mode != "temperature" && _photo?.WhiteBalanceEquivalents.TryGetValue(mode, out var equivalent) == true)
        {
            (temperature, tint) = equivalent;
        }

        Temperature = Math.Round(temperature);
        Tint = Math.Round(tint);
    }

    // ------------------------------------------------------------------ edits

    partial void OnExposureChanged(double value) => Edit("tone", "exposure_ev", Math.Round(value, 2));

    partial void OnHighlightsChanged(double value) => Edit("tone", "highlights", Math.Round(value));

    partial void OnShadowsChanged(double value) => Edit("tone", "shadows", Math.Round(value));

    partial void OnBrightnessChanged(double value) => Edit("tone", "brightness", Math.Round(value));

    partial void OnContrastChanged(double value) => Edit("tone", "contrast", Math.Round(value));

    partial void OnSaturationChanged(double value) => Edit("tone", "saturation", Math.Round(value));

    partial void OnAutoBrightnessChanged(bool value) => Edit("tone", "auto_brightness", value);

    partial void OnCurveChanged(IReadOnlyList<Point> value) =>
        Edit("tone", "curve", new JsonArray([.. value.Select(p => (JsonNode)new JsonArray(Math.Round(p.X, 4), Math.Round(p.Y, 4)))]));

    partial void OnWhiteBalanceChanged(DevelopOption? value)
    {
        if (value is null || _loadingPanel)
        {
            return;
        }

        Edit("develop", "wb_mode", value.Key);
        _loadingPanel = true;
        ShowWhiteBalanceEquivalent(value.Key, Temperature, Tint);
        _loadingPanel = false;
    }

    partial void OnTemperatureChanged(double value) => EditWhiteBalance("temperature", Math.Round(value));

    partial void OnTintChanged(double value) => EditWhiteBalance("tint", Math.Round(value));

    /// <summary>Moving temperature or tint switches to «Personalizzato» (as in Denoiser).</summary>
    private void EditWhiteBalance(string key, double value)
    {
        if (_loadingPanel)
        {
            return;
        }

        var develop = Json.Section(_settings, "develop");
        develop["temperature"] = Math.Round(Temperature);
        develop["tint"] = Math.Round(Tint);
        if (WhiteBalance?.Key != "temperature")
        {
            _loadingPanel = true;
            WhiteBalance = WhiteBalanceModes.First(m => m.Key == "temperature");
            _loadingPanel = false;
        }

        Edit("develop", "wb_mode", "temperature");
        _ = key;
        _ = value;
    }

    partial void OnNoiseEnabledChanged(bool value)
    {
        Edit("studio", "noise_enabled", value);
        OnPropertyChanged(nameof(NoiseBadge));
    }

    partial void OnGrainChanged(double value) => Edit("denoise", "luma_strength", Math.Round(value, 2), full: true);

    partial void OnColorNoiseChanged(double value) => Edit("denoise", "chroma_strength", Math.Round(value, 2), full: true);

    partial void OnDetailChanged(double value) => Edit("denoise", "detail", Math.Round(value, 2), full: true);

    partial void OnSensorStageChanged(bool value) => Edit("denoise", "raw_stage", value ? "sensor" : "rgb", full: true);

    partial void OnHotPixelsChanged(bool value) => Edit("denoise", "hot_pixel_threshold", value ? 8.0 : 0.0, full: true);

    partial void OnMethodChanged(DevelopOption? value)
    {
        if (value is null || _loadingPanel)
        {
            return;
        }

        Json.Section(_settings, "denoise")["params"] = new JsonObject(); // the parameters belong to the method
        Edit("denoise", "algorithm", value.Key, full: true);
        UpdateDownload();
        OnPropertyChanged(nameof(NoiseBadge));
    }

    partial void OnDemosaicChanged(DevelopOption? value)
    {
        if (value is null || _loadingPanel || Active is not { } item)
        {
            return;
        }

        Edit("develop", "demosaic", value.Key);
        _ = ReopenAsync(item); // the colours are rebuilt by LibRaw: the photo is developed again
    }

    partial void OnPresetChanged(DevelopOption? value)
    {
        if (value is null || _loadingPanel || _path is null)
        {
            return;
        }

        _ = ApplyPresetAsync(value.Key);
    }

    private async Task ApplyPresetAsync(string name)
    {
        try
        {
            PushUndo();
            var studio = _settings["studio"]?.DeepClone();
            _settings = await _host.DevelopEngine.ApplyPresetAsync(name, _settings, CancellationToken.None);
            _settings["studio"] = studio ?? new JsonObject();
            LoadPanel();
            Changed(full: true);
        }
        catch (DevelopEngineException e)
        {
            Message = e.Message;
        }
    }

    private async Task ReopenAsync(DevelopPhotoItem item)
    {
        await SaveNowAsync();
        if (_path is { } path)
        {
            await _host.DevelopEngine.CloseAsync(path, CancellationToken.None);
        }

        await OpenAsync(item);
    }

    private void Edit(string section, string key, JsonNode? value, bool full = false)
    {
        if (_loadingPanel || _photo is null)
        {
            return;
        }

        PushUndo();
        Json.Section(_settings, section)[key] = value;
        Changed(full || section == "studio");
    }

    // ------------------------------------------------------------------ geometry (M1)

    private static Rect CropOf(JsonObject geometry) =>
        geometry["crop"] is JsonArray { Count: 4 } c && Json.Double(c[2]) > 0 && Json.Double(c[3]) > 0
            ? new Rect(Json.Double(c[0]), Json.Double(c[1]), Json.Double(c[2]), Json.Double(c[3]))
            : new Rect(0, 0, 1, 1);

    /// <summary>The straightened frame before the crop, in pixels.</summary>
    private (double Width, double Height) FullFrame()
    {
        if (_photo is not { } photo)
        {
            return (1, 1);
        }

        var turned = Json.Int(Json.Section(_settings, "geometry")["rotation"]) % 180 == 90;
        return turned ? (photo.Height, photo.Width) : (photo.Width, photo.Height);
    }

    private double AspectRatio(string? key)
    {
        var (fw, fh) = FullFrame();
        double ratio;
        switch (key)
        {
            case null or "free":
                return 0;
            case "original":
                ratio = fw / fh;
                break;
            default:
                var parts = key.Split(':');
                ratio = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
                break;
        }

        // The ratio follows the orientation of the current crop (a 3:2 on a vertical crop is 2:3).
        var portrait = CropRect.Width * fw < CropRect.Height * fh;
        return portrait == ratio > 1 ? 1 / ratio : ratio;
    }

    /// <summary>The largest crop with <paramref name="pixelRatio"/> (width / height in pixels) centred where <paramref name="current"/> is.</summary>
    private Rect FitAspect(Rect current, double pixelRatio)
    {
        var (fw, fh) = FullFrame();
        var q = pixelRatio * fh / fw; // the same ratio in fractions of the frame
        var w = Math.Min(1, q);
        var h = w / q;
        if (h > 1)
        {
            h = 1;
            w = q;
        }

        var cx = current.X + current.Width / 2;
        var cy = current.Y + current.Height / 2;
        return new Rect(Math.Clamp(cx - w / 2, 0, 1 - w), Math.Clamp(cy - h / 2, 0, 1 - h), w, h);
    }

    partial void OnCropRectChanged(Rect value) => OnPropertyChanged(nameof(CropAspectRatio));

    partial void OnAngleChanged(double value) => Edit("geometry", "angle", Math.Round(value, 2));

    partial void OnAspectChanged(DevelopOption? value)
    {
        OnPropertyChanged(nameof(CropAspectRatio));
        if (value is null || _loadingPanel || _photo is null)
        {
            return;
        }

        PushUndo();
        Json.Section(_settings, "geometry")["aspect"] = value.Key;
        if (AspectRatio(value.Key) is > 0 and var ratio)
        {
            SetCropValue(FitAspect(CropRect, ratio));
        }

        Changed(full: false, render: !IsCropping);
    }

    private void SetCropValue(Rect r)
    {
        var w = Math.Clamp(r.Width, 0.01, 1);
        var h = Math.Clamp(r.Height, 0.01, 1);
        var rect = new Rect(Math.Clamp(r.X, 0, 1 - w), Math.Clamp(r.Y, 0, 1 - h), w, h);
        CropRect = rect;
        Json.Section(_settings, "geometry")["crop"] = new JsonArray(Math.Round(rect.X, 5), Math.Round(rect.Y, 5), Math.Round(rect.Width, 5), Math.Round(rect.Height, 5));
    }

    /// <summary>The crop drawn in the viewer (fractions of the straightened frame).</summary>
    public void SetCrop(Rect rect)
    {
        if (_photo is null)
        {
            return;
        }

        PushUndo();
        SetCropValue(rect);
        Changed(full: false, render: !IsCropping);
    }

    [RelayCommand]
    private void ToggleCrop()
    {
        if (!HasPhoto)
        {
            return;
        }

        IsCropping = !IsCropping;
        if (IsCropping)
        {
            IsPicking = false;
            ActualSize = false;
        }

        ScheduleHistogram();
        _ = RefreshAsync(full: NoiseEnabled);
    }

    [RelayCommand]
    private void CropDone()
    {
        if (IsCropping)
        {
            ToggleCrop();
        }
    }

    [RelayCommand]
    private void RotateRight() => Rotate(90);

    [RelayCommand]
    private void RotateLeft() => Rotate(-90);

    /// <summary>Turns what is shown by <paramref name="degrees"/> (clockwise); the crop turns with it.</summary>
    private void Rotate(int degrees)
    {
        if (_photo is null)
        {
            return;
        }

        PushUndo();
        var geometry = Json.Section(_settings, "geometry");
        // The rotation comes before the mirror: with the mirror on, turning what is shown clockwise is turning the photo the other way.
        var step = Json.Bool(geometry["flip"]) ? -degrees : degrees;
        geometry["rotation"] = (((Json.Int(geometry["rotation"]) + step) % 360) + 360) % 360;
        var r = CropRect;
        SetCropValue(degrees > 0 ? new Rect(1 - r.Y - r.Height, r.X, r.Height, r.Width) : new Rect(r.Y, 1 - r.X - r.Width, r.Height, r.Width));
        Changed(full: false);
    }

    [RelayCommand]
    private void Flip()
    {
        if (_photo is null)
        {
            return;
        }

        PushUndo();
        var geometry = Json.Section(_settings, "geometry");
        geometry["flip"] = !Json.Bool(geometry["flip"]);
        geometry["angle"] = -Json.Double(geometry["angle"]); // the mirrored scene keeps its straightening
        var r = CropRect;
        SetCropValue(new Rect(1 - r.X - r.Width, r.Y, r.Width, r.Height));
        _loadingPanel = true;
        Angle = Json.Double(geometry["angle"]);
        _loadingPanel = false;
        Changed(full: false);
    }

    [RelayCommand]
    private async Task AutoLevelAsync()
    {
        if (_path is not { } path)
        {
            return;
        }

        try
        {
            if (await _host.DevelopEngine.AutoLevelAsync(path, EngineSettings(), CancellationToken.None) is { } angle)
            {
                Angle = Math.Clamp(angle, -45, 45);
                Message = Strings.Format("Develop.Level.Done", Angle.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture));
            }
            else
            {
                Message = Strings.Get("Develop.Level.None");
            }
        }
        catch (DevelopEngineException e)
        {
            Message = e.Message;
        }
    }

    [RelayCommand]
    private void ResetGeometry()
    {
        if (_photo is null || _engineInfo is null)
        {
            return;
        }

        PushUndo();
        _settings["geometry"] = _engineInfo.Defaults["geometry"]?.DeepClone() ?? new JsonObject();
        LoadPanel();
        Changed(full: false);
    }

    private void Changed(bool full, bool render)
    {
        if (render)
        {
            Changed(full);
            return;
        }

        AutoText = string.Empty;
        if (Active is { } item)
        {
            item.IsDeveloped = true;
            UpdateCounters();
        }

        ScheduleSave();
    }

    private void Changed(bool full)
    {
        AutoText = string.Empty;
        if (Active is { } item)
        {
            item.IsDeveloped = true;
            UpdateCounters();
        }

        ScheduleSave();
        ScheduleHistogram();
        _ = RefreshAsync(full: full || NoiseEnabled);
    }

    /// <summary>While the curve is dragged: live preview, no undo step (the step is the whole drag).</summary>
    public void PreviewCurve(IReadOnlyList<Point> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (_photo is null)
        {
            return;
        }

        _dragSnapshot ??= _settings.ToJsonString();
        Json.Section(_settings, "tone")["curve"] = new JsonArray([.. points.Select(p => (JsonNode)new JsonArray(Math.Round(p.X, 4), Math.Round(p.Y, 4)))]);
        ScheduleHistogram();
        _ = RefreshAsync(full: false);
    }

    private string? _dragSnapshot;

    private void PushUndo()
    {
        if (Active is not { } item || !_history.TryGetValue(item.AssetId, out var stacks))
        {
            _dragSnapshot = null;
            return;
        }

        var snapshot = _dragSnapshot ?? _settings.ToJsonString();
        _dragSnapshot = null;
        if (stacks.Undo.Count == 0 || stacks.Undo.Peek() != snapshot)
        {
            stacks.Undo.Push(snapshot);
        }

        stacks.Redo.Clear();
    }

    private void Restore(string snapshot)
    {
        _settings = JsonNode.Parse(snapshot) as JsonObject ?? [];
        LoadPanel();
        if (Active is { } item)
        {
            item.IsDeveloped = true;
        }

        ScheduleSave();
        ScheduleHistogram();
        _ = RefreshAsync(full: true);
    }

    // ------------------------------------------------------------------ preview

    /// <summary>Settings sent to the engine: with the noise reduction off its strengths are zero.</summary>
    private JsonObject EngineSettings()
    {
        var settings = DevelopJson.ForEngine(_settings);
        if (IsCropping)
        {
            Json.Section(settings, "geometry")["crop"] = new JsonArray(0.0, 0.0, 1.0, 1.0); // the whole frame, the viewer draws the crop
        }

        return settings;
    }

    /// <summary>The output frame (full resolution): the engine's once a render arrived, the photo's before.</summary>
    private (int Width, int Height) Frame => _frame.Width > 0 ? _frame : _photo is { } p ? (p.Width, p.Height) : (1, 1);

    private void CancelRenders()
    {
        _renderCts?.Cancel();
        _fullCts?.Cancel();
        _histogramCts?.Cancel();
        IsComputing = false;
    }

    private async Task RefreshAsync(bool full)
    {
        if (_path is not { } path || _photo is not { } photo)
        {
            return;
        }

        _renderCts?.Cancel();
        var cts = _renderCts = new CancellationTokenSource();
        var settings = EngineSettings();
        try
        {
            if (ActualSize)
            {
                // 100%: the visible region, with the noise reduction (fast on a region).
                var (fw, fh) = Frame;
                var w = Math.Min(Viewport.Width, fw);
                var h = Math.Min(Viewport.Height, fh);
                var x = (int)Math.Clamp(_centre.X - w / 2.0, 0, fw - w);
                var y = (int)Math.Clamp(_centre.Y - h / 2.0, 0, fh - h);
                IsComputing = NoiseEnabled;
                OnPropertyChanged(nameof(NoiseBadge));
                var progress = new Progress<DevelopProgress>(p => { NoiseProgress = p.Fraction; OnPropertyChanged(nameof(NoiseBadge)); });
                var render = await _host.DevelopEngine.RenderAsync(path, settings, (x, y, w, h), 0, NoiseEnabled, progress, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    Show(render, render.AfterFile is not null);
                }

                return;
            }

            // Prepared in the background (M8): the cleaned photo at once.
            var key = Active is { } active ? PreviewCache.Key(active.AssetId, settings, PreviewSide) : null;
            if (full && NoiseEnabled && key is not null && _cache.TryGet(key) is { } prepared)
            {
                Show(prepared, true, cached: true);
                return;
            }

            // Fitted: the development at once, then the whole photo cleaned (decision S8).
            _host.HoldAnalysisForPreview();
            var quick = await _host.DevelopEngine.RenderAsync(path, settings, null, PreviewSide, false, null, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Show(quick, false);
            if (NoiseEnabled && full)
            {
                await RenderFullAsync(path, settings, key);
            }
            else if (NoiseEnabled && After is null)
            {
                AfterLabel = Strings.Get("Develop.Label.NotComputed");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (DevelopEngineException e)
        {
            Message = e.Message;
        }
        finally
        {
            if (cts == _renderCts)
            {
                IsComputing = _fullCts is not null;
            }
        }
    }

    private async Task RenderFullAsync(string path, JsonObject settings, string? key)
    {
        _fullCts?.Cancel();
        var cts = _fullCts = new CancellationTokenSource();
        IsComputing = true;
        NoiseProgress = 0;
        OnPropertyChanged(nameof(NoiseBadge));
        try
        {
            var progress = new Progress<DevelopProgress>(p => { NoiseProgress = p.Fraction; OnPropertyChanged(nameof(NoiseBadge)); });
            var render = await _host.DevelopEngine.RenderFullAsync(path, settings, PreviewSide, progress, cts.Token);
            // Kept: going back to this photo (or exporting it soon after) does not compute it again (M8).
            var stored = key is null ? null : _cache.Store(key, render);
            if (!cts.IsCancellationRequested && !ActualSize)
            {
                Show(stored ?? render, true, cached: stored is not null);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (DevelopEngineException e)
        {
            Message = e.Message;
        }
        finally
        {
            if (cts == _fullCts)
            {
                _fullCts = null;
                IsComputing = false;
                OnPropertyChanged(nameof(NoiseBadge));
            }
        }
    }

    private void Show(DevelopRender render, bool withAfter, bool cached = false)
    {
        var before = Load(render.BeforeFile, !cached);
        var after = withAfter && render.AfterFile is { } a ? Load(a, !cached) : null;
        var difference = withAfter && render.DifferenceFile is { } d ? Load(d, !cached) : null;
        if (render.Frame.Width > 0 && render.Frame != _frame)
        {
            _frame = render.Frame;
            if (!ActualSize)
            {
                _centre = new Point(_frame.Width / 2.0, _frame.Height / 2.0);
            }
        }

        Region = new Rect(render.Region.X, render.Region.Y, render.Region.Width, render.Region.Height);
        Before = before;
        After = withAfter ? after : (NoiseEnabled ? null : before);
        Difference = difference;
        AfterLabel = !NoiseEnabled ? Strings.Get("Develop.Label.Developed")
            : withAfter ? Strings.Format("Develop.Label.After", render.Reduction is { } r ? "−" + r.ToString(CultureInfo.CurrentCulture) + "%" : string.Empty)
            : Strings.Get("Develop.Label.Computing");
    }

    private Bitmap? Load(string file, bool delete = true)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return new Bitmap(stream);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (delete && _cleaner.IsInternal(file))
            {
                _cleaner.DeleteInternalFile(file);
            }
        }
    }

    private void ScheduleHistogram()
    {
        _histogramCts?.Cancel();
        var cts = _histogramCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(60, cts.Token);
                if (_path is { } path)
                {
                    var h = await _host.DevelopEngine.HistogramAsync(path, EngineSettings(), cts.Token);
                    Ui.Post(() => { if (!cts.IsCancellationRequested) { Histogram = h; } });
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (DevelopEngineException)
            {
            }
        });
    }

    // ------------------------------------------------------------------ saving

    private void ScheduleSave()
    {
        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        var assetId = Active?.AssetId;
        var json = _settings.ToJsonString();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                if (assetId is { } id)
                {
                    await _host.Catalog.Develop.SetSettingsAsync(id, json, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        _pendingSave = assetId is { } pending ? (pending, json) : null;
    }

    private (long AssetId, string Json)? _pendingSave;

    private async Task FlushSaveAsync()
    {
        _saveCts?.Cancel();
        if (_pendingSave is { } save)
        {
            _pendingSave = null;
            await _host.Catalog.Develop.SetSettingsAsync(save.AssetId, save.Json, CancellationToken.None);
        }
    }

    private Task SaveNowAsync() => FlushSaveAsync();

    // ------------------------------------------------------------------ commands

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Develop.Photo" => StepCommand,
        "Develop.Zoom" => ToggleZoomCommand,
        "Develop.Compare" => CycleCompareCommand,
        "Develop.Noise" => ToggleNoiseCommand,
        "Develop.Pick" => PickCommand,
        "Develop.Copy" => CopyCommand,
        "Develop.Paste" => PasteCommand,
        "Develop.ApplySelected" => ApplySelectedCommand,
        "Develop.Undo" => UndoCommand,
        "Develop.Redo" => RedoCommand,
        "Develop.Editor" => OpenInEditorCommand,
        "Develop.Escape" => EscapeCommand,
        "Develop.Crop" => ToggleCropCommand,
        "Develop.SelectAll" => SelectAllCommand,
        "Develop.SelectGroup" => SelectGroupCommand,
        "Develop.SyncGroup" => SyncGroupCommand,
        "Develop.CropDone" => CropDoneCommand,
        _ => base.ResolveCommand(id),
    };

    [RelayCommand]
    private void Step(string? direction)
    {
        if (Photos.Count == 0)
        {
            return;
        }

        var index = Active is null ? -1 : Photos.IndexOf(Active);
        var step = int.TryParse(direction, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 1;
        Active = Photos[Math.Clamp(index + step, 0, Photos.Count - 1)];
    }

    public void Click(DevelopPhotoItem item, bool add)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (add)
        {
            item.IsSelected = !item.IsSelected;
            return;
        }

        foreach (var p in Photos)
        {
            p.IsSelected = false;
        }

        item.IsSelected = true;
        Active = item;
    }

    // ------------------------------------------------------------------ groups in the same light (M7)

    public string GroupCountText => Strings.Format("Develop.Groups", (Photos.Count == 0 ? 0 : Photos.Max(p => p.GroupIndex) + 1).ToString(CultureInfo.CurrentCulture));

    public string SyncGroupText => Active is { } a ? Strings.Format("Develop.SyncGroup", (Photos.Count(p => p.GroupIndex == a.GroupIndex) - 1).ToString(CultureInfo.CurrentCulture)) : string.Empty;

    public int UndevelopedCount => Photos.Count(p => !p.IsDeveloped && p != Active);

    public string ApplyUndevelopedText => Strings.Format("Develop.ApplyUndeveloped", UndevelopedCount.ToString(CultureInfo.CurrentCulture));

    /// <summary>G (M7): the photos of the group of the open photo become the selection.</summary>
    [RelayCommand]
    private void SelectGroup()
    {
        if (Active is not { } active)
        {
            return;
        }

        foreach (var photo in Photos)
        {
            photo.IsSelected = photo.GroupIndex == active.GroupIndex;
        }
    }

    /// <summary>Ctrl+Shift+G (M7): what changed on the open photo goes to the rest of its group (as «apply to the selected»).</summary>
    [RelayCommand]
    private async Task SyncGroupAsync()
    {
        SelectGroup();
        await ApplySelectedAsync();
    }

    /// <summary>
    /// The look of the open photo as the starting point of the photos never developed (M7): light, colour, tones and noise,
    /// not the framing. One undo for all.
    /// </summary>
    [RelayCommand]
    private async Task ApplyToUndevelopedAsync()
    {
        var targets = Photos.Where(p => !p.IsDeveloped && p != Active).ToList();
        if (targets.Count == 0 || _photo is null)
        {
            Message = Strings.Get("Develop.ApplyUndeveloped.None");
            return;
        }

        await FlushSaveAsync();
        var previous = new Dictionary<long, string?>();
        foreach (var target in targets)
        {
            previous[target.AssetId] = await _host.Catalog.Develop.GetSettingsAsync(target.AssetId, CancellationToken.None);
            var settings = BaseSettings();
            foreach (var section in new[] { "tone", "denoise", "studio" })
            {
                settings[section] = _settings[section]?.DeepClone();
            }

            foreach (var key in new[] { "wb_mode", "temperature", "tint" })
            {
                Json.Section(settings, "develop")[key] = _settings["develop"]?[key]?.DeepClone();
            }

            await _host.Catalog.Develop.SetSettingsAsync(target.AssetId, settings.ToJsonString(), CancellationToken.None);
            target.IsDeveloped = true;
            target.IsPrepared = false; // its prepared preview no longer matches
        }

        _lastBulk = previous;
        UndoBulkVisible = true;
        UpdateCounters();
        Message = Strings.Format("Develop.ApplyUndeveloped.Done", targets.Count);
    }

    // ------------------------------------------------------------------ prepared in the background (M8)

    private readonly List<DevelopPhotoItem> _prepareQueue = [];
    private CancellationTokenSource? _prepareCts;
    private bool _prepareAll;
    private int _prepareDone;
    private int _prepareTotal;
    private double _secondsPerPhoto;

    [ObservableProperty]
    private string? _prepareText;

    [ObservableProperty]
    private bool _isPreparingAll;

    /// <summary>The photos around the open one get ready while the user looks at it.</summary>
    private void PrefetchAround(DevelopPhotoItem item)
    {
        var index = Photos.IndexOf(item);
        foreach (var i in new[] { index + 1, index + 2, index - 1 })
        {
            if (i >= 0 && i < Photos.Count)
            {
                Enqueue(Photos[i]);
            }
        }
    }

    /// <summary>«Prepara tutte»: every photo of the strip with the noise reduction computed, with the time left.</summary>
    [RelayCommand]
    private void PrepareAll()
    {
        _prepareAll = true;
        _prepareDone = 0;
        foreach (var photo in Photos.Where(p => !p.IsPrepared))
        {
            Enqueue(photo);
        }

        lock (_prepareQueue)
        {
            _prepareTotal = _prepareQueue.Count;
        }

        IsPreparingAll = _prepareTotal > 0;
        UpdatePrepareText();
    }

    [RelayCommand]
    private void StopPrepare()
    {
        lock (_prepareQueue)
        {
            _prepareQueue.Clear();
        }

        _prepareCts?.Cancel();
        _prepareAll = false;
        IsPreparingAll = false;
        PrepareText = null;
    }

    private void Enqueue(DevelopPhotoItem item)
    {
        if (item.IsPrepared)
        {
            return;
        }

        lock (_prepareQueue)
        {
            if (!_prepareQueue.Contains(item))
            {
                _prepareQueue.Add(item);
            }
        }

        if (_prepareCts is null)
        {
            var cts = _prepareCts = new CancellationTokenSource();
            _ = PrepareLoopAsync(cts);
        }
    }

    private async Task PrepareLoopAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                DevelopPhotoItem? next;
                lock (_prepareQueue)
                {
                    if (_prepareQueue.Count == 0)
                    {
                        break;
                    }

                    next = _prepareQueue[0];
                    _prepareQueue.RemoveAt(0);
                }

                var started = Stopwatch.GetTimestamp();
                await PrepareOneAsync(next, cts.Token);
                var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                _secondsPerPhoto = _secondsPerPhoto == 0 ? seconds : (_secondsPerPhoto * 0.7) + (seconds * 0.3);
                if (_prepareAll)
                {
                    _prepareDone++;
                    UpdatePrepareText();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (DevelopEngineException)
        {
            // the photo is computed when it is opened
        }
        finally
        {
            if (_prepareCts == cts)
            {
                _prepareCts = null;
            }

            if (_prepareAll && _prepareQueue.Count == 0)
            {
                _prepareAll = false;
                IsPreparingAll = false;
                PrepareText = Strings.Format("Develop.Prepare.Done", _prepareDone.ToString(CultureInfo.CurrentCulture));
            }
        }
    }

    private void UpdatePrepareText()
    {
        int left;
        lock (_prepareQueue)
        {
            left = _prepareQueue.Count;
        }

        PrepareText = Strings.Format("Develop.Prepare.Progress", _prepareDone.ToString(CultureInfo.CurrentCulture), _prepareTotal.ToString(CultureInfo.CurrentCulture),
            _secondsPerPhoto > 0 ? Formats.Duration(TimeSpan.FromSeconds(left * _secondsPerPhoto)) : "…");
    }

    /// <summary>
    /// The cleaned preview of a photo that is not open, with the settings it would open with: its own, or (never developed)
    /// the base ones with the preset suggested by the noise, as <see cref="OpenAsync"/> does.
    /// </summary>
    private async Task PrepareOneAsync(DevelopPhotoItem item, CancellationToken ct)
    {
        if (item.IsPrepared || await AssetFiles.LocateAsync(_host.Catalog, _host.FileSystem, item.AssetId, ct) is not { } path)
        {
            return;
        }

        var stored = await _host.Catalog.Develop.GetSettingsAsync(item.AssetId, ct) is { } json ? JsonNode.Parse(json) as JsonObject : null;
        var settings = stored ?? BaseSettings();
        if (!Json.Bool(settings["studio"]?["noise_enabled"], true))
        {
            return; // no noise reduction: the preview is quick anyway
        }

        var engineSettings = DevelopJson.ForEngine(settings);
        if (stored is not null && _cache.Contains(PreviewCache.Key(item.AssetId, engineSettings, PreviewSide)))
        {
            item.IsPrepared = true;
            return;
        }

        var (render, used) = await _host.DevelopEngine.PrepareAsync(path, engineSettings, PreviewSide, auto: stored is null, null, ct);
        _cache.Store(PreviewCache.Key(item.AssetId, stored is null ? used : engineSettings, PreviewSide), render);
        item.IsPrepared = true;
    }

    /// <summary>Ctrl+A (M3): every photo of the strip selected, for «apply to the selected» and the export.</summary>
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var photo in Photos)
        {
            photo.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ToggleZoom() => ZoomAt(null);

    public void ZoomAt(Point? point)
    {
        if (_photo is null)
        {
            return;
        }

        if (point is { } p && !ActualSize)
        {
            _centre = p;
        }

        ActualSize = !ActualSize;
        _ = RefreshAsync(full: !ActualSize);
    }

    public void PanTo(Point centre)
    {
        _centre = centre;
        _ = RefreshAsync(full: false);
    }

    [RelayCommand]
    private void CycleCompare() => CompareMode = CompareMode switch
    {
        CompareMode.Split => CompareMode.After,
        CompareMode.After => CompareMode.Before,
        CompareMode.Before => CompareMode.Difference,
        _ => CompareMode.Split,
    };

    [RelayCommand]
    private void ToggleNoise() => NoiseEnabled = !NoiseEnabled;

    [RelayCommand]
    private void Pick() => IsPicking = !IsPicking && HasPhoto;

    public async Task PickAtAsync(Point point)
    {
        IsPicking = false;
        if (_path is not { } path)
        {
            return;
        }

        try
        {
            var (temperature, tint) = await _host.DevelopEngine.PickWhiteBalanceAsync(path, point.X, point.Y, EngineSettings(), CancellationToken.None);
            PushUndo();
            _loadingPanel = true;
            WhiteBalance = WhiteBalanceModes.First(m => m.Key == "temperature");
            Temperature = temperature;
            Tint = tint;
            _loadingPanel = false;
            var develop = Json.Section(_settings, "develop");
            develop["wb_mode"] = "temperature";
            develop["temperature"] = temperature;
            develop["tint"] = tint;
            Changed(full: false);
        }
        catch (DevelopEngineException e)
        {
            Message = e.Message;
        }
    }

    [RelayCommand]
    private void Copy()
    {
        _clipboard = (JsonObject)_settings.DeepClone();
        Message = Strings.Get("Develop.Copied");
    }

    [RelayCommand]
    private void Paste()
    {
        if (_clipboard is null || _photo is null)
        {
            return;
        }

        PushUndo();
        foreach (var section in new[] { "tone", "denoise", "studio" })
        {
            _settings[section] = _clipboard[section]?.DeepClone();
        }

        var develop = Json.Section(_settings, "develop");
        foreach (var key in new[] { "wb_mode", "temperature", "tint" })
        {
            develop[key] = _clipboard["develop"]?[key]?.DeepClone();
        }

        LoadPanel();
        Changed(full: true);
    }

    /// <summary>Copies to the other selected photos only the fields changed on this one (decision S10).</summary>
    [RelayCommand]
    private async Task ApplySelectedAsync()
    {
        var targets = Photos.Where(p => p.IsSelected && p != Active).ToList();
        if (targets.Count == 0 || _photo is null)
        {
            Message = Strings.Get("Develop.ApplySelected.None");
            return;
        }

        var fields = ChangedFields(_opened, _settings);
        if (fields.Count == 0)
        {
            Message = Strings.Get("Develop.ApplySelected.NothingChanged");
            return;
        }

        var previous = new Dictionary<long, string?>();
        foreach (var target in targets)
        {
            var stored = await _host.Catalog.Develop.GetSettingsAsync(target.AssetId, CancellationToken.None);
            previous[target.AssetId] = stored;
            var settings = stored is not null ? JsonNode.Parse(stored) as JsonObject ?? BaseSettings() : BaseSettings();
            foreach (var (section, key) in fields)
            {
                Json.Section(settings, section)[key] = _settings[section]?[key]?.DeepClone();
            }

            await _host.Catalog.Develop.SetSettingsAsync(target.AssetId, settings.ToJsonString(), CancellationToken.None);
            target.IsDeveloped = true;
            target.IsPrepared = false; // its prepared preview no longer matches
        }

        _lastBulk = previous;
        UpdateCounters();
        Message = Strings.Format("Develop.ApplySelected.Done", targets.Count, string.Join(", ", fields.Select(f => Strings.Get("Develop.Field." + f.Key))));
        UndoBulkVisible = true;
    }

    private Dictionary<long, string?>? _lastBulk;

    [ObservableProperty]
    private bool _undoBulkVisible;

    [RelayCommand]
    private async Task UndoBulkAsync()
    {
        if (_lastBulk is null)
        {
            return;
        }

        foreach (var (assetId, json) in _lastBulk)
        {
            if (json is not null)
            {
                await _host.Catalog.Develop.SetSettingsAsync(assetId, json, CancellationToken.None);
            }
        }

        _lastBulk = null;
        UndoBulkVisible = false;
        Message = Strings.Get("Develop.ApplySelected.Undone");
    }

    /// <summary>Fields (section, key) of the photo changed since it was opened.</summary>
    public static List<(string Section, string Key)> ChangedFields(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var result = new List<(string, string)>();
        foreach (var section in new[] { "develop", "tone", "denoise", "studio", "geometry" })
        {
            var a = after[section] as JsonObject ?? [];
            var b = before[section] as JsonObject ?? [];
            foreach (var (key, value) in a)
            {
                if (!JsonNode.DeepEquals(value, b[key]))
                {
                    result.Add((section, key));
                }
            }
        }

        return result;
    }

    [RelayCommand]
    private void Undo()
    {
        if (Active is not { } item || !_history.TryGetValue(item.AssetId, out var stacks) || stacks.Undo.Count == 0)
        {
            return;
        }

        stacks.Redo.Push(_settings.ToJsonString());
        Restore(stacks.Undo.Pop());
    }

    [RelayCommand]
    private void Redo()
    {
        if (Active is not { } item || !_history.TryGetValue(item.AssetId, out var stacks) || stacks.Redo.Count == 0)
        {
            return;
        }

        stacks.Undo.Push(_settings.ToJsonString());
        Restore(stacks.Redo.Pop());
    }

    [RelayCommand]
    private void ResetDevelopment()
    {
        if (_engineInfo is null || _photo is null)
        {
            return;
        }

        PushUndo();
        var defaults = _engineInfo.Defaults;
        _settings["tone"] = defaults["tone"]?.DeepClone();
        var develop = Json.Section(_settings, "develop");
        foreach (var key in new[] { "wb_mode", "temperature", "tint" })
        {
            develop[key] = defaults["develop"]?[key]?.DeepClone();
        }

        LoadPanel();
        Changed(full: false);
    }

    [RelayCommand]
    private void ResetCurve() => Curve = [new(0, 0), new(1, 1)];

    [RelayCommand]
    private void OpenInEditor()
    {
        if (_path is null)
        {
            return;
        }

        if (_host.Settings.PhotoshopPath is { } editor && File.Exists(editor))
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(editor) { ArgumentList = { _path }, UseShellExecute = false });
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                Message = e.Message;
            }
        }
        else
        {
            Message = Strings.Get("Browse.NoPhotoshop");
        }
    }

    [RelayCommand]
    private void Escape()
    {
        if (IsCropping)
        {
            ToggleCrop();
        }
        else if (IsPicking)
        {
            IsPicking = false;
        }
        else if (ActualSize)
        {
            ToggleZoom();
        }
    }

    /// <summary>Export: the selected photos of the filmstrip, otherwise the whole working selection (decision S11).</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        await SendToWorkingSetAsync(forExport: true);
        _navigator.ShowSection(SectionCatalog.Export);
    }

    public async Task<bool> SendToWorkingSetAsync(bool forExport)
    {
        await FlushSaveAsync();
        var selected = Photos.Where(p => p.IsSelected).Select(p => p.AssetId).ToList();
        if (selected.Count > 1)
        {
            _host.WorkingSet.Set(Strings.Format("Develop.Origin.Selected", selected.Count), selected, Active?.AssetId, _host.WorkingSet.EventName);
        }

        return Photos.Count > 0;
    }

    [RelayCommand]
    private async Task DownloadMethodAsync()
    {
        if (Method is not { } method || _engineInfo?.Algorithms.FirstOrDefault(a => a.Key == method.Key) is not { Neural: true, Available: false } algorithm)
        {
            return;
        }

        try
        {
            var progress = new Progress<DevelopProgress>(p => DownloadText = Strings.Format("Develop.Download.Progress", (p.Fraction * 100).ToString("0", CultureInfo.CurrentCulture)));
            await _host.DevelopEngine.DownloadModelAsync(algorithm.Key, progress, CancellationToken.None);
            _engineInfo = await _host.DevelopEngine.HelloAsync(CancellationToken.None);
            UpdateDownload();
            _ = RefreshAsync(full: true);
        }
        catch (DevelopEngineException e)
        {
            Message = e.Message;
            UpdateDownload();
        }
    }

    private void UpdateDownload()
    {
        DownloadText = Method is { } m && _engineInfo?.Algorithms.FirstOrDefault(a => a.Key == m.Key) is { Neural: true, Available: false } a
            ? Strings.Format("Develop.Download", a.Label, a.SizeMb)
            : null;
    }

    public void Dispose()
    {
        _host.WorkingSet.Changed -= OnWorkingSetChanged;
        _prepareCts?.Cancel();
        CancelRenders();
        _saveCts?.Cancel();
        _renderCts?.Dispose();
        _fullCts?.Dispose();
        _histogramCts?.Dispose();
        _saveCts?.Dispose();
    }
}

/// <summary>Where the photos to develop come from (decisions S4, S5).</summary>
public static class DevelopSources
{
    /// <summary>The best and kept photos of a job, one per RAW+JPEG pair (the RAW), in shooting order.</summary>
    public static async Task<IReadOnlyList<long>> BestOfJobAsync(ICatalog catalog, long jobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var items = await catalog.Jobs.GetItemsAsync(jobId, ct);
        var ids = items.Where(i => i.AssetId is not null).Select(i => i.AssetId!.Value).Distinct().ToList();
        var assets = await catalog.Assets.GetManyAsync(ids, ct);
        var result = new List<(long Id, DateTimeOffset? At)>();
        var seen = new HashSet<long>();
        foreach (var id in ids)
        {
            if (!assets.TryGetValue(id, out var asset) || seen.Contains(id))
            {
                continue;
            }

            var chosen = id;
            if (await catalog.Assets.GetTwinAsync(id, ct) is { } twin)
            {
                seen.Add(twin);
                if (assets.TryGetValue(twin, out var other) && other.Handler == "raw")
                {
                    chosen = twin;
                }
            }

            seen.Add(id);
            var decision = await catalog.Decisions.GetAsync(id, ct) ?? (chosen != id ? await catalog.Decisions.GetAsync(chosen, ct) : null);
            if (decision?.EffectiveOutcome is CullingOutcome.Best or CullingOutcome.Kept || decision?.Rating >= 3)
            {
                result.Add((chosen, asset.CapturedAt));
            }
        }

        return [.. result.OrderBy(r => r.At ?? DateTimeOffset.MaxValue).Select(r => r.Id)];
    }
}
