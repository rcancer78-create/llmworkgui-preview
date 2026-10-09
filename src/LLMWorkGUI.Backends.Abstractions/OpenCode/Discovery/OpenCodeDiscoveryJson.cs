using System.Text;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;

public static class OpenCodeDiscoveryJson
{
    private static readonly string[] ConnectedPropertyNames = { "connected" };

    public static IReadOnlyList<OpenCodeProviderInfo> ParseProviders(string rawJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);

        using var document = JsonDocument.Parse(rawJson);

        return ParseProviders(document.RootElement);
    }

    public static IReadOnlyList<OpenCodeModelInfo> ParseModels(string rawJson, string? providerId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);

        using var document = JsonDocument.Parse(rawJson);

        var models = ParseModels(document.RootElement, providerId: null);

        if (string.IsNullOrWhiteSpace(providerId))
        {
            return models;
        }

        return models
            .Where(model => string.Equals(model.ProviderId, providerId, StringComparison.Ordinal))
            .ToArray();
    }

    public static OpenCodeConfiguredProvidersResponse ParseConfiguredProviders(string rawJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);

        using var document = JsonDocument.Parse(rawJson);

        return ParseConfiguredProviders(document.RootElement);
    }

    public static string SerializeConfiguredProviders(OpenCodeConfiguredProvidersResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("providers");

            foreach (var provider in response.Providers)
            {
                writer.WriteStartObject();
                writer.WriteString("id", provider.Id);
                writer.WriteString("name", provider.Name);

                if (provider.BaseUrl is not null)
                {
                    writer.WriteString("baseUrl", provider.BaseUrl);
                }

                writer.WriteBoolean("isConnected", provider.IsConnected);
                writer.WriteStartArray("models");

                foreach (var model in provider.Models)
                {
                    writer.WriteStringValue(model);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("defaultModels");

            foreach (var pair in response.DefaultModels)
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IReadOnlyList<OpenCodeProviderInfo> ParseProviders(JsonElement root)
    {
        return root.ValueKind switch
        {
            JsonValueKind.Array => ParseProviderArray(root, connectedIds: null),
            JsonValueKind.Object => ParseProviderObject(root),
            _ => throw new JsonException(
                "The OpenCode provider payload must be a JSON array or object.")
        };
    }

    private static IReadOnlyList<OpenCodeProviderInfo> ParseProviderObject(JsonElement root)
    {
        if (root.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array)
        {
            return ParseProviderArray(all, ReadStringSet(root, ConnectedPropertyNames));
        }

        if (root.TryGetProperty("providers", out var providers))
        {
            return providers.ValueKind switch
            {
                JsonValueKind.Array => ParseProviderArray(providers, ReadStringSet(root, ConnectedPropertyNames)),
                JsonValueKind.Object => ParseProviderDictionary(
                    providers,
                    ReadStringSet(root, ConnectedPropertyNames)),
                _ => throw new JsonException(
                    "The OpenCode provider payload 'providers' member must be a JSON array or object.")
            };
        }

        return ParseProviderDictionary(root, connectedIds: null);
    }

    private static IReadOnlyList<OpenCodeProviderInfo> ParseProviderArray(
        JsonElement array,
        IReadOnlySet<string>? connectedIds)
    {
        var providers = new List<OpenCodeProviderInfo>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var provider = ParseProvider(element, fallbackId: null, connectedIds);

            if (provider is not null)
            {
                providers.Add(provider);
            }
        }

        return providers;
    }

    private static IReadOnlyList<OpenCodeProviderInfo> ParseProviderDictionary(
        JsonElement dictionary,
        IReadOnlySet<string>? connectedIds)
    {
        var providers = new List<OpenCodeProviderInfo>();

        foreach (var property in dictionary.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var provider = ParseProvider(property.Value, property.Name, connectedIds);

            if (provider is not null)
            {
                providers.Add(provider);
            }
        }

        return providers;
    }

    private static OpenCodeProviderInfo? ParseProvider(
        JsonElement element,
        string? fallbackId,
        IReadOnlySet<string>? connectedIds)
    {
        var id = OpenCodeJson.GetString(element, "id")
            ?? OpenCodeJson.GetString(element, "providerID")
            ?? (string.IsNullOrWhiteSpace(fallbackId) ? null : fallbackId);

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var name = OpenCodeJson.GetString(element, "name");
        var baseUrl = OpenCodeJson.GetString(element, "baseUrl")
            ?? OpenCodeJson.GetString(element, "baseURL")
            ?? OpenCodeJson.GetString(element, "api")
            ?? OpenCodeJson.GetString(element, "url");

        var isConnected = GetBoolean(element, "isConnected")
            ?? GetBoolean(element, "connected")
            ?? connectedIds?.Contains(id)
            ?? false;

        return new OpenCodeProviderInfo
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            BaseUrl = baseUrl,
            IsConnected = isConnected,
            Models = ParseProviderModelIds(element)
        };
    }

    private static IReadOnlyList<string> ParseProviderModelIds(JsonElement provider)
    {
        if (provider.ValueKind != JsonValueKind.Object
            || !provider.TryGetProperty("models", out var models)
            || models.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<string>();
        }

        var ids = new List<string>();

        if (models.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in models.EnumerateObject())
            {
                var id = property.Value.ValueKind == JsonValueKind.Object
                    ? OpenCodeJson.GetString(property.Value, "id") ?? property.Name
                    : property.Name;

                AddDistinct(ids, id);
            }
        }
        else if (models.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in models.EnumerateArray())
            {
                var id = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Object => OpenCodeJson.GetString(element, "id")
                        ?? OpenCodeJson.GetString(element, "modelID"),
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(id))
                {
                    AddDistinct(ids, id);
                }
            }
        }

        return ids;
    }

    private static OpenCodeConfiguredProvidersResponse ParseConfiguredProviders(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                return new OpenCodeConfiguredProvidersResponse
                {
                    Providers = ParseProviderArray(root, connectedIds: null)
                };

            case JsonValueKind.Object:
                var providers = ParseProviderObject(root);
                var defaultModels = ReadStringMap(root, "defaultModels")
                    ?? ReadStringMap(root, "default")
                    ?? OpenCodeConfiguredProvidersResponse.EmptyDefaultModels;

                return new OpenCodeConfiguredProvidersResponse
                {
                    Providers = providers,
                    DefaultModels = defaultModels
                };

            default:
                throw new JsonException(
                    "The OpenCode configured providers payload must be a JSON array or object.");
        }
    }

    private static IReadOnlyList<OpenCodeModelInfo> ParseModels(JsonElement root, string? providerId)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                return ParseModelArray(root, providerId);

            case JsonValueKind.Object:
                if (root.TryGetProperty("models", out var models)
                    && models.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    return ParseModelCollection(models, providerId);
                }

                if (root.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array)
                {
                    return ParseModelArray(all, providerId);
                }

                return IsProviderKeyedModelMap(root)
                    ? ParseProviderKeyedModels(root)
                    : ParseModelDictionary(root, providerId);

            default:
                throw new JsonException(
                    "The OpenCode model payload must be a JSON array or object.");
        }
    }

    private static IReadOnlyList<OpenCodeModelInfo> ParseModelCollection(JsonElement collection, string? providerId)
    {
        return collection.ValueKind switch
        {
            JsonValueKind.Array => ParseModelArray(collection, providerId),
            JsonValueKind.Object => ParseModelDictionary(collection, providerId),
            _ => throw new JsonException("The OpenCode model payload must be a JSON array or object.")
        };
    }

    private static IReadOnlyList<OpenCodeModelInfo> ParseProviderKeyedModels(JsonElement root)
    {
        var models = new List<OpenCodeModelInfo>();

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object
                || !property.Value.TryGetProperty("models", out var nested)
                || nested.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            {
                continue;
            }

            models.AddRange(ParseModelCollection(nested, property.Name));
        }

        return models;
    }

    private static IReadOnlyList<OpenCodeModelInfo> ParseModelArray(JsonElement array, string? providerId)
    {
        var models = new List<OpenCodeModelInfo>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var model = ParseModel(element, fallbackId: null, providerId);

            if (model is not null)
            {
                models.Add(model);
            }
        }

        return models;
    }

    private static IReadOnlyList<OpenCodeModelInfo> ParseModelDictionary(JsonElement dictionary, string? providerId)
    {
        var models = new List<OpenCodeModelInfo>();

        foreach (var property in dictionary.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var model = ParseModel(property.Value, property.Name, providerId);

            if (model is not null)
            {
                models.Add(model);
            }
        }

        return models;
    }

    private static OpenCodeModelInfo? ParseModel(JsonElement element, string? fallbackId, string? providerId)
    {
        var id = OpenCodeJson.GetString(element, "id")
            ?? OpenCodeJson.GetString(element, "modelID")
            ?? (string.IsNullOrWhiteSpace(fallbackId) ? null : fallbackId);

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var resolvedProviderId = OpenCodeJson.GetString(element, "providerId")
            ?? OpenCodeJson.GetString(element, "providerID")
            ?? providerId
            ?? string.Empty;

        var name = OpenCodeJson.GetString(element, "name");

        return new OpenCodeModelInfo
        {
            Id = id,
            ProviderId = resolvedProviderId,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            Variants = ParseVariants(element),
            Capabilities = ParseCapabilities(element),
            ContextLimit = ParseContextLimit(element)
        };
    }

    private static IReadOnlyList<string> ParseVariants(JsonElement model)
    {
        if (!model.TryGetProperty("variants", out var variants)
            || variants.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();

        if (variants.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in variants.EnumerateObject())
            {
                var id = property.Value.ValueKind == JsonValueKind.Object
                    ? OpenCodeJson.GetString(property.Value, "id") ?? property.Name
                    : property.Name;

                AddDistinct(values, id);
            }
        }
        else if (variants.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in variants.EnumerateArray())
            {
                var id = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Object => OpenCodeJson.GetString(element, "id")
                        ?? OpenCodeJson.GetString(element, "name"),
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(id))
                {
                    AddDistinct(values, id);
                }
            }
        }

        return values;
    }

    private static IReadOnlyList<string> ParseCapabilities(JsonElement model)
    {
        if (!model.TryGetProperty("capabilities", out var capabilities)
            || capabilities.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();

        if (capabilities.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in capabilities.EnumerateObject())
            {
                if (IsTruthy(property.Value))
                {
                    AddDistinct(values, property.Name);
                }
            }
        }
        else if (capabilities.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in capabilities.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    AddDistinct(values, element.GetString());
                }
            }
        }

        return values;
    }

    private static int? ParseContextLimit(JsonElement model)
    {
        var contextLimit = OpenCodeJson.GetInt64(model, "contextLimit")
            ?? OpenCodeJson.GetInt64(model, "contextWindow");

        if (contextLimit is not null)
        {
            return ToContextLimit(contextLimit);
        }

        if (!model.TryGetProperty("limit", out var limit)
            || limit.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (limit.ValueKind == JsonValueKind.Number || limit.ValueKind == JsonValueKind.String)
        {
            return ToContextLimit(OpenCodeJson.GetInt64(model, "limit"));
        }

        if (limit.ValueKind == JsonValueKind.Object)
        {
            var nested = OpenCodeJson.GetInt64(limit, "context")
                ?? OpenCodeJson.GetInt64(limit, "input");

            return ToContextLimit(nested);
        }

        return null;
    }

    private static int? ToContextLimit(long? value)
    {
        if (value is null or < 0 or > int.MaxValue)
        {
            return null;
        }

        return (int)value.Value;
    }

    private static bool IsProviderKeyedModelMap(JsonElement root)
    {
        var hasNestedModels = false;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (property.Value.TryGetProperty("models", out var nested)
                && nested.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                hasNestedModels = true;
            }
        }

        return hasNestedModels;
    }

    private static IReadOnlySet<string>? ReadStringSet(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var values = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    values.Add(item.GetString()!);
                }
            }

            return values;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string>? ReadStringMap(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var mapValue = property.Value.GetString();

                if (!string.IsNullOrWhiteSpace(mapValue))
                {
                    map[property.Name] = mapValue;
                }
            }
        }

        return map;
    }

    private static bool? GetBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        if (value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool IsTruthy(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetDouble(out var number)
                && number > 0
                && !double.IsNaN(number),
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed)
                && parsed,
            _ => false
        };
    }

    private static void AddDistinct(List<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || values.Contains(value, StringComparer.Ordinal))
        {
            return;
        }

        values.Add(value);
    }
}
