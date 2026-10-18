// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

/// <summary>
/// The photo large with its faces (spec §7, P21): a click on a face says who it is, a box drawn adds a face, a double
/// click closes it as one on a thumbnail opened it (not while drawing). The page gives the image and the close command.
/// </summary>
public sealed partial class LargePhotoView : UserControl
{
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<LargePhotoView, IImage?>(nameof(Source));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty = AvaloniaProperty.Register<LargePhotoView, ICommand?>(nameof(CloseCommand));

    /// <summary>The key that adds a face on this page (N in Consultazione, Maiusc N in Revisione).</summary>
    public static readonly StyledProperty<string> AddFaceKeyProperty = AvaloniaProperty.Register<LargePhotoView, string>(nameof(AddFaceKey), "N");

    public static readonly StyledProperty<string?> FacesHintProperty = AvaloniaProperty.Register<LargePhotoView, string?>(nameof(FacesHint));

    private PhotoEditorViewModel? _editor;

    public LargePhotoView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_editor is not null)
            {
                _editor.PersonChoiceRequested -= OnPersonChoiceRequested;
            }

            _editor = DataContext as PhotoEditorViewModel;
            if (_editor is not null)
            {
                _editor.PersonChoiceRequested += OnPersonChoiceRequested;
            }
        };
    }

    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public ICommand? CloseCommand { get => GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    public string AddFaceKey { get => GetValue(AddFaceKeyProperty); set => SetValue(AddFaceKeyProperty, value); }

    public string? FacesHint { get => GetValue(FacesHintProperty); set => SetValue(FacesHintProperty, value); }

    private void OnPersonChoiceRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (IsEffectivelyVisible)
        {
            FacePersonBox.Focus();
            FacePersonBox.IsDropDownOpen = true;
        }
    });

    /// <summary>A face clicked on the photo: who is it?</summary>
    private async void OnFaceClicked(object? sender, FaceBoxViewModel face)
    {
        if (_editor is not null)
        {
            await _editor.IdentifyFaceAsync(face);
        }
    }

    /// <summary>A box drawn on the photo: the face to add, then its person.</summary>
    private void OnFaceDrawn(object? sender, Core.Analysis.BoundingBox box) => _editor?.FaceDrawnOnPhoto(box);

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2 && _editor is { IsDrawingFace: false } && CloseCommand is { } close && close.CanExecute(null))
        {
            e.Handled = true;
            close.Execute(null);
            this.FindAncestorOfType<UserControl>()?.Focus();
        }
    }
}
