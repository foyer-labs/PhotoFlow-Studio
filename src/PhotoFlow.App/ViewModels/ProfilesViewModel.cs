// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.Analysis.Quality;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;

namespace PhotoFlow.App.ViewModels;

/// <summary>A profile of the list (board 5e): built-in ones are locked.</summary>
public sealed record ProfileListItem(SceneProfile Profile, bool IsFirstCustom)
{
    public string Name => ProfileChoice.NameOf(Profile);

    public string Detail => Profile.IsBuiltIn ? string.Empty
        : Strings.Format("Profiles.Detail", Profile.BasedOn is { } b ? Strings.Get("Profile." + b) : "—", Profile.Version);
}

/// <summary>A choice of a combo box: the value and its label.</summary>
public sealed record OptionItem<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Profiles (board 5e, spec §6, P17): built-in profiles are read-only and get duplicated (Ctrl D); a custom profile is
/// edited — weights summing to 100, blur thresholds, exposure, sharpness zone, burst gap, minimum similarity, tie
/// margin, faces — and saved as a new version of its JSON file (Ctrl S). The values of the original profile are shown
/// beside each field. Changes apply to the next jobs or with "re-analyze" in the review. From enough corrections on the
/// profile, a better blur-discard threshold is suggested; nothing changes until the user applies it and saves.
/// </summary>
public sealed partial class ProfilesViewModel : PageViewModel
{
    private static readonly CommandTable Table = new(
    [
        new("Profiles.Move", new KeyGesture(Key.Up), "People.Command.Navigate", StatusGroupKey: "People.Command.Navigate", Parameter: "-1"),
        new("Profiles.Move", new KeyGesture(Key.Down), "People.Command.Navigate", StatusGroupKey: "People.Command.Navigate", Parameter: "1"),
        new("Profiles.Duplicate", new KeyGesture(Key.D, KeyModifiers.Control), "Profiles.Command.Duplicate", StatusGroupKey: "Profiles.Command.Duplicate"),
        new("Profiles.Save", new KeyGesture(Key.S, KeyModifiers.Control), "Profiles.Command.Save", StatusGroupKey: "Profiles.Command.Save"),
        new("Profiles.Rename", new KeyGesture(Key.F2), "People.Command.Rename", StatusGroupKey: "People.Command.Rename"),
        new("Profiles.Revert", new KeyGesture(Key.Escape), "Profiles.Command.Revert"),
    ]);

    private static readonly string[] EditorProperties =
    [
        nameof(Name), nameof(WeightSharpness), nameof(WeightEyes), nameof(WeightExposure), nameof(WeightSubject), nameof(WeightAesthetic),
        nameof(BlurDiscard), nameof(BlurReview), nameof(Exposure), nameof(Zones), nameof(MaxBurstGap), nameof(MinSimilarity), nameof(TieMargin),
        nameof(Faces), nameof(AbsoluteSharpness),
    ];

    private readonly PhotoFlowHost _host;
    private bool _loading;

    public ProfilesViewModel(PhotoFlowHost host)
    {
        _host = host;
        ExposureOptions = Enum.GetValues<ExposureMode>().Select(e => new OptionItem<ExposureMode>(e, Strings.Get("Profiles.Exposure." + e))).ToList();
        FaceOptions = Enum.GetValues<FaceMode>().Select(f => new OptionItem<FaceMode>(f, Strings.Get("Import.Faces." + f))).ToList();
        ZoneOptions = BuiltInProfiles.All.Select(p => p.SharpnessZones).DistinctBy(z => string.Join(',', z))
            .Select(z => new OptionItem<IReadOnlyList<SharpnessZoneKind>>(z, string.Join(" → ", z.Select(k => Strings.Get("Signal.Sharpness." + k))))).ToList();
    }

