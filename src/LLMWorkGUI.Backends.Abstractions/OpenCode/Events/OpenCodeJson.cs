using System.Globalization;
using System.Text.Json;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

internal static class OpenCodeJson
{
    public static bool TryGetObject(JsonElement element, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            value = element;
            return true;
        }

        value = default;
        return false;
    }

    public static bool TryGetObjectProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    public static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    public static long? GetInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static decimal? GetDecimal(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var number))
        {
            return number;
        }

        return null;
    }

    public static long? GetObjectProperty(JsonElement element, string objectPropertyName, string propertyName)
    {
        return TryGetObjectProperty(element, objectPropertyName, out var nested)
            ? GetInt64(nested, propertyName)
            : null;
    }

    public static JsonElement CloneProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind != JsonValueKind.Null)
        {
            return value.Clone();
        }

        return default;
    }

    public static DateTimeOffset? FromUnixMilliseconds(long? unixMilliseconds)
    {
        if (unixMilliseconds is not { } value)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
