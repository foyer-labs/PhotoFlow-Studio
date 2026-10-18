// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Nodes;

namespace PhotoFlow.App.Services.Develop;

/// <summary>Tolerant reads of the engine's JSON (numbers may come as integers or decimals, keys may be missing).</summary>
public static class Json
{
    /// <summary>
    /// Any number: parsed from the engine (a JSON element) or created here (an int, a double…). TryGetValue alone
    /// fails across types (an int value is not a long), so the number is read from its JSON text.
    /// </summary>
    public static double? NullableDouble(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    public static double Double(JsonNode? node, double fallback = 0) => NullableDouble(node) ?? fallback;

    public static long Long(JsonNode? node, long fallback = 0) => NullableDouble(node) is { } d ? (long)Math.Round(d) : fallback;

    public static int Int(JsonNode? node, int fallback = 0) => (int)Long(node, fallback);

    public static bool Bool(JsonNode? node, bool fallback = false) => node is JsonValue v
        ? v.GetValueKind() switch { System.Text.Json.JsonValueKind.True => true, System.Text.Json.JsonValueKind.False => false, _ => fallback }
        : fallback;

    public static string? NullableString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;

    public static string String(JsonNode? node, string fallback = "") => NullableString(node) ?? fallback;

    /// <summary>The object at <paramref name="section"/>.<paramref name="key"/> of a settings object, created if missing.</summary>
    public static JsonObject Section(JsonObject settings, string section)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings[section] is not JsonObject obj)
        {
            obj = [];
            settings[section] = obj;
        }

        return obj;
    }

    public static string Format(double value, int decimals) => value.ToString("F" + decimals, CultureInfo.CurrentCulture);
}
