using System.Globalization;
using System.Text;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Route identity for adaptation. The four identity parts are carried as separate fields and are
/// never collapsed into one string: <see cref="AccountId"/> is the local account record id,
/// <see cref="ProviderProfileId"/> the owning profile, <see cref="Backend"/> the backend that
/// profile resolves to and <see cref="BackendModelId"/> the backend-native model id, which is taken
/// from the local provider configuration or, when it has none, from the local account record
/// (<c>Account.ProviderNativeId</c>).
///
/// <para>
/// Only <see cref="RouteId"/> is a single string, and it is built so that it can never be confused
/// with a bare account id: it carries a reserved prefix and every component is percent-encoded with
/// a closed unreserved set, so the separator can appear nowhere inside a component. A route id is
/// therefore resolved structurally or, when it is not a route id at all, treated as an account id
/// and looked up in the repositories. No literal route string is ever classified as a backend.
/// </para>
/// </summary>
public sealed record AdaptationRouteIdentity
{
    private const string RouteIdPrefix = "route:";
    private const char ComponentSeparator = '|';
    private const int ComponentCount = 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public AdaptationRouteIdentity(
        string accountId,
        string providerProfileId,
        BackendType backend,
        string? backendModelId = null)
    {
        AccountId = ApplicationGuard.NotBlank(accountId, nameof(accountId));
        ProviderProfileId = ApplicationGuard.NotBlank(providerProfileId, nameof(providerProfileId));
        Backend = backend;
        if (!string.IsNullOrWhiteSpace(backendModelId))
        {
            if (!BackendModelIdPolicy.TryNormalize(backendModelId, out var normalizedModelId))
            {
                throw new ArgumentException("The backend model identifier is invalid.", nameof(backendModelId));
            }

            BackendModelId = normalizedModelId;
        }
    }

    public string AccountId { get; }

    public string ProviderProfileId { get; }

    public BackendType Backend { get; }

    /// <summary>
    /// Backend-native model id. Null when no local source supplies one — neither the account record
    /// (<c>Account.ProviderNativeId</c>) nor the local provider configuration — in which case no
    /// backend request may name a model.
    /// </summary>
    public string? BackendModelId { get; }

    public bool HasBackendModelId => !string.IsNullOrWhiteSpace(BackendModelId);

    /// <summary>Returns the same route with a different backend-native model id.</summary>
    public AdaptationRouteIdentity WithBackendModelId(string? backendModelId) =>
        new(AccountId, ProviderProfileId, Backend, backendModelId);

    /// <summary>
    /// Closed set of backends the adaptation engine can actually run. Every other backend
    /// (CursorAcp, Mirasim, StarCliProxy, Agy) is refused by the invoker, so its routes are never
    /// offered as adaptation targets.
    /// </summary>
    public bool IsAdaptationCapable => IsAdaptationCapableBackend(Backend);

    /// <summary>
    /// The single source of truth for backend capability, so a caller that has no identity yet — the
    /// catalog projection, for one — asks the same question the route and the invoker answer.
    /// </summary>
    public static bool IsAdaptationCapableBackend(BackendType backend) => backend is BackendType.OpenCode;

    /// <summary>A route is only offered when the backend can run it and a native model id is known.</summary>
    public bool IsSelectableForAdaptation => IsAdaptationCapable && HasBackendModelId;

    /// <summary>
    /// Enumerates the real account+model routes this identity stands for: one identity per selectable
    /// backend model id, in configuration order. An account with several configured models therefore
    /// yields several distinct route ids, each carrying the model that a backend request may name, and
    /// a route on a backend that cannot run adaptation yields none.
    /// </summary>
    public IReadOnlyList<AdaptationRouteIdentity> EnumerateModelRoutes(IEnumerable<string> backendModelIds)
    {
        ArgumentNullException.ThrowIfNull(backendModelIds);

        if (!IsAdaptationCapable)
        {
            return Array.Empty<AdaptationRouteIdentity>();
        }

        var routes = new List<AdaptationRouteIdentity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var modelId in backendModelIds)
        {
            if (BackendModelIdPolicy.TryNormalize(modelId, out var normalized) && seen.Add(normalized))
            {
                routes.Add(WithBackendModelId(normalized));
            }
        }

