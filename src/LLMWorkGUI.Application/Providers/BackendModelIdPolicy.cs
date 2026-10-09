namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Accepts only values that can really be a backend-native model id. Several backends store a
/// filesystem path or a profile name in <c>Account.ProviderNativeId</c> (StarCliProxy stores
/// <c>CodexHomePath</c>, Agy stores the profile name), so the same field is not a model id at all.
/// This policy is pure string inspection — no filesystem is touched — and is the single place that
/// decides whether a candidate may be sent to a backend as <c>Model</c>.
/// </summary>
public static class BackendModelIdPolicy
{
    /// <summary>Upper bound for a model id, so a local record can never turn into a huge request field.</summary>
    public const int MaxLength = 200;

    private const string SecretReferencePrefix = "urn:llmworkgui:secret:";

    /// <summary>
    /// Returns the trimmed model id when it is a plausible backend model id, otherwise false. A
    /// rejected value is never repaired or truncated into one: a filesystem path, a profile name, a
    /// secret URN or a blank value simply is not a model id.
    /// </summary>
    public static bool TryNormalize(string? value, out string modelId)
    {
        modelId = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();

        if (candidate.Length > MaxLength)
        {
            return false;
        }

        foreach (var character in candidate)
        {
            // Control characters, newlines and inner whitespace never belong to a model id and would
            // let a record break out of a single request field.
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        if (candidate.StartsWith(SecretReferencePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A backslash is a Windows path separator and a model id never contains one.
        if (candidate.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        // Rooted or home-relative POSIX paths, and the ".." parent segment, are traversal shapes.
        if (candidate.StartsWith('/')
            || candidate.StartsWith('~')
            || candidate.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        // A drive prefix is not a model tag: "llama3:8b" qualifies, "C:model" is drive-relative. The
        // asymmetry is deliberate — an exotic model tag that is refused costs one route, while a
        // drive-relative path accepted as a model would put a filesystem path into a backend request.
        if (candidate.Length >= 2
            && char.IsAsciiLetter(candidate[0])
            && candidate[1] == ':')
        {
            return false;
        }

        modelId = candidate;
        return true;
    }

    /// <summary>Convenience form of <see cref="TryNormalize"/> for call sites that only need a yes/no.</summary>
    public static bool IsSelectableModelId(string? value) => TryNormalize(value, out _);
}
