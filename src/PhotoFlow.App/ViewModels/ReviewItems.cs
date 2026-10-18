// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoFlow.App.Localization;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.App.ViewModels;

/// <summary>A photo of the review grid: the files of a RAW+JPEG pair are one photo.</summary>
public sealed partial class ReviewItemViewModel : ObservableObject
{
    private Bitmap? _thumbnail;
    private bool _loading;

    public ReviewItemViewModel(IReadOnlyList<long> assetIds, long analyzedAssetId, string name, string? thumbnailPath, DateTimeOffset? capturedAt, CullingSignals? signals, ExifSummary? exif)
    {
        AssetIds = assetIds;
        AnalyzedAssetId = analyzedAssetId;
        Name = name;
        ThumbnailPath = thumbnailPath;
        CapturedAt = capturedAt;
        Signals = signals;
        Exif = exif;
    }

    public IReadOnlyList<long> AssetIds { get; }

    public long AnalyzedAssetId { get; }

    public string Name { get; }

    public string? ThumbnailPath { get; }

    public DateTimeOffset? CapturedAt { get; }

    public CullingSignals? Signals { get; set; }

    public ExifSummary? Exif { get; }

    /// <summary>The group (burst, bracketing, timelapse, panorama) of the photo, if any.</summary>
    public ReviewGroupViewModel? Group { get; set; }

    /// <summary>Rank in a burst by score (1 = best), or position in another group; null for a single shot.</summary>
    public int? Rank { get; set; }

    public double? Score { get; set; }

    public bool HasPair => AssetIds.Count > 1;

    public string RankText => Rank is { } r && Group is { } g ? $"{r}/{g.Size}" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Outcome), nameof(OutcomeKey), nameof(OutcomeGlyph), nameof(IsManual), nameof(Stars), nameof(StarsText), nameof(OutcomeLabel),
        nameof(OriginLabel), nameof(Why), nameof(IsDiscard), nameof(IsBest), nameof(IsToReview), nameof(ShortReason))]
    private Decision? _decision;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Part of the comparison (Shift+click in the grid, C on a burst).</summary>
    [ObservableProperty]
    private bool _isCompared;

    [ObservableProperty]
    private bool _xmpPending;

    public string StarsText => Stars > 0 ? new string('★', Stars) : string.Empty;

    public CullingOutcome? Outcome => Decision?.EffectiveOutcome;

    /// <summary>Key of the outcome color/glyph in the views: Discard, ToReview, Best, Similar, Kept or None.</summary>
    public string OutcomeKey => Outcome?.ToString() ?? "None";

    /// <summary>Glyph of the outcome (§12bis: recognizable without colour).</summary>
    public string OutcomeGlyph => Outcome switch
    {
        CullingOutcome.Discard => "✕",
        CullingOutcome.ToReview => "?",
        CullingOutcome.Best => "★",
        CullingOutcome.Similar => "≈",
        CullingOutcome.Kept => "✓",
        _ => string.Empty,
    };

    public bool IsDiscard => Outcome == CullingOutcome.Discard;

    public bool IsBest => Outcome == CullingOutcome.Best;

    public bool IsToReview => Outcome == CullingOutcome.ToReview;

    public bool IsManual => Decision?.UserOutcome is not null || Decision?.Rating is not null;

    public int Stars => Decision?.Rating ?? 0;

    public string OutcomeLabel => Outcome is { } o ? Strings.Get("Outcome." + o) : Strings.Get("Outcome.None");

    public string OriginLabel => Strings.Get(IsManual ? "Review.Origin.Manual" : "Review.Origin.Automatic");

    public string Why => Decision is null || Decision.Reasons.Count == 0 || Decision.UserOutcome is not null
        ? string.Empty
        : string.Join(" · ", Decision.Reasons.Select(r => Strings.Get("Reason." + r)));

    /// <summary>Bottom-right short reason of the thumbnail (§12bis).</summary>
    public string ShortReason => Decision is { UserOutcome: null, Reasons.Count: > 0 } d ? Strings.Get("Reason." + d.Reasons[0]) : string.Empty;

    /// <summary>Thumbnail decoded on demand (rows are virtualized: only visible photos load).</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_loading && ThumbnailPath is { } path && File.Exists(path))
            {
                _loading = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var stream = File.OpenRead(path);
                        var bitmap = Bitmap.DecodeToWidth(stream, 320);
                        Ui.Post(() =>
                        {
                            _thumbnail = bitmap;
                            OnPropertyChanged(nameof(Thumbnail));
                        });
                    }
                    catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
                    {
                        // Unreadable thumbnail: the cell stays empty.
                    }
                });
            }

            return _thumbnail;
        }
    }
}

