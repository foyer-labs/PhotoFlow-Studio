// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Search;

namespace PhotoFlow.App.ViewModels;

/// <summary>
/// The tags chosen in the facets (spec §12bis): groups of tags, OR inside a group and AND between groups. A tag added
/// "in AND" opens a new group, "in OR" joins the last one; a group left empty disappears.
/// </summary>
public sealed class TagSelection
{
    private readonly List<List<long>> _groups = [];

    public IReadOnlyList<IReadOnlyList<long>> Groups => _groups;

    public bool Contains(long tagId) => _groups.Any(g => g.Contains(tagId));

    public void Add(long tagId, bool orWithLast)
    {
        if (Contains(tagId))
        {
            return;
        }

        if (orWithLast && _groups.Count > 0)
        {
            _groups[^1].Add(tagId);
        }
        else
        {
            _groups.Add([tagId]);
        }
    }

    public void Remove(long tagId)
    {
        foreach (var group in _groups)
        {
            group.Remove(tagId);
        }

        _groups.RemoveAll(g => g.Count == 0);
    }

    public void Clear() => _groups.Clear();

    /// <summary>The groups but the last (the base of the "+N" of an OR).</summary>
    public IReadOnlyList<IReadOnlyList<long>> WithoutLast => _groups.Take(Math.Max(0, _groups.Count - 1)).ToList();
}

/// <summary>A value of a facet with its count.</summary>
public sealed partial class FacetItemViewModel(long id, string label, string? detail) : ObservableObject
{
    public long Id { get; } = id;

    public string Label { get; } = label;

    public string? Detail { get; } = detail;

    [ObservableProperty]
    private string _countText = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>A chosen tag in its group; <see cref="IsFirst"/> hides the "o" before the first one.</summary>
public sealed record FacetChip(long Id, string Label, bool IsFirst);

/// <summary>A group of chosen tags (OR inside); <see cref="IsFirst"/> hides the "e" before the first group.</summary>
public sealed record FacetGroup(IReadOnlyList<FacetChip> Chips, bool IsFirst);

/// <summary>
/// The facets panel of Consultazione (spec §12bis, 13 Oct 2026): tags in groups AND/OR, events (OR), people (AND),
/// each with the number of photos, recomputed on the whole result at every change. The counts tell what a click
/// does: in AND the photos left, in OR the photos added ("+N").
/// </summary>
public sealed partial class BrowseFacetsViewModel(PhotoFlowHost host, Func<Task> search) : ObservableObject
{
    private readonly TagSelection _tags = new();
    private readonly HashSet<long> _events = [];
    private readonly HashSet<long> _persons = [];
    private Dictionary<long, (string Label, string? Category, bool IsEvent)> _tagInfo = [];
    private Dictionary<long, string> _personNames = [];
    private List<FacetItemViewModel> _allTags = [];
    private int _version;

    /// <summary>The panel is shown (F).</summary>
    [ObservableProperty]
    private bool _isOpen = true;

    /// <summary>A plain click adds in OR (to the last group) instead of AND (a new group); Ctrl+click does the other.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAndMode))]
    private bool _orMode;

    public bool IsAndMode
    {
        get => !OrMode;
        set => OrMode = !value;
    }

    /// <summary>At most this many tags in the list; the filter finds the others.</summary>
    public const int MaxShown = 300;

    /// <summary>"Altri N tag: usa il filtro" when the list is cut.</summary>
    [ObservableProperty]
    private string? _moreTagsText;

    /// <summary>Text filtering the list of tags.</summary>
    [ObservableProperty]
    private string? _filter;

    [ObservableProperty]
    private bool _isCounting;

    public ObservableCollection<FacetItemViewModel> Tags { get; } = [];

    public ObservableCollection<FacetItemViewModel> Events { get; } = [];

    public ObservableCollection<FacetItemViewModel> Persons { get; } = [];

    public ObservableCollection<FacetGroup> Groups { get; } = [];

    public bool HasSelection => _tags.Groups.Count > 0 || _events.Count > 0 || _persons.Count > 0;

    public bool HasEvents => Events.Count > 0;

    public bool HasPersons => Persons.Count > 0;

    partial void OnFilterChanged(string? value) => ShowTags();

    partial void OnOrModeChanged(bool value) => _ = search();

