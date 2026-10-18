// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Commands;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Tagging.Vocabulary;

namespace PhotoFlow.App.ViewModels;

/// <summary>A row of the vocabulary (board 5c): English id, Italian label, category, photos, state.</summary>
public sealed partial class VocabularyRow(Tag tag) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Italian), nameof(Category), nameof(StateText), nameof(Photos))]
    private Tag _tag = tag;

    public long Id => Tag.Id;

    public string English => Tag.English;

    public string Italian => Tag.Italian ?? "—";

    public string Category => Tag.Category ?? "—";

    public string Photos => Tag.PhotoCount.ToString("N0", CultureInfo.CurrentCulture);

    public string StateText => Strings.Get(Tag.Blacklisted ? "Vocabulary.State.Blacklisted" : Tag.Reviewed ? "Vocabulary.State.Reviewed" : "Vocabulary.State.ToReview");

    [ObservableProperty]
    private bool _isSelected;
}

public enum VocabularyTab
{
    Core,
    All,
    Custom,
    Blacklist,
    Categories,
}

/// <summary>A personal CLIP tag of the "Personalizzati" tab (5d).</summary>
public sealed record CustomTagRow(Tag Tag, CustomTag Custom)
{
    public string Label => Tag.Italian ?? Tag.English;

    public string Threshold => Custom.Threshold.ToString("0.00", CultureInfo.CurrentCulture);

    public string Photos => Tag.PhotoCount.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>A category with the number of tags it holds.</summary>
public sealed record CategoryRow(string Name, int Tags);

/// <summary>
/// Vocabolario (boards 5c, 5d, 5d2; spec §8, P15): the core (the 500 most frequent tags once 1,000 photos have tags) to
/// review one row at a time — Enter approves and moves on, F2 edits the Italian label, B blacklist, K category; all
/// tags; personal CLIP tags with a trial on the archive (Ctrl T) and "save and re-analyze" (Ctrl S); blacklist;
/// categories. Until a label is reviewed only the English key is written (F3-2).
/// </summary>
public sealed partial class VocabularyViewModel : PageViewModel
{
    public const int CoreSize = 500;
    public const int CoreMinimumPhotos = 1000;

    public static readonly IReadOnlyList<string?> CategoryCycle = [null, "Persone", "Luoghi", "Natura", "Veicoli", "Eventi", "Animali"];

    private static readonly CommandTable Table = new(
    [
        new("Vocabulary.Move", new KeyGesture(Key.Up), "Vocabulary.Command.Row", StatusGroupKey: "Vocabulary.Command.Row", Parameter: "-1"),
        new("Vocabulary.Move", new KeyGesture(Key.Down), "Vocabulary.Command.Row", StatusGroupKey: "Vocabulary.Command.Row", Parameter: "1"),
        new("Vocabulary.Approve", new KeyGesture(Key.Enter), "Vocabulary.Command.Approve", StatusGroupKey: "Vocabulary.Command.Approve"),
        new("Vocabulary.Edit", new KeyGesture(Key.F2), "Vocabulary.Command.Edit", StatusGroupKey: "Vocabulary.Command.Edit"),
        new("Vocabulary.Blacklist", new KeyGesture(Key.B), "Vocabulary.Command.Blacklist", StatusGroupKey: "Vocabulary.Command.Blacklist"),
        new("Vocabulary.Category", new KeyGesture(Key.K), "Vocabulary.Command.Category", StatusGroupKey: "Vocabulary.Command.Category"),
        new("Vocabulary.Undo", new KeyGesture(Key.Z, KeyModifiers.Control), "Vocabulary.Command.Undo", StatusGroupKey: "Vocabulary.Command.Undo"),
        new("Vocabulary.New", new KeyGesture(Key.N, KeyModifiers.Control), "Vocabulary.Command.New"),
        new("Vocabulary.Trial", new KeyGesture(Key.T, KeyModifiers.Control), "Vocabulary.Command.Trial"),
        new("Vocabulary.Save", new KeyGesture(Key.S, KeyModifiers.Control), "Vocabulary.Command.Save"),
        new("Vocabulary.Escape", new KeyGesture(Key.Escape), "Common.Cancel"),
    ]);

