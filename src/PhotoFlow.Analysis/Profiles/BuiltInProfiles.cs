// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoFlow.Core.Analysis;

namespace PhotoFlow.Analysis.Profiles;

/// <summary>Scene profiles shipped with the application (repository folder <c>profiles/</c>, embedded), spec §6.</summary>
public static class BuiltInProfiles
{
    public const int SchemaVersion = 1;
    private const string ResourcePrefix = "PhotoFlow.Profiles.";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>All built-in profiles, in the order shown to the user.</summary>
    public static IReadOnlyList<SceneProfile> All { get; } = Load();

    public static SceneProfile Get(string id) =>
        All.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException($"Unknown profile '{id}'.");

    /// <summary>Parses a profile document (also used for custom profiles).</summary>
    public static SceneProfile Parse(string json, bool isBuiltIn)
    {
        var document = JsonSerializer.Deserialize<ProfileDocument>(json, Options) ?? throw new InvalidDataException("Empty profile.");
        if (document.Schema != SchemaVersion)
        {
            throw new InvalidDataException($"Profile '{document.Id}': schema {document.Schema} not supported.");
        }

        var w = document.Weights;
        if (w.Sharpness + w.Eyes + w.Exposure + w.Subject + w.Aesthetic != 100)
        {
            throw new InvalidDataException($"Profile '{document.Id}': weights must sum to 100.");
        }

        if (document.SharpnessZones.Count == 0)
        {
            throw new InvalidDataException($"Profile '{document.Id}': at least one sharpness zone.");
        }

        var grouping = document.Grouping ?? GroupingParameters.Default;
        if (grouping.MaxBurstGapSeconds <= 0 || grouping.MinSimilarity is <= 0 or > 1 || grouping.TieMargin < 0)
        {
            throw new InvalidDataException($"Profile '{document.Id}': grouping parameters out of range.");
        }

        var t = document.Thresholds;
        if (t.BlurDiscard is < 0 or > 1 || t.BlurReview is < 0 or > 1 || t.BlurDiscard > t.BlurReview)
        {
            throw new InvalidDataException($"Profile '{document.Id}': the blur thresholds must be within 0..1, discard below review.");
        }

        if (string.IsNullOrWhiteSpace(document.Id) || string.IsNullOrWhiteSpace(document.Name) || document.Version < 1)
        {
            throw new InvalidDataException("Profile without id, name or version.");
        }

        return new SceneProfile(document.Id, document.Name, document.Version, isBuiltIn, document.CullingEnabled, document.SharpnessZones,
            document.Exposure, document.Faces, document.SubjectClasses, document.Ocr, document.AbsoluteSharpness, w, document.Thresholds)
        {
            Grouping = grouping,
            BasedOn = document.BasedOn,
        };
    }

    /// <summary>The JSON document of a profile (custom profiles, spec §6: versioned JSON files).</summary>
    public static string Serialize(SceneProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var document = new ProfileDocument(SchemaVersion, profile.Id, profile.Version, profile.Name, profile.CullingEnabled, profile.SharpnessZones,
            profile.Exposure, profile.Faces, profile.SubjectClasses, profile.Ocr, profile.AbsoluteSharpness, profile.Weights, profile.Thresholds,
            profile.Grouping, profile.BasedOn);
        return JsonSerializer.Serialize(document, Options);
    }

    private static IReadOnlyList<SceneProfile> Load()
    {
        var order = new[] { "events", "portrait", "motorsport", "animals", "landscape", "night", "archive" };
        var assembly = typeof(BuiltInProfiles).Assembly;
        var profiles = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return Parse(reader.ReadToEnd(), isBuiltIn: true);
            })
            .ToList();
        return [.. profiles.OrderBy(p => Array.IndexOf(order, p.Id) is var i && i < 0 ? int.MaxValue : i).ThenBy(p => p.Id, StringComparer.Ordinal)];
    }

    private sealed record ProfileDocument(
        int Schema,
        string Id,
        int Version,
        string Name,
        bool CullingEnabled,
        IReadOnlyList<SharpnessZoneKind> SharpnessZones,
        ExposureMode Exposure,
        FaceMode Faces,
        IReadOnlyList<string> SubjectClasses,
        bool Ocr,
        bool AbsoluteSharpness,
        ProfileWeights Weights,
        CullingThresholds Thresholds,
        GroupingParameters? Grouping = null,
        string? BasedOn = null);
}