    public override string Title => Strings.Get("Nav.Profiles");

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Profiles.Move" => MoveCommand,
        "Profiles.Duplicate" => DuplicateCommand,
        "Profiles.Save" => SaveCommand,
        "Profiles.Rename" => RenameCommand,
        "Profiles.Revert" => RevertCommand,
        _ => base.ResolveCommand(id),
    };

    public ObservableCollection<ProfileListItem> Items { get; } = [];

    public IReadOnlyList<OptionItem<ExposureMode>> ExposureOptions { get; }

    public IReadOnlyList<OptionItem<FaceMode>> FaceOptions { get; }

    public IReadOnlyList<OptionItem<IReadOnlyList<SharpnessZoneKind>>> ZoneOptions { get; }

    [ObservableProperty]
    private ProfileListItem? _selected;

    public bool IsEditable => Selected is { Profile.IsBuiltIn: false };

    public bool IsLocked => Selected is { Profile.IsBuiltIn: true };

    [ObservableProperty]
    private string _usage = string.Empty;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>F2: the view moves the focus to the name field.</summary>
    public event EventHandler? RenameRequested;

    // --- Editor ---

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private decimal _weightSharpness;

    [ObservableProperty]
    private decimal _weightEyes;

    [ObservableProperty]
    private decimal _weightExposure;

    [ObservableProperty]
    private decimal _weightSubject;

    [ObservableProperty]
    private decimal _weightAesthetic;

    [ObservableProperty]
    private decimal _blurDiscard;

    [ObservableProperty]
    private decimal _blurReview;

    [ObservableProperty]
    private OptionItem<ExposureMode>? _exposure;

    [ObservableProperty]
    private OptionItem<IReadOnlyList<SharpnessZoneKind>>? _zones;

    [ObservableProperty]
    private decimal _maxBurstGap;

    [ObservableProperty]
    private decimal _minSimilarity;

    [ObservableProperty]
    private decimal _tieMargin;

    [ObservableProperty]
    private OptionItem<FaceMode>? _faces;

    [ObservableProperty]
    private bool _absoluteSharpness;

    public decimal WeightsSum => WeightSharpness + WeightEyes + WeightExposure + WeightSubject + WeightAesthetic;

    public bool WeightsValid => WeightsSum == 100;

    public string WeightsText => Strings.Format(WeightsValid ? "Profiles.WeightsOk" : "Profiles.WeightsWrong", WeightsSum.ToString("0", CultureInfo.CurrentCulture));

    /// <summary>The values of the profile it was duplicated from (grey marks of 5e).</summary>
    public SceneProfile? Origin => Selected?.Profile is { } p ? (p.BasedOn is { } b ? _host.Profiles.Find(b) : null) ?? p : null;

    public string OriginWeights => Origin is { Weights: var w } ? Strings.Format("Profiles.OriginWeights", $"{w.Sharpness} · {w.Eyes} · {w.Exposure} · {w.Subject} · {w.Aesthetic}") : string.Empty;

    public string OriginBlur => Origin is { } o ? Strings.Format("Profiles.OriginBlur", F(o.Thresholds.BlurDiscard), F(o.Thresholds.BlurReview)) : string.Empty;

    public string OriginExposure => Origin is { } o ? Strings.Format("Profiles.OriginExposure", Strings.Get("Profiles.Exposure." + o.Exposure), string.Join(" → ", o.SharpnessZones.Select(k => Strings.Get("Signal.Sharpness." + k)))) : string.Empty;

    public string OriginGrouping => Origin is { Grouping: var g } ? Strings.Format("Profiles.OriginGrouping", F(g.MaxBurstGapSeconds, "0.0"), F(g.MinSimilarity), F(g.TieMargin)) : string.Empty;

    public string FileText => Selected is { Profile: { IsBuiltIn: false } p }
        ? Strings.Format("Profiles.File", FormattableString.Invariant($"{p.Id}.v{p.Version}.json"))
        : Strings.Get("Profiles.BuiltInFile");

    // --- Suggestion from the corrections ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestion), nameof(SuggestionText))]
    private ThresholdSuggestion? _suggestion;

    public bool HasSuggestion => Suggestion is not null && IsEditable;

    public string SuggestionText => Suggestion is { } s
        ? Strings.Format("Profiles.Suggestion", s.Corrections, F(s.Value), s.AvoidedWrong, s.AddedCaught, s.NewWrong, s.LostCaught)
        : string.Empty;

    private static string F(double value, string format = "0.00") => value.ToString(format, CultureInfo.CurrentCulture);

    public override async Task ActivateAsync()
    {
        _host.Profiles.Reload();
        var selected = Selected?.Profile.Id;
        Load(selected);
        if (_host.Profiles.LoadErrors.Count > 0)
        {
            Message = Strings.Format("Profiles.LoadErrors", string.Join("; ", _host.Profiles.LoadErrors));
        }

        await RefreshDetailsAsync();
    }

    private void Load(string? selectedId)
    {
        Items.Clear();
        var firstCustom = true;
        foreach (var profile in _host.Profiles.All)
        {
            Items.Add(new ProfileListItem(profile, !profile.IsBuiltIn && firstCustom));
            firstCustom &= profile.IsBuiltIn;
        }

        Selected = Items.FirstOrDefault(i => i.Profile.Id == selectedId) ?? Items.FirstOrDefault();
    }

    partial void OnSelectedChanged(ProfileListItem? value)
    {
        _loading = true;
        try
        {
            if (value?.Profile is { } p)
            {
                Name = ProfileChoice.NameOf(p);
                (WeightSharpness, WeightEyes, WeightExposure, WeightSubject, WeightAesthetic) = (p.Weights.Sharpness, p.Weights.Eyes, p.Weights.Exposure, p.Weights.Subject, p.Weights.Aesthetic);
                BlurDiscard = (decimal)p.Thresholds.BlurDiscard;
                BlurReview = (decimal)p.Thresholds.BlurReview;
                Exposure = ExposureOptions.First(o => o.Value == p.Exposure);
                Zones = ZoneOptions.FirstOrDefault(o => o.Value.SequenceEqual(p.SharpnessZones))
                        ?? new OptionItem<IReadOnlyList<SharpnessZoneKind>>(p.SharpnessZones, string.Join(" → ", p.SharpnessZones.Select(k => Strings.Get("Signal.Sharpness." + k))));
                MaxBurstGap = (decimal)p.Grouping.MaxBurstGapSeconds;
                MinSimilarity = (decimal)p.Grouping.MinSimilarity;
                TieMargin = (decimal)p.Grouping.TieMargin;
                Faces = FaceOptions.First(o => o.Value == p.Faces);
                AbsoluteSharpness = p.AbsoluteSharpness;
            }
        }
        finally
        {
            _loading = false;
        }

        IsDirty = false;
        foreach (var name in new[] { nameof(IsEditable), nameof(IsLocked), nameof(Origin), nameof(OriginWeights), nameof(OriginBlur), nameof(OriginExposure), nameof(OriginGrouping), nameof(FileText), nameof(WeightsSum), nameof(WeightsValid), nameof(WeightsText), nameof(HasSuggestion) })
        {
            OnPropertyChanged(name);
        }

        _ = RefreshDetailsAsync();
    }

    private async Task RefreshDetailsAsync()
    {
        if (Selected?.Profile is not { } profile)
        {
            return;
        }

        var jobs = await _host.Catalog.Jobs.ListRecentAsync(100_000, CancellationToken.None);
        Usage = Strings.Format(profile.IsBuiltIn ? "Profiles.UsageBuiltIn" : "Profiles.Usage", jobs.Count(j => j.ProfileId == profile.Id || j.FallbackProfileId == profile.Id));
        Suggestion = profile.IsBuiltIn ? null : ThresholdAdvisor.SuggestBlurDiscard(profile, await _host.Catalog.Decisions.ListCorrectionsAsync(CancellationToken.None));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && EditorProperties.Contains(e.PropertyName))
        {
            IsDirty = IsEditable && BuildProfile() is { } edited && BuiltInProfiles.Serialize(edited) != BuiltInProfiles.Serialize(Selected!.Profile);
            if (e.PropertyName?.StartsWith("Weight", StringComparison.Ordinal) == true)
            {
                OnPropertyChanged(nameof(WeightsSum));
                OnPropertyChanged(nameof(WeightsValid));
                OnPropertyChanged(nameof(WeightsText));
            }
        }
    }

    private SceneProfile? BuildProfile()
    {
        if (Selected?.Profile is not { } p || Exposure is null || Zones is null || Faces is null)
        {
            return null;
        }

        return p with
        {
            Name = Name.Trim(),
            Weights = new ProfileWeights((int)WeightSharpness, (int)WeightEyes, (int)WeightExposure, (int)WeightSubject, (int)WeightAesthetic),
            Thresholds = p.Thresholds with { BlurDiscard = (double)BlurDiscard, BlurReview = (double)BlurReview },
            Exposure = Exposure.Value,
            SharpnessZones = Zones.Value,
            Grouping = new GroupingParameters((double)MaxBurstGap, (double)MinSimilarity, (double)TieMargin),
            Faces = Faces.Value,
            AbsoluteSharpness = AbsoluteSharpness,
        };
    }

    [RelayCommand]
    private void Move(string? delta)
    {
        if (Items.Count == 0 || !int.TryParse(delta, CultureInfo.InvariantCulture, out var d))
        {
            return;
        }

        var index = Selected is null ? 0 : Items.IndexOf(Selected);
        Selected = Items[Math.Clamp(index + d, 0, Items.Count - 1)];
    }

    /// <summary>Ctrl D: a custom copy of the selected profile.</summary>
    [RelayCommand]
    private async Task DuplicateAsync()
    {
        if (Selected?.Profile is not { } source)
        {
            return;
        }

        try
        {
            var copy = _host.Profiles.Duplicate(source.Id, Strings.Format("Profiles.CopyName", ProfileChoice.NameOf(source)));
            Load(copy.Id);
            Message = Strings.Format("Profiles.Duplicated", ProfileChoice.NameOf(source));
            await RefreshDetailsAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Message = Strings.Format("Profiles.SaveFailed", e.Message);
        }
    }

    /// <summary>Ctrl S: saves the custom profile as its next version.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!IsEditable || BuildProfile() is not { } edited)
        {
            Message = IsLocked ? Strings.Get("Profiles.LockedHint") : null;
            return;
        }

        if (!WeightsValid)
        {
            Message = WeightsText;
            return;
        }

        if (edited.Name.Length == 0)
        {
            Message = Strings.Get("Profiles.NameRequired");
            return;
        }

        try
        {
            var saved = _host.Profiles.Save(edited);
            Load(saved.Id);
            Message = Strings.Format("Profiles.Saved", saved.Version);
            await RefreshDetailsAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Message = Strings.Format("Profiles.SaveFailed", e.Message);
        }
    }

    [RelayCommand]
    private void Rename()
    {
        if (IsEditable)
        {
            RenameRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Message = Strings.Get("Profiles.LockedHint");
        }
    }

    /// <summary>Esc: back to the saved values.</summary>
    [RelayCommand]
    private void Revert()
    {
        if (IsDirty)
        {
            OnSelectedChanged(Selected);
            Message = Strings.Get("Profiles.Reverted");
        }
    }

    [RelayCommand]
    private void ApplySuggestion()
    {
        if (Suggestion is { } s && IsEditable)
        {
            BlurDiscard = (decimal)s.Value;
            Message = Strings.Get("Profiles.SuggestionApplied");
        }
    }

    [RelayCommand]
    private void IgnoreSuggestion() => Suggestion = null;
}
