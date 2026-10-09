using System.Text.Json;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Health;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Backends.OpenCode.Health;

/// <summary>
/// Normalizes OpenCode transport and protocol failures into the health taxonomy.
///
/// When an <see cref="IHealthCenterService"/> is supplied, every failure goes through the normative
/// state machine and lands in the append-only recovery audit, so a repeated failure can actually trip
/// the breaker. The direct repository path is only the fallback for graphs without a Health Center: it
/// pins the scope at <see cref="HealthState.Degraded"/> and cannot quarantine anything.
/// </summary>
public sealed class OpenCodeHealthEventCollector : IOpenCodeHealthEventSink
{
    public const string DefaultScopeType = "OpenCodeBackend";

    private readonly IHealthStateRepository? _repository;
    private readonly IHealthCenterService? _healthCenter;
    private readonly IAccountRepository? _accountRepository;
    private readonly IProviderProfileRepository? _profileRepository;
    private readonly OpenCodeServerOptions? _serverOptions;
    private readonly IOpenCodeClient? _client;
    private readonly ILogger<OpenCodeHealthEventCollector>? _logger;
    private readonly TimeProvider _timeProvider;

    public OpenCodeHealthEventCollector(
        IHealthStateRepository? repository = null,
        ILogger<OpenCodeHealthEventCollector>? logger = null,
        TimeProvider? timeProvider = null,
        IHealthCenterService? healthCenter = null,
        IAccountRepository? accountRepository = null,
        IProviderProfileRepository? profileRepository = null,
        IOptions<OpenCodeServerOptions>? serverOptions = null,
        IOpenCodeClient? client = null)
    {
        _repository = repository;
        _healthCenter = healthCenter;
        _accountRepository = accountRepository;
        _profileRepository = profileRepository;
        _serverOptions = serverOptions?.Value;
        _client = client;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The scope a given OpenCode entity is tracked under by the Health Center.</summary>
    public static HealthScope ScopeFor(string entityId) =>
        new(DefaultScopeType, entityId);

    public Task RecordHealthEventAsync(
        string entityId,
        string failureReason,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        return RecordAsync(entityId, failureReason, exception, statusCode: null, cancellationToken);
    }

    public Task RecordExceptionAsync(
        string entityId,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return RecordHealthEventAsync(entityId, DescribeFailure(exception), exception, cancellationToken);
    }

    public Task RecordHttpFailureAsync(
        string entityId,
        int statusCode,
        CancellationToken cancellationToken = default)
    {
        if (statusCode is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode,
                "HTTP failure status codes must be in the 4xx-5xx range.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        return RecordAsync(
            entityId,
            $"OpenCode server returned HTTP status {statusCode}.",
            exception: null,
            statusCode,
            cancellationToken);
    }

    public static HealthErrorClass ClassifyFailure(Exception? exception, int? statusCode = null)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OpenCodeClientException { StatusCode: { } clientStatus })
            {
                return ClassifyStatusCode((int)clientStatus);
            }

            if (current is JsonException or FormatException)
            {
                return HealthErrorClass.MalformedProtocolEvent;
            }