    private readonly PhotoFlowHost _host;
    private readonly Stack<Tag> _undo = new();
    private List<Tag> _tags = [];

    public VocabularyViewModel(PhotoFlowHost host) => _host = host;

    public override string Title => Strings.Get("Nav.Vocabulary");

    public override CommandTable Commands => Table;

    public override ICommand ResolveCommand(string id) => id switch
    {
        "Vocabulary.Move" => MoveCommand,
        "Vocabulary.Approve" => ApproveCommand,
        "Vocabulary.Edit" => EditCommand,
        "Vocabulary.Blacklist" => BlacklistCommand,
        "Vocabulary.Category" => CycleCategoryCommand,
        "Vocabulary.Undo" => UndoCommand,
        "Vocabulary.New" => NewCustomCommand,
        "Vocabulary.Trial" => TrialCommand,
        "Vocabulary.Save" => SaveCustomCommand,
        "Vocabulary.Escape" => EscapeCommand,
        _ => base.ResolveCommand(id),
    };

    /// <summary>F2: the view focuses the label field.</summary>
    public event EventHandler? EditRequested;

    public ObservableCollection<VocabularyRow> Rows { get; } = [];

    public ObservableCollection<CustomTagRow> CustomTags { get; } = [];

    public ObservableCollection<CategoryRow> Categories { get; } = [];

