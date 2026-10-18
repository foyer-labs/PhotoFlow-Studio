// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoFlow.App.ViewModels;

namespace PhotoFlow.App.Views;

/// <summary>Caption and tags of a photo; T and D put the focus in their field, which gives the keys back to the page when it closes.</summary>
public sealed partial class PhotoEditorPanel : UserControl
{
    private PhotoEditorViewModel? _editor;

    public PhotoEditorPanel()
    {
        InitializeComponent();
        foreach (var field in new Control[] { CaptionBox, TagBox })
        {
            field.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && e.NewValue is false)
                {
                    this.FindAncestorOfType<UserControl>()?.Focus();
                }
            };
        }

        DataContextChanged += (_, _) =>
        {
            if (_editor is not null)
            {
                _editor.FocusTagRequested -= OnFocusTag;
                _editor.FocusCaptionRequested -= OnFocusCaption;
            }

            _editor = DataContext as PhotoEditorViewModel;
            if (_editor is not null)
            {
                _editor.FocusTagRequested += OnFocusTag;
                _editor.FocusCaptionRequested += OnFocusCaption;
            }
        };
    }

    private void OnFocusTag(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => TagBox.Focus());

    private void OnFocusCaption(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        CaptionBox.Focus();
        CaptionBox.CaretIndex = CaptionBox.Text?.Length ?? 0;
    });
}
