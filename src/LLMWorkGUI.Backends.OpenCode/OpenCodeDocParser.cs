using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode;

namespace LLMWorkGUI.Backends.OpenCode;

public static class OpenCodeDocParser
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get",
        "put",
        "post",
        "delete",
        "options",
        "head",
        "patch",
        "trace"
    };

    public static OpenCodeDocResponse Parse(string rawJson)
    {
        ArgumentNullException.ThrowIfNull(rawJson);

        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;

        var specification = root.TryGetProperty("openapi", out var specificationElement)
            && specificationElement.ValueKind == JsonValueKind.String
                ? specificationElement.GetString() ?? string.Empty
                : string.Empty;

        var operations = new List<OpenCodeDocOperation>();

        if (root.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
        {
            foreach (var path in paths.EnumerateObject())
            {
                if (path.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var operation in path.Value.EnumerateObject())
                {
                    if (operation.Value.ValueKind == JsonValueKind.Object
                        && HttpMethods.Contains(operation.Name))
                    {
                        operations.Add(new OpenCodeDocOperation(
                            operation.Name.ToUpperInvariant(),
                            path.Name));
                    }
                }
            }
        }

        return new OpenCodeDocResponse
        {
            OpenApiSpecification = specification,
            Operations = operations,
            RawJson = rawJson
        };
    }
}
