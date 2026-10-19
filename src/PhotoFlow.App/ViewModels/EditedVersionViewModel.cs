// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;

namespace PhotoFlow.App.ViewModels;

/// <summary>A version saved by the external editor (M11), in the panel of the photo.</summary>
public sealed class EditedVersionViewModel(EditedVersion version)
{
    public EditedVersion Version { get; } = version;

    public string Name => Version.Name;

    public string Path => Version.Path;

    public string Detail => Strings.Format("Browse.Version.Detail", Version.ModifiedAt.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture), Formats.Bytes(Version.Length));
}
