using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

public sealed record OpenCodeSessionResponse
{
    public required string Id { get; init; }

    public string? Slug { get; init; }

    public string? ProjectId { get; init; }

    public string? Directory { get; init; }

    public string? Title { get; init; }

    public string? Version { get; init; }

    public DateTime? CreatedAtUtc { get; init; }

    public DateTime? UpdatedAtUtc { get; init; }

    public string? Model { get; init; }

    public OpenCodeTokenUsage? Tokens { get; init; }

    public decimal? Cost { get; init; }

    public string? ParentId { get; init; }

    public static OpenCodeSessionResponse Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);

        return Parse(document.RootElement);
    }

    public static OpenCodeSessionResponse Parse(JsonElement element)
    {
        if (!TryParse(element, out var response))
        {
            throw new JsonException("The OpenCode session payload does not contain a native session identifier.");
        }

        return response;
    }

    public static bool TryParse(JsonElement element, [NotNullWhen(true)] out OpenCodeSessionResponse? response)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            response = null;
            return false;
        }

        var id = OpenCodeJson.GetString(element, "id");

        if (string.IsNullOrWhiteSpace(id))
        {
            response = null;
            return false;
        }

        response = new OpenCodeSessionResponse
        {
            Id = id,
            Slug = OpenCodeJson.GetString(element, "slug"),
            ProjectId = OpenCodeJson.GetString(element, "projectID"),
            Directory = OpenCodeJson.GetString(element, "directory"),
            Title = OpenCodeJson.GetString(element, "title"),
            Version = OpenCodeJson.GetString(element, "version"),
            CreatedAtUtc = ToUtcDateTime(OpenCodeJson.GetObjectProperty(element, "time", "created")),
            UpdatedAtUtc = ToUtcDateTime(OpenCodeJson.GetObjectProperty(element, "time", "updated")),
            Model = ParseModel(element),
            Tokens = OpenCodeTokenUsage.TryParse(element, out var tokens) ? tokens : null,
            Cost = OpenCodeJson.GetDecimal(element, "cost"),
            ParentId = OpenCodeJson.GetString(element, "parentID")
        };

        return true;
    }

    private static DateTime? ToUtcDateTime(long? unixMilliseconds)
    {
        return OpenCodeJson.FromUnixMilliseconds(unixMilliseconds)?.UtcDateTime;
    }

    private static string? ParseModel(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("model", out var model))
        {
            return null;
        }

        if (model.ValueKind == JsonValueKind.String)
        {
            return model.GetString();
        }

        if (model.ValueKind == JsonValueKind.Object)
        {
            return OpenCodeJson.GetString(model, "modelID") ?? OpenCodeJson.GetString(model, "id");
        }

        return null;
    }
}