    /// <summary>The labels of tags and people (Italian when available, U5).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        _tagInfo = (await host.Catalog.Tags.ListAsync(ct)).Where(t => !t.Blacklisted).ToDictionary(t => t.Id, t =>
        {
            var isEvent = t.Key.StartsWith(Tagging.Vocabulary.TaggingStage.EventPrefix, StringComparison.Ordinal);
            return (t.Italian ?? t.English, t.Category, isEvent);
        });
        _personNames = (await host.Catalog.People.ListPersonsAsync(ct)).Where(p => p.Person.Name is not null && !p.Person.Ignored)
            .ToDictionary(p => p.Person.Id, p => p.Person.Name!);
    }

    /// <summary>The selection added to the query: people in AND, the events as one OR group, then the tag groups.</summary>
    public SearchQuery Apply(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<IReadOnlyList<long>> groups = [];
        if (_events.Count > 0)
        {
            groups.Add([.. _events]);
        }

        groups.AddRange(_tags.Groups);
        return query with
        {
            PersonIds = [.. query.PersonIds, .. _persons.Except(query.PersonIds)],
            TagGroups = [.. query.TagGroups ?? [], .. groups],
        };
    }

    /// <summary>Clicks on a tag: in the mode of the switch, or the other one with Ctrl.</summary>
    public async Task ClickTagAsync(FacetItemViewModel item, bool other)
    {
        ArgumentNullException.ThrowIfNull(item);
        _tags.Add(item.Id, OrMode != other);
        await search();
    }

    [RelayCommand]
    private async Task RemoveTagAsync(FacetChip? chip)
    {
        if (chip is not null)
        {
            _tags.Remove(chip.Id);
            await search();
        }
    }

    [RelayCommand]
    private async Task ToggleEventAsync(FacetItemViewModel? item)
    {
        if (item is not null && !_events.Remove(item.Id))
        {
            _events.Add(item.Id);
        }

        await search();
    }

    [RelayCommand]
    private async Task TogglePersonAsync(FacetItemViewModel? item)
    {
        if (item is not null && !_persons.Remove(item.Id))
        {
            _persons.Add(item.Id);
        }

        await search();
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        Reset();
        await search();
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    /// <summary>Forgets the selection (the "Azzera" of the search).</summary>
    public void Reset()
    {
        _tags.Clear();
        _events.Clear();
        _persons.Clear();
        Filter = null;
    }

    /// <summary>
    /// Counts on the result of <paramref name="query"/> (the full query, selection included). A newer refresh
    /// supersedes an older one still running.
    /// </summary>
    public async Task RefreshAsync(SearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var version = Interlocked.Increment(ref _version);
        BuildGroups();
        IsCounting = true;
        try
        {
            var search = host.Catalog.Search;
            var baseGroups = query.TagGroups?.Take((query.TagGroups?.Count ?? 0) - _tags.Groups.Count - (_events.Count > 0 ? 1 : 0)).ToList() ?? [];
            var eventsGroup = _events.Count > 0 ? new List<IReadOnlyList<long>> { _events.ToList() } : [];
            var withoutEvents = query with { TagGroups = [.. baseGroups, .. _tags.Groups] };
            var orMode = OrMode && _tags.Groups.Count > 0;
            var (counts, added, events) = await Task.Run(async () =>
            {
                var counts = await search.CountFacetsAsync(query, ct);
                return (counts,
                    orMode ? await search.CountTagsAsync(query with { TagGroups = [.. baseGroups, .. eventsGroup, .. _tags.WithoutLast] }, query, ct) : null,
                    _events.Count > 0 ? await search.CountTagsAsync(withoutEvents, null, ct) : counts.Tags);
            }, ct);
            var (inResult, persons) = (counts.Tags, counts.Persons);
            if (version != _version)
            {
                return;
            }

            // AND: the tags of the result, with the photos they keep. OR: also the tags that add photos, with "+N";
            // a tag of the result that adds nothing stays listed ("+0") so that Ctrl+click can still narrow with it.
            int Shown(long id) => added is null ? inResult.GetValueOrDefault(id) : added.GetValueOrDefault(id);
            _allTags = [.. inResult.Keys.Union(added?.Keys ?? [])
                .Where(id => _tagInfo.TryGetValue(id, out var info) && !info.IsEvent && !_tags.Contains(id))
                .OrderByDescending(Shown).ThenByDescending(id => inResult.GetValueOrDefault(id)).ThenBy(id => _tagInfo[id].Label, StringComparer.CurrentCultureIgnoreCase)
                .Select(id => new FacetItemViewModel(id, _tagInfo[id].Label, _tagInfo[id].Category) { CountText = Count(Shown(id), orMode) })];
            ShowTags();

            Events.Clear();
            foreach (var id in _tagInfo.Where(t => t.Value.IsEvent).Select(t => t.Key)
                         .Where(id => events.GetValueOrDefault(id) > 0 || _events.Contains(id))
                         .OrderByDescending(id => _events.Contains(id)).ThenByDescending(id => events.GetValueOrDefault(id)))
            {
                Events.Add(new FacetItemViewModel(id, _tagInfo[id].Label, null) { CountText = Count(events.GetValueOrDefault(id), false), IsSelected = _events.Contains(id) });
            }

            Persons.Clear();
            foreach (var (id, name) in _personNames.Where(p => persons.GetValueOrDefault(p.Key) > 0 || _persons.Contains(p.Key))
                         .OrderByDescending(p => _persons.Contains(p.Key)).ThenByDescending(p => persons.GetValueOrDefault(p.Key)).ThenBy(p => p.Value, StringComparer.CurrentCulture))
            {
                Persons.Add(new FacetItemViewModel(id, name, null) { CountText = Count(persons.GetValueOrDefault(id), false), IsSelected = _persons.Contains(id) });
            }

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasEvents));
            OnPropertyChanged(nameof(HasPersons));
        }
        finally
        {
            if (version == _version)
            {
                IsCounting = false;
            }
        }
    }

    private static string Count(int value, bool added) =>
        (added ? "+" : string.Empty) + value.ToString("N0", CultureInfo.CurrentCulture);

    private void ShowTags()
    {
        Tags.Clear();
        var filter = Filter?.Trim();
        var matching = _allTags.Where(t => string.IsNullOrEmpty(filter)
            || t.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
            || (t.Detail?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ?? false)).ToList();
        foreach (var item in matching.Take(MaxShown))
        {
            Tags.Add(item);
        }

        MoreTagsText = matching.Count > MaxShown ? Strings.Format("Browse.Facets.More", matching.Count - MaxShown) : null;
    }

    private void BuildGroups()
    {
        Groups.Clear();
        foreach (var (group, g) in _tags.Groups.Select((group, g) => (group, g)))
        {
            Groups.Add(new FacetGroup([.. group.Select((id, i) => new FacetChip(id, _tagInfo.TryGetValue(id, out var info) ? info.Label : Strings.Get("Browse.Facets.Unknown"), i == 0))], g == 0));
        }
    }
}