/// <summary>A group of the job shown as a header above its photos (board 2a).</summary>
public sealed partial class ReviewGroupViewModel(AssetGroup group, int number, IReadOnlyList<ReviewItemViewModel> members, string profileId) : ObservableObject
{
    public AssetGroup Group { get; } = group;

    public long Id => Group.Id;

    public int Number { get; } = number;

    public IReadOnlyList<ReviewItemViewModel> Members { get; } = members;

    public int Size => Members.Count;

    public string ProfileId { get; set; } = profileId;

    /// <summary>Profile CLIP finds more fitting (manual mode, D8), with its confidence.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestion), nameof(SuggestionText))]
    private (string ProfileId, double Confidence)? _suggestion;

    public bool HasSuggestion => Suggestion is not null;

    public string SuggestionText => Suggestion is { } s
        ? Strings.Format("Review.Group.Suggestion", NameOfProfile(s.ProfileId), s.Confidence.ToString("P0", CultureInfo.CurrentCulture))
        : string.Empty;

    public string Title => Strings.Format("Review.Group." + Group.Type, Number);

    public string Meta
    {
        get
        {
            var times = Members.Where(m => m.CapturedAt is not null).Select(m => m.CapturedAt!.Value).Order().ToList();
            var span = times.Count >= 2
                ? $" · {times[0].ToLocalTime().ToString("HH:mm:ss,f", CultureInfo.CurrentCulture)} – {times[^1].ToLocalTime().ToString("HH:mm:ss,f", CultureInfo.CurrentCulture)}"
                : string.Empty;
            return Strings.Format("Review.Group.Shots", Size) + span;
        }
    }

    /// <summary>Display name of a profile id (custom profiles have their own name).</summary>
    public Func<string, string> NameOfProfile { get; init; } = id => Strings.Get("Profile." + id);

    public string ProfileText => NameOfProfile(ProfileId);

    /// <summary>"First two tied: choose the best" when the ranking ended in a tie (P17).</summary>
    public string Note => Group.Type switch
    {
        GroupType.Burst when Members.Count(m => m.Decision?.Reasons.Contains(Analysis.Grouping.BurstRanker.Reasons.Tie) == true) >= 2 => Strings.Get("Review.Group.Tie"),
        GroupType.Bracketing => Strings.Get("Review.Group.BracketingNote"),
        GroupType.Timelapse => Strings.Get("Review.Group.TimelapseNote"),
        _ => string.Empty,
    };

    public bool HasNote => Note.Length > 0;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(ProfileText));
    }
}

/// <summary>A row of the virtualized grid: a group header or photos.</summary>
public abstract record ReviewRow;

public sealed record ReviewHeaderRow(ReviewGroupViewModel Group) : ReviewRow;

public sealed record ReviewRowViewModel(IReadOnlyList<ReviewItemViewModel> Items) : ReviewRow;

/// <summary>Outcome filter chip with its count (board 2a).</summary>
public sealed partial class OutcomeFilterViewModel(CullingOutcome? outcome) : ObservableObject
{
    public CullingOutcome? Outcome { get; } = outcome;

    public string Label => Outcome is { } o ? Strings.Get("Outcome." + o) : Strings.Get("Review.Filter.All");

    public string Key => Outcome?.ToString() ?? "All";

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>A signal bar of the details panel: value, profile threshold, text.</summary>
public sealed record SignalViewModel(string Label, double Value, double? Threshold, string Text);

/// <summary>A person on the selected photo (chips of the details panel): certain, or suggested with its similarity.</summary>
public sealed record PersonChipViewModel(string Name, bool IsSuggestion, string Similarity);

/// <summary>A photo of the comparison (board 2b), with its own preview.</summary>
public sealed partial class CompareItemViewModel(ReviewItemViewModel photo, int index) : ObservableObject
{
    public ReviewItemViewModel Photo { get; } = photo;

    public int Index { get; } = index;

    public string Number => (Index + 1).ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private Bitmap? _preview;

    [ObservableProperty]
    private bool _isFocused;

    public string SignalsText => Photo.Signals is { } s
        ? string.Create(CultureInfo.CurrentCulture, $"{Strings.Get("Review.Compare.Sharpness")} {s.ZoneSharpness:0.00} · {Strings.Get("Review.Compare.Exposure")} {1 - Math.Min(1, s.ZoneHighlights + s.ZoneShadows):0.00}")
        : string.Empty;
}
