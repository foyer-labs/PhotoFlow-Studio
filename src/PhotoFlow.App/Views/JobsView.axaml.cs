// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;

namespace PhotoFlow.App.Views;

public sealed partial class JobsView : UserControl
{
    public JobsView()
    {
        InitializeComponent();
        PageKeys.Attach(this);
    }
}
