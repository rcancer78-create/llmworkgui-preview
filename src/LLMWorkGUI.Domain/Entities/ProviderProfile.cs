using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.Entities;

public sealed class ProviderProfile
{
    public ProviderProfile(
        string id,
        string displayName,
        BackendType backend,
        string? baseUrl,
        string? executablePath,
        DataClassification maxDataClass,
        bool isEnabled,
        string? gatewayNativeId = null,
        IReadOnlyList<ProviderHeader>? customHeaders = null,
        long? revision = null)
    {
        Id = DomainGuard.NotBlank(id, nameof(id));
        DisplayName = DomainGuard.NotBlank(displayName, nameof(displayName));
        Backend = backend;
        BaseUrl = DomainGuard.OptionalNotBlank(baseUrl, nameof(baseUrl));
        ExecutablePath = DomainGuard.OptionalNotBlank(executablePath, nameof(executablePath));
        MaxDataClass = maxDataClass;
        IsEnabled = isEnabled;
        GatewayNativeId = DomainGuard.OptionalNotBlank(gatewayNativeId, nameof(gatewayNativeId));
        if (revision < -1) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
        if (customHeaders is not null)
        {
            if (customHeaders.Count > 64 || customHeaders.Any(h => h is null)
                || customHeaders.Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != customHeaders.Count)
                throw new ArgumentException("Headers must have unique names and contain at most 64 entries.", nameof(customHeaders));
            CustomHeaders = Array.AsReadOnly(customHeaders.ToArray());
        }
    }

    public string Id { get; }

    public string DisplayName { get; }

    public BackendType Backend { get; }

    public string? BaseUrl { get; }

    public string? ExecutablePath { get; }

    public DataClassification MaxDataClass { get; }

    public bool IsEnabled { get; }

    /// <summary>Null preserves stored headers on update; an empty collection explicitly removes them.</summary>
    public IReadOnlyList<ProviderHeader>? CustomHeaders { get; }

    /// <summary>Expected persisted revision; -1 requires insertion, null is an unconditional repository update.</summary>
    public long? Revision { get; }

    /// <summary>
    /// The name a gateway reports for this provider, when one has been observed and stored.
    /// <para>
    /// Deliberately separate from <see cref="Id"/> and from <see cref="DisplayName"/>. Neither of those is
    /// the gateway's own name for the provider, and a display name is a label a person chose. Null means no
    /// gateway name is recorded for this profile, which is the state of every row written before the
    /// identity existed; it is not an instruction to guess one from the id or the display name.
    /// </para>
    /// </summary>
    public string? GatewayNativeId { get; }
}
