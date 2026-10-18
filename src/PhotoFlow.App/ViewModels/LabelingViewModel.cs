// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.Analysis.Quality;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.App.ViewModels;

/// <summary>A photo of the labelling session.</summary>
public sealed record LabelingPhoto(long AssetId, string Name, string? ThumbnailPath, CullingOutcome? Automatic, string ProfileId, long? GroupId, double Priority);

/// <summary>A metric bar of the panel (board 2d): value, 95 % interval, target, verdict.</summary>
public sealed record MetricViewModel(string Label, string Value, string Interval, string Target, double Low, double High, double TargetValue, string Verdict, bool IsWithin);

/// <summary>
/// Labelling mode (board 2d, spec §14): short sessions of about 100 photos, blind by default (the verdict of the
/// system stays hidden), one key per photo (1 good, 2 blurred, 3 eyes closed, 4 exposure, 5 other, Space skip,
/// Ctrl+Z correct the previous one), bursts shown whole with Enter on the best. The most useful photos come first:
/// those near the thresholds, the uncertain ones, the less represented profiles. The metrics panel shows the three
/// culling metrics with their 95 % interval; the normal mode unlocks only when all three are within target (D23).
/// </summary>
public sealed partial class LabelingViewModel : PageViewModel
{
    public const int SessionSize = 100;

    private static readonly CommandTable Table = new(
    [
        new("Labeling.Label", new KeyGesture(Key.D1), "Labeling.Command.Label", StatusGroupKey: "Labeling.Command.Label", Parameter: nameof(PhotoLabel.Good)),
        new("Labeling.Label", new KeyGesture(Key.D2), "Labeling.Command.Label", StatusGroupKey: "Labeling.Command.Label", Parameter: nameof(PhotoLabel.Blurred)),
        new("Labeling.Label", new KeyGesture(Key.D3), "Labeling.Command.Label", StatusGroupKey: "Labeling.Command.Label", Parameter: nameof(PhotoLabel.EyesClosed)),
        new("Labeling.Label", new KeyGesture(Key.D4), "Labeling.Command.Label", StatusGroupKey: "Labeling.Command.Label", Parameter: nameof(PhotoLabel.Exposure)),
        new("Labeling.Label", new KeyGesture(Key.D5), "Labeling.Command.Label", StatusGroupKey: "Labeling.Command.Label", Parameter: nameof(PhotoLabel.Other)),
        new("Labeling.Skip", new KeyGesture(Key.Space), "Labeling.Command.Skip", StatusGroupKey: "Labeling.Command.Skip"),
        new("Labeling.BurstBest", new KeyGesture(Key.Enter), "Labeling.Command.BurstBest", StatusGroupKey: "Labeling.Command.BurstBest"),
        new("Labeling.Move", new KeyGesture(Key.Left), "Labeling.Command.Burst", Parameter: "-1"),
        new("Labeling.Move", new KeyGesture(Key.Right), "Labeling.Command.Burst", Parameter: "1"),
        new("Labeling.Zoom", new KeyGesture(Key.Z), "Review.Command.Zoom", StatusGroupKey: "Review.Command.Zoom"),
        new("Labeling.Undo", new KeyGesture(Key.Z, KeyModifiers.Control), "Review.Command.Undo", StatusGroupKey: "Review.Command.Undo"),
        new("Labeling.Exit", new KeyGesture(Key.Escape), "Labeling.Command.Exit", StatusGroupKey: "Labeling.Command.Exit"),
    ]);

    private readonly PhotoFlowHost _host;
    private readonly IShellNavigator _navigator;
    private readonly long? _jobId;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly Stack<(int Index, IReadOnlyList<long> Assets)> _done = new();
    private readonly Stopwatch _clock = new();
    private List<LabelingPhoto> _photos = [];

    public LabelingViewModel(PhotoFlowHost host, IShellNavigator navigator, long? jobId)
    {
        _host = host;
        _navigator = navigator;
        _jobId = jobId;
    }

