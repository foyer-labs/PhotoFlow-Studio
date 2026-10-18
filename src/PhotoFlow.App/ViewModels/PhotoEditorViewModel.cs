// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Faces.Recognition;

namespace PhotoFlow.App.ViewModels;

/// <summary>
/// What the user fixes on one photo (spec §7-§8, P21): its tags (T adds, ✕ removes), its caption (D) and its faces (the
/// boxes, "who is it?", a face added by hand). One component for Consultazione and Revisione, so both behave the same
/// way. Every change is the user's (I5); the XMP of a photo already confirmed is written again at once, the others are
/// written at the confirmation of their review.
/// </summary>
public sealed partial class PhotoEditorViewModel(PhotoFlowHost host) : ObservableObject
{
    private IReadOnlyList<Face> _faces = [];
    private IReadOnlyDictionary<long, Person> _persons = new Dictionary<long, Person>();
    private Caption? _current;
    private Size _imageSize;
    private int _version;

    /// <summary>The photo being edited; null when nothing is selected.</summary>
    public long? AssetId { get; private set; }

    /// <summary>A tag, the caption or a face changed (the page refreshes its counts and lists).</summary>
    public event EventHandler? Changed;

    /// <summary>A face is about to be drawn: the page shows the photo large.</summary>
    public event EventHandler? LargePhotoRequested;

    public event EventHandler? FocusTagRequested;

    public event EventHandler? FocusCaptionRequested;

    /// <summary>A face was drawn or clicked: the view opens the list of people.</summary>
    public event EventHandler? PersonChoiceRequested;

    public ObservableCollection<DetailTagViewModel> Tags { get; } = [];

    public ObservableCollection<string> TagSuggestions { get; } = [];

    /// <summary>The faces in the pixels of the shown image (<see cref="ImageSize"/>).</summary>
    public ObservableCollection<FaceBoxViewModel> FaceBoxes { get; } = [];

    public ObservableCollection<PersonChoice> FacePeople { get; } = [];

    /// <summary>"Luca, Ripa · 1 non riconosciuto".</summary>
    [ObservableProperty]
    private string _people = string.Empty;

    /// <summary>The tag being typed (T), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTagging))]
    private string? _tagText;

    public bool IsTagging => TagText is not null;

    /// <summary>Caption of the photo (spec §8), "—" when there is none.</summary>
    [ObservableProperty]
    private string _captionLine = string.Empty;

    /// <summary>The caption in the other languages (Florence-2 in English under the Italian one, U5), or empty.</summary>
    [ObservableProperty]
    private string _otherCaptions = string.Empty;

    /// <summary>Where the caption comes from: the model, or the user.</summary>
    [ObservableProperty]
    private string _captionSource = string.Empty;

    /// <summary>The caption being edited (D), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingCaption))]
    private string? _captionText;

    public bool IsEditingCaption => CaptionText is not null;

    /// <summary>The faces are boxed and named on the photo (V).</summary>
    [ObservableProperty]
    private bool _showFaces = true;

    /// <summary>A box is being drawn on the photo to add a face, or a clicked face waits for its person.</summary>
    [ObservableProperty]
    private bool _isDrawingFace;

    /// <summary>The face drawn, waiting for its person.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosingFacePerson))]
    private BoundingBox? _drawnFace;

