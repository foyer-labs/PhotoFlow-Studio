// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;

namespace PhotoFlow.App.Services.Develop;

/// <summary>
/// What an exported photo carries besides the metadata of its original (M2): author, copyright and contacts, the
/// keywords and people of the catalog (the same that go in its XMP), the caption and the stars. The engine writes them
/// with ExifTool in XMP and, where the format has them, in EXIF and IPTC.
/// </summary>
public static class DeliveryMetadata
{
    public static async Task<JsonObject> ForAssetAsync(ICatalog catalog, long assetId, RightsInfo? rights, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var result = new JsonObject();
        var asset = await catalog.Assets.GetAsync(assetId, ct).ConfigureAwait(false);
        if (rights is { IsEmpty: false })
        {
            var r = rights.ForYear(asset?.CapturedAt?.Year);
            Add(result, "creator", r.Creator);
            Add(result, "rights", r.Copyright);
            Add(result, "email", r.Email);
            Add(result, "url", r.Website);
            Add(result, "usage_terms", r.UsageTerms);
        }

        // Tags, faces and captions live on the analyzed photo: a RAW takes them from its JPEG twin.
        var twin = await catalog.Assets.GetTwinAsync(assetId, ct).ConfigureAwait(false);
        var tags = await catalog.Tags.GetForAssetAsync(assetId, ct).ConfigureAwait(false);
        if (tags.Count == 0 && twin is { } t1)
        {
            tags = await catalog.Tags.GetForAssetAsync(t1, ct).ConfigureAwait(false);
        }

        var faces = await catalog.Analysis.GetFacesAsync(assetId, ct).ConfigureAwait(false);
        if (faces.Count == 0 && twin is { } t2)
        {
            faces = await catalog.Analysis.GetFacesAsync(t2, ct).ConfigureAwait(false);
        }

        var persons = (await catalog.People.ListPersonsAsync(ct).ConfigureAwait(false)).Select(p => p.Person).ToDictionary(p => p.Id);
        var people = faces
            .Where(f => f.PersonId is { } id && persons.TryGetValue(id, out var person) && person.Name is not null && !person.Ignored)
            .Select(f => persons[f.PersonId!.Value].Name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var changes = XmpPublisher.WithTags(null, tags);
        var keywords = (changes?.FlatKeywords ?? []).Concat(people).Distinct(StringComparer.Ordinal).ToList();
        var hierarchical = (changes?.HierarchicalKeywords ?? []).Concat(people.Select(p => $"{XmpPublisher.PeopleKeywordRoot}|{p}")).Distinct(StringComparer.Ordinal).ToList();
        if (keywords.Count > 0)
        {
            result["keywords"] = new JsonArray([.. keywords.Select(k => (JsonNode)k)]);
        }

        if (hierarchical.Count > 0)
        {
            result["hierarchical"] = new JsonArray([.. hierarchical.Select(k => (JsonNode)k)]);
        }

        if (people.Count > 0)
        {
            result["people"] = new JsonArray([.. people.Select(p => (JsonNode)p)]);
        }

        var captions = await catalog.Captions.GetAllAsync(assetId, ct).ConfigureAwait(false);
        if (captions.Count == 0 && twin is { } t3)
        {
            captions = await catalog.Captions.GetAllAsync(t3, ct).ConfigureAwait(false);
        }

        Add(result, "description", captions.FirstOrDefault()?.Text);
        if ((await catalog.Decisions.GetAsync(assetId, ct).ConfigureAwait(false))?.Rating is { } stars and >= 1 and <= 5)
        {
            result["rating"] = stars;
        }

        return result;
    }

    private static void Add(JsonObject target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value.Trim();
        }
    }
}
