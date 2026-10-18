// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace PhotoFlow.App.Services.Develop;

/// <summary>Settings of a photo as PhotoFlow Studio keeps them (Denoiser's settings plus a <c>studio</c> section).</summary>
public static class DevelopJson
{
    /// <summary>
    /// What the engine receives: with the noise reduction switched off (decision S8, key R) its strengths are zero, the
    /// rest of the settings stays as the user left it.
    /// </summary>
    public static JsonObject ForEngine(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = (JsonObject)settings.DeepClone();
        if (!Json.Bool(copy["studio"]?["noise_enabled"], true))
        {
            var denoise = Json.Section(copy, "denoise");
            denoise["luma_strength"] = 0.0;
            denoise["chroma_strength"] = 0.0;
            denoise["hot_pixel_threshold"] = 0.0;
        }

        copy.Remove("studio");
        return copy;
    }
}
