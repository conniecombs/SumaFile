using System.Globalization;
using System.Text.Json;
using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.PluginHost;

/// <summary>
/// Validates and defaults the string values stored for a plugin's declared settings. Booleans are stored as
/// "true"/"false", integers as invariant decimal, enums as one of the declared options.
/// </summary>
public static class PluginSettingsSchema
{
    /// <summary>Null when <paramref name="value"/> is valid for <paramref name="spec"/>, else a message for the user.</summary>
    public static string? ValidateValue(PluginSettingSpec spec, string value)
    {
        ArgumentNullException.ThrowIfNull(spec);
        value ??= "";
        return spec.Type switch
        {
            "boolean" => value is "true" or "false" ? null : $"{spec.Label} must be true or false.",
            "integer" => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                ? null
                : $"{spec.Label} must be a whole number.",
            "enum" => spec.Options is { } options && options.Contains(value, StringComparer.Ordinal)
                ? null
                : $"{spec.Label} must be one of: {string.Join(", ", spec.Options ?? [])}.",
            "string" or "path" => null,
            _ => $"{spec.Label} has an unsupported type '{spec.Type}'.",
        };
    }

    /// <summary>The manifest's default as a stored string, or the type's default when none (or an unusable one) is declared.</summary>
    public static string DefaultValue(PluginSettingSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Default is { } element && FromJson(element) is { } declared && ValidateValue(spec, declared) is null)
        {
            return declared;
        }

        return spec.Type switch
        {
            "boolean" => "false",
            "integer" => "0",
            "enum" => spec.Options is { Count: > 0 } options ? options[0] : "",
            _ => "",
        };
    }

    private static string? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.TryGetInt64(out var integer)
            ? integer.ToString(CultureInfo.InvariantCulture)
            : element.GetRawText(),
        _ => null,
    };
}
