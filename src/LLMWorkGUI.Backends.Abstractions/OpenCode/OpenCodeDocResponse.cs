namespace LLMWorkGUI.Backends.Abstractions.OpenCode;

public sealed record OpenCodeDocResponse
{
    public required string OpenApiSpecification { get; init; }

    public required IReadOnlyList<OpenCodeDocOperation> Operations { get; init; }

    public required string RawJson { get; init; }

    public bool HasOperation(string method, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Operations.Any(operation =>
            string.Equals(operation.Method, method, StringComparison.OrdinalIgnoreCase)
            && string.Equals(operation.Path, path, StringComparison.Ordinal));
    }
}
