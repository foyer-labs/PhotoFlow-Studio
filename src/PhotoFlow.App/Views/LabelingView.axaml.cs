// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;

namespace PhotoFlow.App.Views;

public sealed partial class LabelingView : UserControl
{
    public LabelingView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
    }
}
