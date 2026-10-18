// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;

namespace PhotoFlow.App.Views;

public sealed partial class RegenerationView : UserControl
{
    public RegenerationView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
    }
}