    /// <summary>A face of the photo clicked to say who it is ("?" or another person).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosingFacePerson), nameof(CanSaveFace), nameof(FacePrompt))]
    private FaceBoxViewModel? _identifyingFace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewFacePerson), nameof(CanSaveFace))]
    private PersonChoice? _facePerson;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveFace))]
    private string? _newFacePersonName;

    /// <summary>The outcome of the last change ("volto aggiunto", XMP not written…), or null.</summary>
    [ObservableProperty]
    private string? _message;

    public bool IsChoosingFacePerson => DrawnFace is not null || IdentifyingFace is not null;

    public bool IsNewFacePerson => FacePerson is { Id: null };

    public bool CanSaveFace => (DrawnFace is not null || IdentifyingFace is not null)
        && (FacePerson is { Id: not null } || (IsNewFacePerson && !string.IsNullOrWhiteSpace(NewFacePersonName)));

    /// <summary>What the picker asks: draw a face, or who the clicked face is.</summary>
    public string FacePrompt => IdentifyingFace is { } face
        ? Strings.Format("Browse.IdentifyFace", face.Name ?? "?")
        : Strings.Get("Browse.DrawFace");

    /// <summary>Size in pixels of the image the page shows; the face boxes follow it.</summary>
    public Size ImageSize
    {
        get => _imageSize;
        set
        {
            if (_imageSize != value)
            {
                _imageSize = value;
                OnPropertyChanged();
                ShowBoxes();
            }
        }
    }

    /// <summary>Loads tags, caption and faces of a photo (null: nothing selected). A newer call wins over an older one.</summary>
    public async Task LoadAsync(long? assetId)
    {
        var version = ++_version;
        if (assetId != AssetId)
        {
            AssetId = assetId;
            TagText = null;
            CaptionText = null;
            Message = null;
            CancelAddFace();
            Tags.Clear();
            FaceBoxes.Clear();
            _faces = [];
            People = string.Empty;
            CaptionLine = string.Empty;
            OtherCaptions = string.Empty;
            CaptionSource = string.Empty;
            _current = null;
        }

        if (assetId is not { } id)
        {
            return;
        }

        var catalog = host.Catalog;
        var tags = await catalog.Tags.GetForAssetAsync(id, CancellationToken.None);
        var captions = await catalog.Captions.GetAllAsync(id, CancellationToken.None);
        if (captions.Count == 0 && await catalog.Assets.GetTwinAsync(id, CancellationToken.None) is { } twin)
        {
            captions = await catalog.Captions.GetAllAsync(twin, CancellationToken.None);
        }

        var persons = (await catalog.People.ListPersonsAsync(CancellationToken.None)).ToDictionary(p => p.Person.Id, p => p.Person);
        var faces = await catalog.Analysis.GetFacesAsync(id, CancellationToken.None);
        if (version != _version)
        {
            return;
        }

        Tags.Clear();
        foreach (var tag in tags.Where(t => t.Assignment.State == AssetTagState.Active && !t.Tag.Blacklisted && !t.Tag.Key.StartsWith(Tagging.Vocabulary.TaggingStage.EventPrefix, StringComparison.Ordinal)))
        {
            Tags.Add(new DetailTagViewModel(tag.Tag.Id, tag.Tag.Reviewed && tag.Tag.Italian is { } it ? it : tag.Tag.English, tag.Assignment.Source == TagSource.Manual));
        }

        _current = captions.FirstOrDefault();
        OtherCaptions = string.Join("\n", captions.Skip(1).Where(c => c.Text.Length > 0).Select(c => $"{c.Language.ToUpperInvariant()} · {c.Text}"));
        CaptionLine = _current is { Text.Length: > 0 } caption ? caption.Text : "—";
        CaptionSource = _current switch
        {
            null => string.Empty,
            { Origin: CaptionOrigin.Manual } => Strings.Get("Browse.Caption.Yours"),
            { ModelVersion: { } model } => Strings.Format("Browse.Caption.Automatic", model.Split('@')[0]),
            _ => string.Empty,
        };

        _faces = faces;
        _persons = persons;
        People = FaceBoxViewModel.People(faces, persons);
        ShowBoxes();
    }

    /// <summary>The labels offered while typing a tag: those in use, in the language shown.</summary>
    public async Task LoadTagSuggestionsAsync()
    {
        var labels = new List<string>();
        foreach (var tag in await host.Catalog.Tags.ListAsync(CancellationToken.None))
        {
            if (tag.PhotoCount > 0 && !tag.Blacklisted && !tag.Key.StartsWith(Tagging.Vocabulary.TaggingStage.EventPrefix, StringComparison.Ordinal))
            {
                labels.Add(tag.Italian is { } it && tag.Reviewed ? it : tag.English);
            }
        }

        TagSuggestions.Clear();
        foreach (var label in labels.Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            TagSuggestions.Add(label);
        }
    }

    private void ShowBoxes()
    {
        FaceBoxes.Clear();
        if (_imageSize.Width > 0 && _imageSize.Height > 0)
        {
            foreach (var box in FaceBoxViewModel.From(_faces, _persons, _imageSize.Width, _imageSize.Height))
            {
                FaceBoxes.Add(box);
            }
        }
    }

    // --- Tags ---

    [RelayCommand]
    private void StartTag()
    {
        if (AssetId is not null)
        {
            TagText = string.Empty;
            FocusTagRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Enter in the tag field: a manual tag (an existing label is reused, otherwise a new manual tag).</summary>
    [RelayCommand]
    private async Task ApplyTagAsync()
    {
        if (AssetId is not { } id || string.IsNullOrWhiteSpace(TagText))
        {
            TagText = null;
            return;
        }

        var text = TagText.Trim();
        TagText = null;
        var catalog = host.Catalog;
        var existing = (await catalog.Tags.ListAsync(CancellationToken.None))
            .FirstOrDefault(t => string.Equals(t.Italian, text, StringComparison.CurrentCultureIgnoreCase) || string.Equals(t.English, text, StringComparison.CurrentCultureIgnoreCase));
        await catalog.Tags.AddManualAsync(id, existing?.Key ?? text, CancellationToken.None);
        if (existing is null && await catalog.Tags.FindAsync(text, CancellationToken.None) is { } created)
        {
            await catalog.Tags.UpdateAsync(created.Id, text, reviewed: true, null, blacklisted: false, CancellationToken.None);
        }

        await AfterChangeAsync(id, null);
    }

    /// <summary>✕ on a tag: removed from the photo and suppressed, so no analysis adds it again (I5).</summary>
    [RelayCommand]
    private async Task RemoveTagAsync(DetailTagViewModel? tag)
    {
        if (AssetId is { } id && tag is not null)
        {
            await host.Catalog.Tags.SuppressAsync(id, tag.TagId, CancellationToken.None);
            await AfterChangeAsync(id, null);
        }
    }

    // --- Caption ---

    /// <summary>D: edit the caption of the photo.</summary>
    [RelayCommand]
    private void StartCaption()
    {
        if (AssetId is not null)
        {
            CaptionText = _current?.Text ?? string.Empty;
            FocusCaptionRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Enter in the caption field: the user's caption, never replaced by an analysis (I5); empty clears it for good.</summary>
    [RelayCommand]
    private async Task SaveCaptionAsync()
    {
        if (AssetId is not { } id || CaptionText is not { } text)
        {
            return;
        }

        CaptionText = null;
        var language = _current?.Language ?? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en");
        await host.Catalog.Captions.SetManualAsync(_current?.AssetId ?? id, language, text.Trim(), CancellationToken.None);
        await AfterChangeAsync(id, null);
    }

    [RelayCommand]
    private void CancelCaption() => CaptionText = null;

    // --- Faces ---

    [RelayCommand]
    private void ToggleFaces() => ShowFaces = !ShowFaces;

    /// <summary>Add a face: the page shows the photo large and a box can be drawn on the face.</summary>
    [RelayCommand]
    private async Task StartAddFaceAsync()
    {
        if (AssetId is null)
        {
            return;
        }

        LargePhotoRequested?.Invoke(this, EventArgs.Empty);
        ShowFaces = true;
        await LoadFacePeopleAsync();
        IsDrawingFace = true;
    }

    /// <summary>A click on a face of the photo: who is it? (a "?" face, or one to correct).</summary>
    public async Task IdentifyFaceAsync(FaceBoxViewModel face)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (AssetId is null || face.FaceId == 0)
        {
            return;
        }

        await LoadFacePeopleAsync();
        FacePerson = face.PersonId is { } current ? FacePeople.FirstOrDefault(p => p.Id == current) : null;
        IdentifyingFace = face;
        IsDrawingFace = true; // the same panel; no box is drawn while a face is being identified
        PersonChoiceRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The box drawn on the photo: now the person.</summary>
    public void FaceDrawnOnPhoto(BoundingBox box)
    {
        if (IsDrawingFace && IdentifyingFace is null)
        {
            DrawnFace = box;
            PersonChoiceRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoadFacePeopleAsync()
    {
        DrawnFace = null;
        IdentifyingFace = null;
        FacePeople.Clear();
        FacePeople.Add(new PersonChoice(null, Strings.Get("People.NewPerson")));
        foreach (var person in (await host.Catalog.People.ListPersonsAsync(CancellationToken.None))
                     .Where(p => p.Person.Name is not null && !p.Person.Ignored).OrderBy(p => p.Person.Name, StringComparer.CurrentCulture))
        {
            FacePeople.Add(new PersonChoice(person.Person.Id, Strings.Format("People.ChoiceLabel", person.Person.Name!, person.FaceCount)));
        }

        FacePerson = null;
        NewFacePersonName = null;
    }

    /// <summary>Saves who the face is, or the face drawn, for the chosen person (a new one with the name typed); the user's (I5).</summary>
    [RelayCommand]
    private async Task SaveFaceAsync()
    {
        if (!CanSaveFace || AssetId is not { } id)
        {
            return;
        }

        var catalog = host.Catalog;
        var person = FacePerson!.Id ?? await catalog.People.CreatePersonAsync(NewFacePersonName!.Trim(), CancellationToken.None);
        string message;
        if (IdentifyingFace is { } face)
        {
            // A manual assignment; both people get their samples again, so the fingerprint of this face helps
            // recognizing the person in the other photos.
            await catalog.People.AssignManualAsync([face.FaceId], person, CancellationToken.None);
            await PeopleEngine.RefreshPrototypesAsync(catalog, person, CancellationToken.None);
            if (face.PersonId is { } previous && previous != person)
            {
                await PeopleEngine.RefreshPrototypesAsync(catalog, previous, CancellationToken.None);
            }

            message = Strings.Get("Browse.FaceIdentified");
        }
        else
        {
            await catalog.Analysis.AddManualFaceAsync(id, DrawnFace!.Value, person, CancellationToken.None);
            message = Strings.Get("Browse.FaceAdded");
        }

        CancelAddFace();
        await AfterChangeAsync(id, message);
    }

    [RelayCommand]
    private void CancelAddFace()
    {
        IsDrawingFace = false;
        DrawnFace = null;
        IdentifyingFace = null;
    }

    /// <summary>Esc: closes the field or the face being added; false when there was nothing to close.</summary>
    public bool Escape()
    {
        if (TagText is not null)
        {
            TagText = null;
        }
        else if (CaptionText is not null)
        {
            CaptionText = null;
        }
        else if (IsDrawingFace)
        {
            CancelAddFace();
        }
        else
        {
            return false;
        }

        return true;
    }

    private async Task AfterChangeAsync(long assetId, string? message)
    {
        var written = await RewriteXmpAsync(host, assetId);
        await LoadAsync(assetId);
        Message = written ? message : Strings.Get("Browse.XmpFailed");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Writes the XMP of a photo again after a change by the user, when it was already confirmed; before, the
    /// confirmation of its review writes it. False when the writing failed (the change stays in the catalog).
    /// </summary>
    public static async Task<bool> RewriteXmpAsync(PhotoFlowHost host, long assetId)
    {
        ArgumentNullException.ThrowIfNull(host);
        if ((await host.Catalog.XmpSync.GetAsync(assetId, CancellationToken.None))?.LastWrittenAt is null)
        {
            return true; // never confirmed: the XMP is written at the confirmation of its review
        }

        await host.Catalog.XmpSync.MarkPendingAsync(assetId, null, CancellationToken.None);
        var summary = await Task.Run(() => host.CreatePublisher().WriteAsync([assetId], host.Settings.PrudentMode, null, CancellationToken.None));
        return summary.Failed.Count == 0;
    }
}