            if (current is HttpRequestException or TimeoutException or OperationCanceledException or IOException)
            {
                return HealthErrorClass.NetworkOrTimeout;
            }
        }

        if (statusCode is { } status)
        {
            return ClassifyStatusCode(status);
        }

        return HealthErrorClass.UnknownOrAmbiguousCompletion;
    }

    /// <summary>
    /// Maps an HTTP status onto the normative taxonomy. A rejected credential (401/403) is an
    /// authorization failure, not a generic provider error: ТЗ §6.10 requires it to block the account
    /// immediately, and only the auth class is blocked on its very first observation.
    /// </summary>
    private static HealthErrorClass ClassifyStatusCode(int statusCode) =>
        statusCode switch
        {
            401 or 403 => HealthErrorClass.AuthenticationOrRefresh,
            >= 400 => HealthErrorClass.Provider4xx5xx,
            _ => HealthErrorClass.UnknownOrAmbiguousCompletion
        };

    public static string DescribeFailure(Exception? exception)
    {
        if (exception is null)
        {
            return "OpenCode backend failure.";
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OpenCodeClientException { StatusCode: { } statusCode }:
                    return $"OpenCode request failed with HTTP status {(int)statusCode}.";

                case HttpRequestException:
                    return "OpenCode transport failure (connection refused, reset or network error).";

                case TimeoutException or OperationCanceledException:
                    return "OpenCode request timed out.";

                case JsonException or FormatException:
                    return "OpenCode response or event stream payload is malformed.";
            }
        }

        return $"OpenCode backend failure: {exception.GetType().Name}.";
    }

    private async Task RecordAsync(
        string entityId,
        string failureReason,
        Exception? exception,
        int? statusCode,
        CancellationToken cancellationToken)
    {
        var errorClass = ClassifyFailure(exception, statusCode ?? GetStatusCode(exception));
        var updatedAt = _timeProvider.GetUtcNow();

        _logger?.LogWarning(
            exception,
            "OpenCode health event for '{EntityId}': {FailureReason}",
            entityId,
            failureReason);

        if (_healthCenter is not null)
        {
            try
            {
                if (errorClass == HealthErrorClass.AuthenticationOrRefresh)
                {
                    var accounts = await ResolveAuthenticationAccountsAsync(entityId, cancellationToken).ConfigureAwait(false);
                    await _healthCenter.ReportAuthenticationFailuresAsync(
                        new[] { ScopeFor(entityId) }.Concat(accounts.Select(HealthScope.ForAccount)).ToArray(),
                        failureReason, cancellationToken).ConfigureAwait(false);
                }
                else
                    await _healthCenter.ReportFailureAsync(ScopeFor(entityId), errorClass, failureReason, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception healthCenterException)
            {
                _logger?.LogWarning(
                    healthCenterException,
                    "Failed to report the OpenCode health event for '{EntityId}' to the Health Center.",
                    entityId);
                if (errorClass == HealthErrorClass.AuthenticationOrRefresh) throw;
            }

            return;
        }

        if (_repository is null)
        {
            return;
        }

        try
        {
            var existing = await _repository
                .GetAsync(DefaultScopeType, entityId, cancellationToken)
                .ConfigureAwait(false);

            var record = new HealthStateRecord(
                Id: $"{DefaultScopeType}:{entityId}",
                ScopeType: DefaultScopeType,
                ScopeId: entityId,
                State: HealthState.Degraded,
                ErrorClass: errorClass,
                FailureCount: (existing?.FailureCount ?? 0) + 1,
                WindowStartedAt: existing?.WindowStartedAt ?? updatedAt,
                CooldownUntil: null,
                EvidenceRedactedJson: SerializeEvidence(failureReason, exception),
                UpdatedAt: updatedAt);

            await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception repositoryException)
        {
            _logger?.LogWarning(
                repositoryException,
                "Failed to persist the OpenCode health event for '{EntityId}'.",
                entityId);
        }
    }

    /// <summary>
    /// ТЗ §6.10: an authorization failure blocks every route of the account immediately. The entity a
    /// transport failure was observed for may be the account id itself, or may be the bare server base URL the production caller
    /// reports. The router and the sticky-send gate read the account scope, not the backend one, so
    /// without this step a live 401/403 would leave the account routable.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveAuthenticationAccountsAsync(
        string entityId,
        CancellationToken cancellationToken)
    {
        if (_healthCenter is null) return [];

        var accountIds = new HashSet<string>(StringComparer.Ordinal);
        var observed = await _healthCenter.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var snapshot in observed)
        {
            if (snapshot.Scope.ScopeType == HealthScope.AccountScopeType &&
                !string.IsNullOrWhiteSpace(snapshot.Scope.ScopeId) &&
                ReferencesAccount(entityId, snapshot.Scope.ScopeId))
            {
                accountIds.Add(snapshot.Scope.ScopeId);
            }
        }

        await AddAccountsLinkedToServerAsync(entityId, accountIds, cancellationToken).ConfigureAwait(false);

        return accountIds.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Resolves the accounts implicated by the observed entity id through the repositories. The
    /// production caller reports the managed server base URL, which carries no account id, so the
    /// account is found either directly (the entity id is an account id) or through the provider
    /// profiles. A managed serve URL is not the upstream endpoint stored on the profile, so when the
    /// entity id is that serve URL every OpenCode account is implicated; otherwise only the profiles
    /// whose own endpoint the entity id names are. Without this a live 401/403 observed before any
    /// health record existed would leave every account routable.
    /// </summary>
    private async Task AddAccountsLinkedToServerAsync(
        string entityId,
        HashSet<string> accountIds,
        CancellationToken cancellationToken)
    {
        if (_accountRepository is null)
        {
            return;
        }

        var direct = await _accountRepository.GetByIdAsync(entityId, cancellationToken).ConfigureAwait(false);

        if (direct is not null)
        {
            accountIds.Add(direct.Id);
        }

        if (_profileRepository is null)
        {
            return;
        }

        var isManagedServer = IsManagedServerUrl(entityId);
        var profiles = await _profileRepository.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var profile in profiles)
        {
            if (profile.Backend != BackendType.OpenCode ||
                (!isManagedServer && !ReferencesServer(profile.BaseUrl, entityId)))
            {
                continue;
            }

            var accounts = await _accountRepository
                .ListByProviderProfileIdAsync(profile.Id, cancellationToken)
                .ConfigureAwait(false);

            foreach (var account in accounts)
            {
                accountIds.Add(account.Id);
            }
        }
    }

    /// <summary>
    /// True when the entity id names the managed OpenCode serve endpoint rather than an upstream
    /// provider endpoint. The serve URL never matches a profile's upstream base URL, so a live auth
    /// failure observed on it has to implicate every OpenCode account instead of none.
    /// </summary>
    private bool IsManagedServerUrl(string entityId)
    {
        if (_client is not null && ReferencesServer(_client.BaseUrl.ToString(), entityId))
        {
            return true;
        }

        return _serverOptions is not null &&
               ReferencesServer($"http://{_serverOptions.Hostname}:{_serverOptions.Port}", entityId);
    }

    /// <summary>
    /// Only an exact account id identifies credentials here; URL targets use repository endpoint bindings.
    /// </summary>
    private static bool ReferencesAccount(string entityId, string accountId) =>
        string.Equals(entityId, accountId, StringComparison.Ordinal);

    /// <summary>
    /// True when the provider profile points at the server the entity id names. The profile may carry a
    /// path of the same endpoint (for example <c>/v1</c>), so absolute values are compared by scheme,
    /// host and port; anything else falls back to a trailing-slash-insensitive comparison.
    /// </summary>
    private static bool ReferencesServer(string? profileBaseUrl, string entityId)
    {
        if (string.IsNullOrWhiteSpace(profileBaseUrl))
        {
            return false;
        }

        var profile = profileBaseUrl.TrimEnd('/');
        var observed = entityId.TrimEnd('/');

        if (string.Equals(profile, observed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(profile, UriKind.Absolute, out var profileUri) &&
               Uri.TryCreate(observed, UriKind.Absolute, out var observedUri) &&
               string.Equals(
                   profileUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped),
                   observedUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string SerializeEvidence(string failureReason, Exception? exception)
    {
        return JsonSerializer.Serialize(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["failureReason"] = failureReason,
            ["exceptionType"] = exception?.GetType().Name
        });
    }

    private static int? GetStatusCode(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OpenCodeClientException { StatusCode: { } statusCode })
            {
                return (int)statusCode;
            }
        }

        return null;
    }
}