    public override string Title => Strings.Get("Labeling.Title");

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Labeling.Label" => LabelCommand,
        "Labeling.Skip" => SkipCommand,
        "Labeling.BurstBest" => BurstBestCommand,
        "Labeling.Move" => MoveInBurstCommand,
        "Labeling.Zoom" => ToggleZoomCommand,
        "Labeling.Undo" => UndoCommand,
        "Labeling.Exit" => ExitCommand,
        _ => base.ResolveCommand(id),
    };

    public ObservableCollection<LabelingPhoto> Burst { get; } = [];

    public ObservableCollection<MetricViewModel> Metrics { get; } = [];

    public ObservableCollection<string> PerProfile { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress), nameof(HasPhoto), nameof(CurrentName), nameof(Verdict), nameof(IsBurst))]
    private int _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentName), nameof(Verdict))]
    private LabelingPhoto? _current;

    [ObservableProperty]
    private Bitmap? _preview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Verdict))]
    private bool _isBlind = true;

    [ObservableProperty]
    private bool _isZoomed;

    [ObservableProperty]
    private string? _previous;

    [ObservableProperty]
    private string _labeledText = string.Empty;

    [ObservableProperty]
    private string _modeNote = string.Empty;

    [ObservableProperty]
    private bool _isFinished;

    public bool HasPhoto => Current is not null;

    public bool IsBurst => Burst.Count > 1;

    public string CurrentName => Current is { } c ? $"{c.Name} · {_host.ProfileName(c.ProfileId)}{(IsBurst ? " · " + Strings.Format("Labeling.BurstOf", Burst.Count) : string.Empty)}" : string.Empty;

    public string Verdict => IsBlind || Current?.Automatic is not { } a ? string.Empty : Strings.Format("Labeling.Verdict", Strings.Get("Outcome." + a));

    public string Progress
    {
        get
        {
            var remaining = _photos.Count - Index;
            var perPhoto = _done.Count > 0 ? _clock.Elapsed.TotalSeconds / _done.Count : 9;
            return Strings.Format("Labeling.Progress", Math.Min(Index + 1, _photos.Count), _photos.Count, Formats.Duration(TimeSpan.FromSeconds(remaining * perPhoto)));
        }
    }

    public override async Task ActivateAsync()
    {
        _photos = await SelectPhotosAsync();
        Index = 0;
        IsFinished = _photos.Count == 0;
        _clock.Restart();
        await ShowAsync();
        await RefreshMetricsAsync();
    }

    /// <summary>The most useful photos first (spec §14): near a threshold, uncertain, profiles with few labels.</summary>
    private async Task<List<LabelingPhoto>> SelectPhotosAsync()
    {
        var catalog = _host.Catalog;
        var labeled = (await catalog.Labels.ListAsync(CancellationToken.None)).Select(l => l.AssetId).ToHashSet();
        var jobs = _jobId is { } id ? [id] : (await catalog.Jobs.ListRecentAsync(20, CancellationToken.None)).Select(j => j.Id).ToList();
        var candidates = new List<LabelingPhoto>();
        foreach (var jobId in jobs)
        {
            var groups = (await catalog.Groups.ListAsync(jobId, CancellationToken.None)).Where(g => g.Group.IsRanked)
                .SelectMany(g => g.Members.Select(m => (m.AssetId, g.Group.Id))).ToDictionary(x => x.AssetId, x => x.Id);
            var items = (await catalog.Jobs.GetItemsAsync(jobId, CancellationToken.None)).Where(i => i.AssetId is not null).ToList();
            var results = await catalog.Analysis.GetResultsAsync(items.Select(i => i.AssetId!.Value).Distinct().ToList(), CullingAnalyzer.AnalyzerId, CancellationToken.None);
            foreach (var (assetId, result) in results)
            {
                if (labeled.Contains(assetId) || candidates.Any(c => c.AssetId == assetId))
                {
                    continue;
                }

                var asset = await catalog.Assets.GetAsync(assetId, CancellationToken.None);
                var decision = await catalog.Decisions.GetAsync(assetId, CancellationToken.None);
                var item = items.First(i => i.AssetId == assetId);
                candidates.Add(new LabelingPhoto(assetId, Path.GetFileName(item.SourcePath), asset is null ? null : new[] { asset.FullHash, asset.QuickHash }.OfType<string>().Select(_host.Thumbnails.PathFor).FirstOrDefault(File.Exists),
                    decision?.AutomaticOutcome, result.ProfileId, groups.TryGetValue(assetId, out var g) ? g : null, Priority(result, _host.Profiles)));
            }
        }

        var perProfile = candidates.GroupBy(c => c.ProfileId).ToDictionary(g => g.Key, g => g.Count());
        return candidates
            .OrderByDescending(c => c.Priority + (c.Automatic == CullingOutcome.ToReview ? 0.5 : 0) + (1.0 / perProfile[c.ProfileId]))
            .ThenBy(c => c.AssetId)
            .Take(SessionSize)
            .ToList();
    }

    /// <summary>1 at a threshold, falling to 0 a few hundredths away (sharpness, eyes).</summary>
    private static double Priority(AnalyzerResultRecord result, ProfileLibrary profiles)
    {
        try
        {
            var s = CullingScores.FromScores(result.Scores);
            var t = profiles.Get(result.ProfileId).Thresholds;
            var sharpness = Math.Max(Near(s.ZoneSharpness, t.BlurReview), Near(s.ZoneSharpness, t.BlurDiscard));
            var eyes = s.MainFaceLeftEyeRatio is { } l && s.MainFaceRightEyeRatio is { } r ? Near(Math.Min(l, r), t.EyesClosed) : 0;
            return Math.Max(sharpness, eyes);
        }
        catch (KeyNotFoundException)
        {
            return 0;
        }

        static double Near(double value, double threshold) => Math.Max(0, 1 - (Math.Abs(value - threshold) / 0.1));
    }

    private async Task ShowAsync()
    {
        Current = Index < _photos.Count ? _photos[Index] : null;
        IsFinished = Current is null;
        Burst.Clear();
        Preview = null;
        if (Current is not { } photo)
        {
            return;
        }

        if (photo.GroupId is { } group)
        {
            foreach (var member in _photos.Where(p => p.GroupId == group))
            {
                Burst.Add(member);
            }
        }

        OnPropertyChanged(nameof(IsBurst));
        OnPropertyChanged(nameof(CurrentName));
        var path = await AssetFiles.LocateAsync(_host.Catalog, _host.FileSystem, photo.AssetId, CancellationToken.None);
        Bitmap? bitmap = null;
        if (photo.ThumbnailPath is { } thumbnail && File.Exists(thumbnail))
        {
            bitmap = await Task.Run(() =>
            {
                try
                {
                    using var stream = File.OpenRead(thumbnail);
                    return new Bitmap(stream);
                }
                catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
                {
                    return null;
                }
            });
        }

        if (Current == photo)
        {
            Preview = bitmap;
            ModeNote = path is null ? Strings.Get("Labeling.Offline") : string.Empty;
        }
    }

    [RelayCommand]
    private async Task LabelAsync(string? label)
    {
        if (Current is not { } photo || !Enum.TryParse<PhotoLabel>(label, out var value))
        {
            return;
        }

        await _host.Catalog.Labels.SetAsync(photo.AssetId, value, _sessionId, CancellationToken.None);
        _done.Push((Index, [photo.AssetId]));
        Previous = Strings.Format("Labeling.Previous", photo.Name, Strings.Get("Label." + value));
        await AdvanceAsync(1);
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        _done.Push((Index, []));
        await AdvanceAsync(1);
    }

    /// <summary>Enter on a burst: the shown photo is its best; the burst is done.</summary>
    [RelayCommand]
    private async Task BurstBestAsync()
    {
        if (Current is not { GroupId: { } group } photo)
        {
            return;
        }

        await _host.Catalog.Labels.SetAsync(photo.AssetId, PhotoLabel.BurstBest, _sessionId, CancellationToken.None);
        _done.Push((Index, [photo.AssetId]));
        Previous = Strings.Format("Labeling.Previous", photo.Name, Strings.Get("Label." + PhotoLabel.BurstBest));
        var last = _photos.FindLastIndex(p => p.GroupId == group);
        await AdvanceAsync(Math.Max(1, last - Index + 1));
    }

    [RelayCommand]
    private async Task MoveInBurstAsync(string? delta)
    {
        if (Current is not { GroupId: not null } photo || !int.TryParse(delta, CultureInfo.InvariantCulture, out var d))
        {
            return;
        }

        var members = Burst.ToList();
        var target = members[Math.Clamp(members.IndexOf(photo) + d, 0, members.Count - 1)];
        Index = _photos.IndexOf(target);
        await ShowAsync();
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (!_done.TryPop(out var entry))
        {
            return;
        }

        foreach (var assetId in entry.Assets)
        {
            await _host.Catalog.Labels.RemoveAsync(assetId, CancellationToken.None);
        }

        Previous = null;
        Index = entry.Index;
        await ShowAsync();
        await RefreshMetricsAsync();
    }

    [RelayCommand]
    private void ToggleZoom() => IsZoomed = !IsZoomed;

    /// <summary>Esc: the session can stop at any time; labels are already saved.</summary>
    [RelayCommand]
    private void Exit() => _navigator.ShowSection(SectionCatalog.Review);

    private async Task AdvanceAsync(int step)
    {
        Index = Math.Min(Index + step, _photos.Count);
        await ShowAsync();
        await RefreshMetricsAsync();
    }

    public async Task RefreshMetricsAsync()
    {
        var metrics = await ComputeMetricsAsync(_host.Catalog);
        Metrics.Clear();
        foreach (var metric in new[] { metrics.WrongDiscards, metrics.Intercepted, metrics.ToReview })
        {
            var (low, high) = metric.Proportion.Interval;
            Metrics.Add(new MetricViewModel(
                Strings.Get("Metric." + metric.Key),
                metric.Proportion.Value.ToString("P1", CultureInfo.CurrentCulture),
                metric.Proportion.Total == 0 ? "—" : $"{low.ToString("P1", CultureInfo.CurrentCulture)} – {high.ToString("P1", CultureInfo.CurrentCulture)}",
                Strings.Format(metric.LowerIsBetter ? "Metric.TargetBelow" : "Metric.TargetAbove", metric.Target.ToString("P0", CultureInfo.CurrentCulture)),
                low, high, metric.Target,
                Strings.Get("Metric.Verdict." + metric.Verdict),
                metric.Verdict == MetricVerdict.WithinTarget));
        }

        PerProfile.Clear();
        foreach (var profile in _host.Profiles.All.Where(p => p.Id != "archive"))
        {
            PerProfile.Add($"{ProfileChoice.NameOf(profile)} {metrics.PerProfile.GetValueOrDefault(profile.Id).ToString(CultureInfo.CurrentCulture)}");
        }

        LabeledText = Strings.Format("Labeling.Labeled", metrics.Labeled.ToString("N0", CultureInfo.CurrentCulture));
        ModeNote = metrics.NormalModeAllowed ? Strings.Get("Labeling.NormalAllowed") : Strings.Get("Labeling.PrudentStays");
        OnPropertyChanged(nameof(Progress));
    }

    /// <summary>The metrics from every label of the catalog, with the automatic verdict and the profile of each photo.</summary>
    public static async Task<CullingMetrics> ComputeMetricsAsync(ICatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var labels = await catalog.Labels.ListAsync(CancellationToken.None);
        var results = await catalog.Analysis.GetResultsAsync(labels.Select(l => l.AssetId).ToList(), CullingAnalyzer.AnalyzerId, CancellationToken.None);
        var verdicts = new List<LabeledVerdict>(labels.Count);
        foreach (var label in labels)
        {
            var decision = await catalog.Decisions.GetAsync(label.AssetId, CancellationToken.None);
            verdicts.Add(new LabeledVerdict(label.Label, decision?.AutomaticOutcome, results.GetValueOrDefault(label.AssetId)?.ProfileId ?? "events"));
        }

        return CullingMetrics.Compute(verdicts);
    }
}