    public ObservableCollection<Bitmap> Examples { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCore), nameof(IsAll), nameof(IsCustom), nameof(IsBlacklist), nameof(IsCategories), nameof(IsRowsTab))]
    private VocabularyTab _tab = VocabularyTab.Core;

    public bool IsCore => Tab == VocabularyTab.Core;

    public bool IsAll => Tab == VocabularyTab.All;

    public bool IsCustom => Tab == VocabularyTab.Custom;

    public bool IsBlacklist => Tab == VocabularyTab.Blacklist;

    public bool IsCategories => Tab == VocabularyTab.Categories;

    public bool IsRowsTab => Tab is VocabularyTab.Core or VocabularyTab.All or VocabularyTab.Blacklist;

    [ObservableProperty]
    private string _coreText = string.Empty;

    [ObservableProperty]
    private string _allText = string.Empty;

    [ObservableProperty]
    private string _customText = string.Empty;

    [ObservableProperty]
    private string _blacklistText = string.Empty;

    [ObservableProperty]
    private string _categoriesText = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>5d2: fewer than 1,000 photos with tags, the core is not formed yet.</summary>
    [ObservableProperty]
    private bool _isCoreNotReady;

    [ObservableProperty]
    private string _coreNotReadyText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private VocabularyRow? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    private string _detailTitle = string.Empty;

    [ObservableProperty]
    private string _writtenAs = string.Empty;

    [ObservableProperty]
    private string _hierarchy = string.Empty;

    [ObservableProperty]
    private string _thresholdText = string.Empty;

    [ObservableProperty]
    private string? _editText;

    public bool IsEditing => EditText is not null;

    [ObservableProperty]
    private decimal _globalAdjustment;

    [ObservableProperty]
    private string? _message;

    // --- Personal tag form (5d) ---

    [ObservableProperty]
    private string _customLabel = string.Empty;

    [ObservableProperty]
    private string _customDescription = string.Empty;

    [ObservableProperty]
    private string? _customCategory;

    [ObservableProperty]
    private decimal _customThreshold = 0.27m;

    [ObservableProperty]
    private decimal _customMargin = 0.03m;

    [ObservableProperty]
    private string? _trialText;

    [ObservableProperty]
    private CustomTagRow? _selectedCustom;

    public IReadOnlyList<string?> CategoryOptions => CategoryCycle;

    partial void OnEditTextChanged(string? value) => OnPropertyChanged(nameof(IsEditing));

    partial void OnSelectedCustomChanged(CustomTagRow? value)
    {
        if (value is not null)
        {
            (CustomLabel, CustomDescription, CustomCategory, CustomThreshold, CustomMargin) =
                (value.Label, value.Custom.ClipDescription, value.Tag.Category, (decimal)value.Custom.Threshold, (decimal)value.Custom.Margin);
            TrialText = null;
        }
    }

    public override async Task ActivateAsync()
    {
        GlobalAdjustment = (decimal)_host.Settings.Tagging.ThresholdAdjustment;
        await ReloadAsync(Selected?.Id);
    }

    private async Task ReloadAsync(long? select)
    {
        _tags = [.. await _host.Catalog.Tags.ListAsync(CancellationToken.None)];
        var custom = await _host.Catalog.Tags.ListCustomAsync(CancellationToken.None);
        var tagged = await _host.Catalog.Tags.CountTaggedAssetsAsync(CancellationToken.None);
        var core = Core();
        IsCoreNotReady = tagged < CoreMinimumPhotos;
        CoreNotReadyText = Strings.Format("Vocabulary.CoreNotReady", tagged.ToString("N0", CultureInfo.CurrentCulture), CoreMinimumPhotos.ToString("N0", CultureInfo.CurrentCulture));
        var reviewed = core.Count(t => t.Reviewed || t.Blacklisted);
        CoreText = Strings.Format("Vocabulary.Tab.Core", core.Count - reviewed);
        AllText = Strings.Format("Vocabulary.Tab.All", _tags.Count(t => t.Source == TagSource.RamPlus).ToString("N0", CultureInfo.CurrentCulture));
        CustomText = Strings.Format("Vocabulary.Tab.Custom", custom.Count);
        BlacklistText = Strings.Format("Vocabulary.Tab.Blacklist", _tags.Count(t => t.Blacklisted));
        CategoriesText = Strings.Format("Vocabulary.Tab.Categories", CategoryCycle.Count - 1);
        Summary = Strings.Format("Vocabulary.Summary", reviewed, core.Count);

        CustomTags.Clear();
        foreach (var (tag, definition) in custom)
        {
            CustomTags.Add(new CustomTagRow(tag, definition));
        }

        Categories.Clear();
        foreach (var category in CategoryCycle.OfType<string>())
        {
            Categories.Add(new CategoryRow(category, _tags.Count(t => t.Category == category && !t.Blacklisted)));
        }

        Rows.Clear();
        IEnumerable<Tag> rows = Tab switch
        {
            VocabularyTab.Core => IsCoreNotReady ? [] : core.OrderBy(t => t.Reviewed || t.Blacklisted).ThenByDescending(t => t.PhotoCount),
            VocabularyTab.Blacklist => _tags.Where(t => t.Blacklisted).OrderBy(t => t.Key, StringComparer.Ordinal),
            _ => _tags.Where(t => t.Source == TagSource.RamPlus).OrderByDescending(t => t.PhotoCount).ThenBy(t => t.Key, StringComparer.Ordinal),
        };
        foreach (var tag in rows)
        {
            Rows.Add(new VocabularyRow(tag));
        }

        Selected = Rows.FirstOrDefault(r => r.Id == select) ?? Rows.FirstOrDefault();
    }

    /// <summary>The 500 most frequent RAM++ tags of the archive (P15).</summary>
    private List<Tag> Core() =>
        [.. _tags.Where(t => t.Source == TagSource.RamPlus && t.PhotoCount > 0).OrderByDescending(t => t.PhotoCount).Take(CoreSize)];

    [RelayCommand]
    private async Task SetTabAsync(string? tab)
    {
        if (Enum.TryParse<VocabularyTab>(tab, out var value))
        {
            Tab = value;
            EditText = null;
            await ReloadAsync(null);
        }
    }

    partial void OnSelectedChanged(VocabularyRow? oldValue, VocabularyRow? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        EditText = null;
        _ = LoadDetailAsync(newValue);
    }

    private async Task LoadDetailAsync(VocabularyRow? row)
    {
        Examples.Clear();
        if (row is null)
        {
            return;
        }

        var tag = row.Tag;
        DetailTitle = $"{tag.English} → {tag.Italian ?? "—"}";
        var flat = tag.Reviewed && tag.Italian is { } it ? $"{it}, {tag.English}" : tag.English;
        WrittenAs = tag.Blacklisted ? Strings.Get("Vocabulary.NeverWritten") : Strings.Format("Vocabulary.WrittenAs", flat);
        Hierarchy = tag.Blacklisted || tag.Category is null ? string.Empty
            : Strings.Format("Vocabulary.Hierarchy", $"{tag.Category}|{Capitalize(tag.Reviewed && tag.Italian is { } label ? label : tag.English)}");
        var official = _host.RamPlusThreshold(tag.Key);
        ThresholdText = official is { } t
            ? Strings.Format("Vocabulary.Threshold", t.ToString("0.00", CultureInfo.CurrentCulture), GlobalAdjustment.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture))
            : string.Empty;
        foreach (var (assetId, _) in await _host.Catalog.Tags.ListExamplesAsync(tag.Id, 6, CancellationToken.None))
        {
            var asset = await _host.Catalog.Assets.GetAsync(assetId, CancellationToken.None);
            var thumbnail = asset is null ? null : new[] { asset.FullHash, asset.QuickHash }.OfType<string>().Select(_host.Thumbnails.PathFor).FirstOrDefault(File.Exists);
            if (thumbnail is not null && await Task.Run(() => UiBitmaps.FromFile(thumbnail, 160)) is { } bitmap && Selected == row)
            {
                Examples.Add(bitmap);
            }
        }

        static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
    }

    [RelayCommand]
    private void Move(string? delta)
    {
        if (Rows.Count == 0 || !int.TryParse(delta, CultureInfo.InvariantCulture, out var d))
        {
            return;
        }

        var index = Selected is null ? 0 : Rows.IndexOf(Selected);
        Selected = Rows[Math.Clamp(index + d, 0, Rows.Count - 1)];
    }

    public void Click(VocabularyRow row) => Selected = row;

    /// <summary>Enter: the label typed (when editing) or the proposed one is approved; next row.</summary>
    [RelayCommand]
    private async Task ApproveAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        var label = EditText is { } edited && edited.Trim().Length > 0 ? edited.Trim() : row.Tag.Italian;
        EditText = null;
        await UpdateAsync(row, row.Tag with { Italian = label, Reviewed = true });
        Move("1");
    }

    [RelayCommand]
    private void Edit()
    {
        if (Selected is { } row)
        {
            EditText = row.Tag.Italian ?? row.Tag.English;
            EditRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private async Task BlacklistAsync()
    {
        if (Selected is { } row)
        {
            await UpdateAsync(row, row.Tag with { Blacklisted = !row.Tag.Blacklisted });
        }
    }

    /// <summary>K: the next category (none, Persone, Luoghi, Natura, Veicoli, Eventi, Animali).</summary>
    [RelayCommand]
    private async Task CycleCategoryAsync()
    {
        if (Selected is { } row)
        {
            var index = CategoryCycle.ToList().IndexOf(row.Tag.Category);
            await UpdateAsync(row, row.Tag with { Category = CategoryCycle[(index + 1) % CategoryCycle.Count] });
        }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_undo.TryPop(out var previous))
        {
            await _host.Catalog.Tags.UpdateAsync(previous.Id, previous.Italian, previous.Reviewed, previous.Category, previous.Blacklisted, CancellationToken.None);
            if (Rows.FirstOrDefault(r => r.Id == previous.Id) is { } row)
            {
                row.Tag = previous;
                Selected = row;
                await LoadDetailAsync(row);
            }

            Message = Strings.Format("Vocabulary.Undone", previous.English);
        }
    }

    private async Task UpdateAsync(VocabularyRow row, Tag updated)
    {
        _undo.Push(row.Tag);
        await _host.Catalog.Tags.UpdateAsync(updated.Id, updated.Italian, updated.Reviewed, updated.Category, updated.Blacklisted, CancellationToken.None);
        row.Tag = updated;
        Summary = Strings.Format("Vocabulary.Summary", Rows.Count(r => r.Tag.Reviewed || r.Tag.Blacklisted), Rows.Count);
        await LoadDetailAsync(row);
    }

    /// <summary>"Regolatore globale" (board 5c): added to every RAM++ threshold, from the next analysis.</summary>
    partial void OnGlobalAdjustmentChanged(decimal value)
    {
        if ((double)value != _host.Settings.Tagging.ThresholdAdjustment)
        {
            _ = _host.UpdateSettingsAsync(s => s with { Tagging = s.Tagging with { ThresholdAdjustment = (double)value } }, CancellationToken.None);
            Message = Strings.Get("Vocabulary.AdjustmentNext");
        }
    }

    [RelayCommand]
    private void Escape() => EditText = null;

    // --- Personal tags (5d) ---

    [RelayCommand]
    private void SelectCustom(CustomTagRow? row) => SelectedCustom = row;

    [RelayCommand]
    private void NewCustom()
    {
        Tab = VocabularyTab.Custom;
        SelectedCustom = null;
        (CustomLabel, CustomDescription, CustomCategory, CustomThreshold, CustomMargin, TrialText) = (string.Empty, string.Empty, null, 0.27m, 0.03m, null);
    }

    private (string Key, Tag Tag, CustomTag Custom)? CustomDraft()
    {
        if (CustomLabel.Trim().Length == 0 || CustomDescription.Trim().Length == 0)
        {
            Message = Strings.Get("Vocabulary.Custom.Incomplete");
            return null;
        }

        var key = SelectedCustom?.Tag.Key ?? "custom:" + Slug(CustomLabel);
        var tag = new Tag(SelectedCustom?.Tag.Id ?? 0, key, CustomCategory, TagSource.ClipZeroShot, true, false) { Italian = CustomLabel.Trim() };
        return (key, tag, new CustomTag(tag.Id, CustomDescription.Trim(), (double)CustomThreshold, (double)CustomMargin));
    }

    /// <summary>Ctrl T: how many photos of the archive would get the tag (nothing saved).</summary>
    [RelayCommand]
    private async Task TrialAsync()
    {
        if (CustomDraft() is not { } draft || await _host.GetCustomTagScanAsync(CancellationToken.None) is not { } scan)
        {
            TrialText = _host.EmbeddingModelVersion is null ? Strings.Get("Vocabulary.Custom.NoClip") : TrialText;
            return;
        }

        var count = await Task.Run(() => scan.TrialAsync(draft.Tag, draft.Custom, CancellationToken.None));
        TrialText = Strings.Format("Vocabulary.Custom.Trial", count.ToString("N0", CultureInfo.CurrentCulture));
    }

    /// <summary>Ctrl S: saves the personal tag and assigns it again on every photo with an embedding.</summary>
    [RelayCommand]
    private async Task SaveCustomAsync()
    {
        if (CustomDraft() is not { } draft)
        {
            return;
        }

        await _host.Catalog.Tags.SaveCustomAsync(draft.Key, draft.Tag.Italian!, draft.Tag.Category, draft.Custom.ClipDescription, draft.Custom.Threshold, draft.Custom.Margin, CancellationToken.None);
        if (await _host.GetCustomTagScanAsync(CancellationToken.None) is { } scan)
        {
            var tagged = await Task.Run(() => scan.RunAsync(null, null, CancellationToken.None));
            Message = Strings.Format("Vocabulary.Custom.Saved", tagged.ToString("N0", CultureInfo.CurrentCulture));
        }
        else
        {
            Message = Strings.Get("Vocabulary.Custom.SavedNoClip");
        }

        await ReloadAsync(null);
        SelectedCustom = CustomTags.FirstOrDefault(c => c.Tag.Key == draft.Key);
    }

    private static string Slug(string text) =>
        new string(text.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    /// <summary>The event prefix, so that the vocabulary does not list events as tags to review.</summary>
    public static bool IsEvent(Tag tag) => tag.Key.StartsWith(TaggingStage.EventPrefix, StringComparison.Ordinal);
}