        return routes;
    }

    public string RouteId => BuildRouteId(this);

    /// <summary>
    /// Builds a route identity from a catalog row. Returns false when the row does not carry a
    /// complete account/profile/backend identity, so a partial row is never offered as a route.
    /// </summary>
    public static bool TryCreate(SanitizedModelInfo model, out AdaptationRouteIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(model.AccountId)
            || string.IsNullOrWhiteSpace(model.ProviderProfileId)
            || model.Backend is not { } backend
            || !Enum.IsDefined(backend)
            || (!string.IsNullOrWhiteSpace(model.BackendModelId)
                && !BackendModelIdPolicy.IsSelectableModelId(model.BackendModelId)))
        {
            identity = null!;
            return false;
        }

        identity = new AdaptationRouteIdentity(
            model.AccountId,
            model.ProviderProfileId,
            backend,
            model.BackendModelId);

        return true;
    }

    /// <summary>
    /// Parses a route id produced by <see cref="RouteId"/>. Returns false for any other string,
    /// including a bare account id and every reserved literal such as "cursor" or "opencode".
    /// </summary>
    public static bool TryParse(string? routeId, out AdaptationRouteIdentity identity)
    {
        identity = null!;

        if (string.IsNullOrEmpty(routeId)
            || !routeId.StartsWith(RouteIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var components = routeId[RouteIdPrefix.Length..].Split(ComponentSeparator);

        if (components.Length != ComponentCount)
        {
            return false;
        }

        if (!TryDecode(components[0], out var accountId)
            || !TryDecode(components[1], out var providerProfileId)
            || !TryDecode(components[2], out var backendText)
            || !Enum.TryParse(backendText, ignoreCase: false, out BackendType backend)
            || !Enum.IsDefined(backend)
            || string.IsNullOrWhiteSpace(accountId)
            || string.IsNullOrWhiteSpace(providerProfileId)
            || !TryDecode(components[3], out var backendModelId)
            || (!string.IsNullOrWhiteSpace(backendModelId)
                && !BackendModelIdPolicy.IsSelectableModelId(backendModelId)))
        {
            return false;
        }

        identity = new AdaptationRouteIdentity(
            accountId,
            providerProfileId,
            backend,
            string.IsNullOrEmpty(backendModelId) ? null : backendModelId);

        return true;
    }

    /// <summary>
    /// Enumerates the account ids a route id may stand for, in resolution order: the account carried
    /// by a route id, otherwise the route id itself, otherwise the legacy "accountId:modelId"
    /// prefix. Every candidate is a repository lookup; no candidate is classified as a backend.
    /// </summary>
    public static IEnumerable<string> EnumerateCandidateAccountIds(string routeId)
    {
        if (TryParse(routeId, out var identity))
        {
            yield return identity.AccountId;
            yield break;
        }

        yield return routeId;

        var separatorIndex = routeId.LastIndexOf(':');

        if (separatorIndex > 0 && separatorIndex < routeId.Length - 1)
        {
            yield return routeId[..separatorIndex];
        }
    }

    private static string BuildRouteId(AdaptationRouteIdentity identity) => string.Concat(
        RouteIdPrefix,
        Encode(identity.AccountId),
        ComponentSeparator.ToString(),
        Encode(identity.ProviderProfileId),
        ComponentSeparator.ToString(),
        Encode(identity.Backend.ToString()),
        ComponentSeparator.ToString(),
        Encode(identity.BackendModelId ?? string.Empty));

    /// <summary>
    /// Percent-encodes everything outside the unreserved set, so the component separator, the
    /// prefix colon and the percent sign itself can never occur inside a component.
    /// </summary>
    private static string Encode(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var octet in StrictUtf8.GetBytes(value))
        {
            var character = (char)octet;
            var isUnreserved = character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_' or '.' or '~';

            if (isUnreserved)
            {
                builder.Append(character);
                continue;
            }

            builder.Append('%');
            builder.Append(octet.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;

        if (value.Length == 0)
        {
            return true;
        }

        var bytes = new byte[value.Length];
        var byteCount = 0;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (character == '%')
            {
                if (index + 2 >= value.Length
                    || !byte.TryParse(
                        value.AsSpan(index + 1, 2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var code))
                {
                    return false;
                }

                bytes[byteCount++] = code;
                index += 2;
                continue;
            }

            var isUnreserved = character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_' or '.' or '~';

            if (!isUnreserved)
            {
                return false;
            }

            bytes[byteCount++] = (byte)character;
        }

        try
        {
            decoded = StrictUtf8.GetString(bytes, 0, byteCount);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
