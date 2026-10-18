// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace PhotoFlow.App.Services.Develop;

/// <summary>A delivery recipe (decision S12): format, size, folder and name of the exported files.</summary>
public sealed record ExportRecipe(
    string Id,
    string LabelKey,
    string DescriptionKey,
    string Format,
    int BitDepth,
    int JpegQuality,
    int MaxSide,
    bool StripGps,
    string Folder,
    string NameTemplate,
    bool CameraRaw = true)
{
    /// <summary>The <c>output</c> section of the engine's settings.</summary>
    public JsonObject ToOutput(bool copyMetadata = true) => new()
    {
        ["format"] = Format,
        ["bit_depth"] = BitDepth,
        ["jpeg_quality"] = JpegQuality,
        ["max_side"] = MaxSide,
        ["strip_gps"] = StripGps,
        ["folder"] = Folder,
        ["name_template"] = NameTemplate,
        ["copy_metadata"] = copyMetadata,
        ["camera_raw"] = CameraRaw,
        ["overwrite"] = false,
    };
}

public static class ExportRecipes
{
    public const string Web = "web";
    public const string Client = "client";
    public const string Print = "print";
    public const string Lightroom = "lightroom";
    public const string Custom = "custom";

    /// <summary>Web, client, print, Lightroom (cleaned DNG next to the original), custom — in this order.</summary>
    public static IReadOnlyList<ExportRecipe> All { get; } =
    [
        new(Web, "Export.Recipe.Web", "Export.Recipe.Web.Note", "jpeg", 8, 85, 2048, true, "web", "{nome}"),
        new(Client, "Export.Recipe.Client", "Export.Recipe.Client.Note", "jpeg", 8, 92, 0, false, "cliente", "{nome}"),
        new(Print, "Export.Recipe.Print", "Export.Recipe.Print.Note", "tiff", 16, 95, 0, false, "stampa", "{nome}"),
        new(Lightroom, "Export.Recipe.Lightroom", "Export.Recipe.Lightroom.Note", "dng", 16, 95, 0, false, string.Empty, "{nome}-PF-NR"),
        new(Custom, "Export.Recipe.Custom", "Export.Recipe.Custom.Note", "tiff", 16, 95, 0, false, "esportate", "{nome}"),
    ];

    public static ExportRecipe Get(string id) => All.FirstOrDefault(r => r.Id == id) ?? All[0];
}
