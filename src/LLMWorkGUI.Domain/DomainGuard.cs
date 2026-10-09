namespace LLMWorkGUI.Domain;

internal static class DomainGuard
{
    public static string NotBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be null, empty, or whitespace.", parameterName);
        }

        return value;
    }

    public static string? OptionalNotBlank(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty or whitespace when provided.", parameterName);
        }

        return value;
    }

    public static IReadOnlyList<T> NotNullList<T>(IReadOnlyList<T> value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return value.ToArray();
    }
}
