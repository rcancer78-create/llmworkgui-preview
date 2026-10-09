using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Enums;
using System.Security.Cryptography;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LLMWorkGUI.Infrastructure.Http;

namespace LLMWorkGUI.Infrastructure.Mirasim;

public sealed class MirasimSessionLifecycleService : IMirasimSessionLifecycleService, IDisposable
{
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public const int MaxStreamLineChars = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly MirasimOptions _options;
    private readonly ICheckoutLockService? _checkoutLockService;
    private readonly ILogger<MirasimSessionLifecycleService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IMirasimEgressPolicy? _egressPolicy;
    private readonly IMirasimExecutionJournal? _executionJournal;
    private readonly MirasimLifecycleHostedService _lifetime;
    private readonly bool _ownsLifetime;
    private readonly bool _ownsHttpClient;
    private readonly object _resourceGate = new();
    private readonly int _maxSessions;
    private readonly int _maxExecutions;
    private int _sessionReservations;
    private int _executionReservations;
    private int _uncertainSessionCreations;
    private int _disposed;
    private readonly ConcurrentDictionary<string, MirasimExecutionAdmission> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OwnedTransportAuthority> _executionAuthorities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _turnGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MirasimSessionBinding> _localSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _uncertainContinuations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _localCleanup = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ICheckoutLockToken> _writerLockTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionExecutions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _turnExecutions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lockReleaseGate = new(1, 1);
    private readonly ConcurrentDictionary<string, RouteExpectation> _turnRouteExpectations = new(StringComparer.Ordinal);
    private readonly Uri? _baseUri;
    private readonly string? _unsupportedTransportReason;

    public MirasimSessionLifecycleService(
        HttpClient httpClient,
        IOptions<MirasimOptions> options,
        ICheckoutLockService? checkoutLockService = null,
        ILogger<MirasimSessionLifecycleService>? logger = null,
        TimeProvider? timeProvider = null,
        IMirasimEgressPolicy? egressPolicy = null,
        IMirasimExecutionJournal? executionJournal = null,
        MirasimLifecycleHostedService? lifetime = null,
        bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _options = options.Value;
        _checkoutLockService = checkoutLockService;
        _logger = logger ?? NullLogger<MirasimSessionLifecycleService>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _egressPolicy = egressPolicy;
        _executionJournal = executionJournal;
        if (_options.MaxRetainedSessions is < 1 or > 4096 || _options.MaxRetainedExecutions is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(options), "Mirasim retained ownership limits are invalid.");
        _maxSessions = _options.MaxRetainedSessions; _maxExecutions = _options.MaxRetainedExecutions;
        _ownsLifetime = lifetime is null; _ownsHttpClient = ownsHttpClient;
        _lifetime = lifetime ?? new MirasimLifecycleHostedService();
        _lifetime.Attach(this);

        if (!MirasimOptions.IsLoopbackHostname(_options.Hostname) || _options.Port is < 1 or > 65535)
        {
            _unsupportedTransportReason =
                $"The configured Mirasim endpoint '{_options.Hostname}:{_options.Port}' is not a permitted loopback " +
                "channel; product execution remains unsupported (ADR-0008 §6).";
        }
        else
        {
            _baseUri = new Uri($"http://{_options.Hostname}:{_options.Port}", UriKind.Absolute);
        }
    }

    public Task<MirasimSessionBinding> CreateSessionAsync(string instanceId, string harness, string modelId,
        string workspacePath, string? authToken = null, CancellationToken cancellationToken = default,
        ProjectProviderContext? projectContext = null) => _lifetime.RunAsync(token => CreateSessionCoreAsync(
            instanceId, harness, modelId, workspacePath, authToken, token, projectContext), cancellationToken);

    private async Task<MirasimSessionBinding> CreateSessionCoreAsync(
        string instanceId,
        string harness,
        string modelId,
        string workspacePath,
        string? authToken = null,
        CancellationToken cancellationToken = default,
        ProjectProviderContext? projectContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (modelId.Equals("auto", StringComparison.OrdinalIgnoreCase)) throw new MirasimEgressPolicyException();
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        EnsureTransportSupported();
        if (projectContext is null || _egressPolicy is null) throw new MirasimEgressPolicyException();
        using var reservation = ReserveSession();
        var policyFingerprint = await _egressPolicy.ValidateAsync(projectContext, workspacePath, cancellationToken).ConfigureAwait(false);

        var payload = new SessionRequestPayload
        {
            Instance = instanceId,
            Harness = harness,
            Model = modelId,
            Workspace = workspacePath
        };

        var responsePayload = await SendSessionRequestAsync(
                BuildUri("api/sessions"),
                payload,
                authToken,
                "session creation",
                cancellationToken,
                new(projectContext, workspacePath, _baseUri!.AbsoluteUri, "SessionCreate", "", harness, modelId, null, "")
                    { ExpectedPolicyFingerprint = policyFingerprint }, reservation.MarkDispatched)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(responsePayload.SessionKey))
        {
            throw new MirasimClientException("The Mirasim session creation response did not include a session key.");
        }

        var binding = new MirasimSessionBinding
        {
            ProjectContext = projectContext,
            InstanceId = instanceId,
            Harness = harness,
            ModelId = modelId,
            RouteMode = MirasimRouteModes.Normalize(responsePayload.RouteMode),
            SessionKey = responsePayload.SessionKey,
            WorkspacePath = workspacePath
        };
        if (!_localSessions.TryAdd(binding.SessionKey, binding)) throw new MirasimEgressPolicyException();
        reservation.KeepKnownSession();
        return binding;
    }

    public Task<MirasimSessionBinding> ContinueSessionAsync(MirasimSessionBinding existingBinding,
        string? authToken = null, CancellationToken cancellationToken = default) => _lifetime.RunAsync(
            token => ContinueSessionCoreAsync(existingBinding, authToken, token), cancellationToken);

    private async Task<MirasimSessionBinding> ContinueSessionCoreAsync(
        MirasimSessionBinding existingBinding,
        string? authToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existingBinding);
        if (!_localSessions.ContainsKey(existingBinding.SessionKey)) throw new MirasimEgressPolicyException();
        var gate = _turnGates.GetOrAdd(existingBinding.SessionKey, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new MirasimEgressPolicyException();
        try { return await ContinueOwnedSessionAsync(existingBinding, authToken, cancellationToken).ConfigureAwait(false); }
        finally { ReleaseTurnGate(existingBinding.SessionKey, gate); }
    }

    private async Task<MirasimSessionBinding> ContinueOwnedSessionAsync(MirasimSessionBinding existingBinding,
        string? authToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(existingBinding);

        EnsureTransportSupported();
        if (_egressPolicy is null || existingBinding.ProjectContext is null
            || !_localSessions.TryGetValue(existingBinding.SessionKey, out var stored) || stored != existingBinding
            || stored.ProjectContext is not { } context
            || _uncertainContinuations.ContainsKey(existingBinding.SessionKey)
            || _sessionExecutions.ContainsKey(existingBinding.SessionKey)) throw new MirasimEgressPolicyException();
        using var reservation = ReserveSession();
        var policyFingerprint = await _egressPolicy.ValidateAsync(context, stored.WorkspacePath, cancellationToken).ConfigureAwait(false);

        var payload = new SessionRequestPayload
        {
            Instance = existingBinding.InstanceId,
            Harness = existingBinding.Harness,
            Model = existingBinding.ModelId,
            Workspace = existingBinding.WorkspacePath
        };

        var responsePayload = await SendSessionRequestAsync(
                BuildUri($"api/sessions/{Uri.EscapeDataString(existingBinding.SessionKey)}/continue"),
                payload,
                authToken,
                "session continuation",
                cancellationToken,
                new(context, stored.WorkspacePath, _baseUri!.AbsoluteUri, "SessionContinue",
                    stored.SessionKey, stored.Harness, stored.ModelId, null, "") { ExpectedPolicyFingerprint = policyFingerprint },
                reservation.MarkDispatched)
            .ConfigureAwait(false);

        var binding = new MirasimSessionBinding
        {
            ProjectContext = stored.ProjectContext,
            InstanceId = existingBinding.InstanceId,
            Harness = existingBinding.Harness,
            ModelId = existingBinding.ModelId,
            RouteMode = MirasimRouteModes.Normalize(responsePayload.RouteMode ?? existingBinding.RouteMode),
            SessionKey = string.IsNullOrWhiteSpace(responsePayload.SessionKey)
                ? existingBinding.SessionKey
                : responsePayload.SessionKey,
            WorkspacePath = existingBinding.WorkspacePath
        };
        if (binding.SessionKey != stored.SessionKey)
        {
            // Continue must preserve the pinned native identity. The dispatched response cannot
            // establish that either remote session ended, so retain both capacity and uncertainty.
            _uncertainContinuations.TryAdd(stored.SessionKey, 0);
            throw new MirasimEgressPolicyException(
                "Mirasim вернул другую идентичность сессии при продолжении. Новые запросы запрещены до сверки состояния.");
        }
        if (!_localSessions.TryUpdate(binding.SessionKey, binding, stored)) throw new MirasimEgressPolicyException();
        reservation.NoAdditionalSession();
        return binding;
    }

    public Task<MirasimTurnResult> ExecuteTurnAsync(MirasimTurnRequest request, CancellationToken cancellationToken = default) =>
        _lifetime.RunAsync(token => ExecuteTurnCoreAsync(request, token), cancellationToken,
            () => PolicyRefusal() with { ErrorMessage = "Mirasim останавливается: новые запросы не принимаются." });

    private async Task<MirasimTurnResult> ExecuteTurnCoreAsync(
        MirasimTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_unsupportedTransportReason is not null) return Failure("", MirasimTurnStatus.UnsupportedTransport,
            MirasimTurnErrorClass.UnsupportedChannel, _unsupportedTransportReason, MirasimRouteModes.ManualOnly);
        if (string.IsNullOrWhiteSpace(request.SessionKey) || !_localSessions.ContainsKey(request.SessionKey)) return PolicyRefusal();
        var gate = _turnGates.GetOrAdd(request.SessionKey, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Failure("", MirasimTurnStatus.Ambiguous, MirasimTurnErrorClass.None,
                "Другой запрос этой Mirasim-сессии ещё выполняется; повторная отправка запрещена.", MirasimRouteModes.ManualOnly);
        try { return await ExecuteOwnedTurnAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { ReleaseTurnGate(request.SessionKey, gate); }
    }

    private async Task<MirasimTurnResult> ExecuteOwnedTurnAsync(MirasimTurnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_unsupportedTransportReason is not null)
        {
            return Failure(
                string.Empty,
                MirasimTurnStatus.UnsupportedTransport,
                MirasimTurnErrorClass.UnsupportedChannel,
                _unsupportedTransportReason,
                MirasimRouteModes.ManualOnly);
        }

        if (_uncertainContinuations.ContainsKey(request.SessionKey)) return PolicyRefusal();
        if (_localCleanup.ContainsKey(request.SessionKey)) return PolicyRefusal(requiresLocalCleanup: true);
        if (_executionJournal is null || _checkoutLockService is null) return PolicyRefusal();
        if (_sessionExecutions.ContainsKey(request.SessionKey)) return Failure("", MirasimTurnStatus.Ambiguous, MirasimTurnErrorClass.None,
            "Предыдущее выполнение Mirasim ещё удерживает writer lock; новый запрос запрещён до сверки состояния.", MirasimRouteModes.ManualOnly);
        string policyFingerprint;
        try { policyFingerprint = await ValidateTurnPolicyAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { return PolicyRefusal(); }
        using var capacity = ReserveExecution();
        if (capacity is null) return PolicyRefusal() with
            { ErrorMessage = "Достигнут лимит незавершённых запросов Mirasim. Сверьте их состояние перед новой отправкой." };
        MirasimExecutionAdmission entry;
        try
        {
            var binding = _localSessions[request.SessionKey];
            entry = await _executionJournal.BeginAsync(new(binding.ProjectContext!, binding.WorkspacePath, binding.SessionKey,
                binding.ModelId, binding.Harness, request.ExecutionId, request.RequestedRouteId,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt))),
                _checkoutLockService.RequiresWriterLock(request.ExecutionMode), request.ProcessGeneration), policyFingerprint, cancellationToken).ConfigureAwait(false);
            if (!_entries.TryAdd(request.ExecutionId, entry)) throw new MirasimEgressPolicyException();
            capacity.KeepAdmission();
            _executionAuthorities[request.ExecutionId] = new(_baseUri!, CredentialFingerprint(request.AuthToken));
            if (!_sessionExecutions.TryAdd(request.SessionKey, request.ExecutionId))
            {
                await TryFinishExecutionAsync(request.ExecutionId, ExecutionState.Failed, ExecutionFailureReason.WorkspaceConflict).ConfigureAwait(false);
                return PolicyRefusal();
            }
        }
        catch (Exception error) when (error is not OperationCanceledException) { return PolicyRefusal(); }

        _logger.LogInformation(
            "Dispatching Mirasim turn for execution {ExecutionId} on session {SessionKey} (harness {Harness}, model {ModelId}, mode {ExecutionMode}).",
            request.ExecutionId,
            request.SessionKey,
            request.RequestedHarness,
            request.RequestedModelId,
            request.ExecutionMode ?? "default");

        ICheckoutLockToken? lockToken = null;

        if (_checkoutLockService is not null && _checkoutLockService.RequiresWriterLock(request.ExecutionMode))
        {
            try
            {
                lockToken = await _checkoutLockService
                    .AcquireWriterLockAsync(
                        request.ProjectId,
                        request.CanonicalRootPath,
                        request.ExecutionId,
                        request.ProcessGeneration,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProjectLockConflictException exception)
            {
                if (!await TryFinishExecutionAsync(request.ExecutionId, ExecutionState.Failed, ExecutionFailureReason.WorkspaceConflict).ConfigureAwait(false))
                { _localCleanup[request.SessionKey] = request.ExecutionId; return PolicyRefusal(true); }
                RemoveSessionExecution(request.SessionKey, request.ExecutionId);
                return RefusedByLock(request, exception.Message);
            }
            catch (SecondaryInstanceReadOnlyException exception)
            {
                if (!await TryFinishExecutionAsync(request.ExecutionId, ExecutionState.Failed, ExecutionFailureReason.WorkspaceConflict).ConfigureAwait(false))
                { _localCleanup[request.SessionKey] = request.ExecutionId; return PolicyRefusal(true); }
                RemoveSessionExecution(request.SessionKey, request.ExecutionId);
                return RefusedByLock(request, exception.Message);
            }
            catch (Exception error)
            {
                var committed = await TryFinishExecutionAsync(request.ExecutionId, ExecutionState.Failed, ExecutionFailureReason.StartupFailure).ConfigureAwait(false);
                if (!committed) { _localCleanup[request.SessionKey] = request.ExecutionId; return PolicyRefusal(true); }
                RemoveSessionExecution(request.SessionKey, request.ExecutionId);
                if (error is OperationCanceledException) throw;
                return PolicyRefusal();
            }

            if (lockToken is not null)
            {
                _writerLockTokens[request.ExecutionId] = lockToken;
                _sessionExecutions[request.SessionKey] = request.ExecutionId;
            }
        }

        var expectation = new RouteExpectation(request.RequestedHarness, request.RequestedModelId,
            request.RequestedAccount, request.RequestedRouteLeg);

        MirasimTurnResult result;
        var transportAttempted = false;
        try { result = await SendTurnRequestAsync(request, policyFingerprint, cancellationToken, () => transportAttempted = true).ConfigureAwait(false); }
        catch (Exception error) when (!transportAttempted)
        {
            // This block is reached only before HttpClient.SendAsync invocation. The transport routine classifies
            // uncertainty after invocation; never reinterpret its Ambiguous result as a local refusal.
            var committed = await TryFinishExecutionAsync(request.ExecutionId, ExecutionState.Failed, ExecutionFailureReason.StartupFailure).ConfigureAwait(false);
            if (committed) await ReleaseLockForExecutionAsync(request.SessionKey, request.ExecutionId, "Mirasim local policy refused before dispatch").ConfigureAwait(false);
            var pending = !committed || _writerLockTokens.ContainsKey(request.ExecutionId);
            if (pending) _localCleanup[request.SessionKey] = request.ExecutionId;
            else RemoveSessionExecution(request.SessionKey, request.ExecutionId);
            if (error is OperationCanceledException && !pending) throw;
            return PolicyRefusal(pending);
        }
        catch (Exception)
        {
            result = Failure("", MirasimTurnStatus.Ambiguous, MirasimTurnErrorClass.Unknown,
                "Передача Mirasim могла начаться; результат не подтверждён. Повторная отправка запрещена до сверки состояния.", MirasimRouteModes.ManualOnly);
        }

        if (!string.IsNullOrWhiteSpace(result.TurnId))
        {
            _turnRouteExpectations[BuildTurnRouteKey(request.SessionKey, result.TurnId)] = expectation;
            _turnExecutions[BuildTurnRouteKey(request.SessionKey, result.TurnId)] = request.ExecutionId;
        }

        if (result.IsTerminal || result.IsAmbiguous)
        {
            var state = ResultState(result);
            if (!await TryFinishExecutionAsync(request.ExecutionId, state, ResultReason(result)).ConfigureAwait(false))
                return result with { Status = MirasimTurnStatus.Ambiguous, ErrorClass = MirasimTurnErrorClass.Unknown,
                    ErrorMessage = "Результат Mirasim получен, но durable journal commit не подтверждён; ownership сохранён до сверки состояния." };
            if (result.IsTerminal) await ReleaseLockForExecutionAsync(
                    request.SessionKey,
                    request.ExecutionId,
                    $"Mirasim turn {result.Status}")
                .ConfigureAwait(false);
            if (result.IsTerminal && !_writerLockTokens.ContainsKey(request.ExecutionId)) RemoveSessionExecution(request.SessionKey, request.ExecutionId);
            if (result.IsTerminal && _writerLockTokens.ContainsKey(request.ExecutionId))
                return result with { Status = MirasimTurnStatus.Ambiguous, ErrorClass = MirasimTurnErrorClass.Unknown,
                    ErrorMessage = "Терминальный результат сохранён, но освобождение writer lock не подтверждено. Повторите сверку состояния." };
        }
        if (result.IsTerminal && !_writerLockTokens.ContainsKey(request.ExecutionId))
            ClearRouteExpectation(request.SessionKey, result.TurnId, expectation);

        _logger.LogInformation(
            "The Mirasim turn for execution {ExecutionId} on session {SessionKey} ended with status {Status} and error class {ErrorClass}.",
            request.ExecutionId,
            request.SessionKey,
            result.Status,
            result.ErrorClass);

        return result;
    }

    public async IAsyncEnumerable<MirasimStreamEvent> WatchTurnAsync(
        string sessionKey,
        string turnId,
        string? authToken = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        if (_unsupportedTransportReason is not null)
        {
            yield return new MirasimStreamEvent
            {
                TurnId = turnId,
                EventType = "unsupported-transport",
                IsTerminal = true
            };

            yield break;
        }

        using var timeoutCts = new CancellationTokenSource(_options.TurnHardTimeout, _timeProvider);
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri($"api/sessions/{Uri.EscapeDataString(sessionKey)}/turns/{Uri.EscapeDataString(turnId)}/events"));

        ApplyAuthorization(httpRequest, authToken);

        HttpResponseMessage response;

        await RequireOwnedOperationAsync(sessionKey, turnId, authToken, requestCts.Token).ConfigureAwait(false);
        try
        {
            response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, requestCts.Token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new MirasimClientException(
                "The Mirasim turn stream could not reach the loopback host.",
                exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MirasimClientException(
                $"The Mirasim turn stream timed out after {_options.TurnHardTimeout}.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new MirasimClientException(
                    $"The Mirasim turn stream returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown"}).",
                    response.StatusCode);
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(requestCts.Token)
                .ConfigureAwait(false);

            using var reader = new StreamReader(stream);
            var boundedReader = new BoundedTextLineReader(reader, MaxStreamLineChars);

            while (!requestCts.IsCancellationRequested)
            {
                string? line;
                try { line = await boundedReader.ReadLineAsync(requestCts.Token).ConfigureAwait(false); }
                catch (InvalidDataException)
                {
                    throw new MirasimClientException("The Mirasim turn stream exceeded its permitted line size; native completion remains unconfirmed.");
                }

                if (line is null)
                {
                    yield break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var streamEvent = ParseStreamEvent(sessionKey, turnId, line);

                yield return streamEvent;

                if (streamEvent.IsTerminal)
                {
                    yield break;
                }
            }
        }
    }

    public Task<MirasimTurnResult> CancelTurnAsync(string sessionKey, string turnId, string? authToken = null,
        CancellationToken cancellationToken = default) => _lifetime.RunAsync(token => CancelTurnCoreAsync(
            sessionKey, turnId, authToken, token), cancellationToken, () => OwnedOperationRefusal(turnId));

    private async Task<MirasimTurnResult> CancelTurnCoreAsync(
        string sessionKey,
        string turnId,
        string? authToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        if (_unsupportedTransportReason is not null)
        {
            return Failure(
                turnId,
                MirasimTurnStatus.UnsupportedTransport,
                MirasimTurnErrorClass.UnsupportedChannel,
                _unsupportedTransportReason,
                MirasimRouteModes.ManualOnly);
        }

        using var timeoutCts = new CancellationTokenSource(_options.RequestTimeout, _timeProvider);
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri($"api/sessions/{Uri.EscapeDataString(sessionKey)}/turns/{Uri.EscapeDataString(turnId)}/cancel"))
        {
            Content = CreateJsonContent(new EmptyRequestPayload())
        };

        ApplyAuthorization(httpRequest, authToken);

        MirasimExecutionAdmission entry;
        try { entry = await RequireOwnedOperationAsync(sessionKey, turnId, authToken, requestCts.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { return OwnedOperationRefusal(turnId); }
        var call = await SendHttpAsync(httpRequest, cancellationToken, requestCts.Token).ConfigureAwait(false);

        if (call.IsFailure)
        {
            return Failure(
                turnId,
                MirasimTurnStatus.Ambiguous,
                call.FailureErrorClass,
                call.FailureMessage,
                MirasimRouteModes.ManualOnly);
        }

        using var response = call.Response!;

        if (!response.IsSuccessStatusCode)
        {
            var errorClass = ClassifyStatusCode(response.StatusCode) ?? MirasimTurnErrorClass.Unknown;

            return Failure(
                turnId,
                MirasimTurnStatus.Ambiguous,
                errorClass,
                $"The Mirasim host did not confirm cancellation; the cancel request returned HTTP {(int)response.StatusCode}.",
                MirasimRouteModes.ManualOnly);
        }

        var read = await ReadBodyAsync(response, cancellationToken, requestCts.Token).ConfigureAwait(false);

        if (read.Failure is not null)
        {
            return Failure(
                turnId,
                MirasimTurnStatus.Ambiguous,
                read.Failure.ErrorClass,
                read.Failure.ErrorMessage,
                MirasimRouteModes.ManualOnly);
        }

        var result = ParseCancelResponse(sessionKey, turnId, read.Body!);

        if (result.IsTerminal)
        {
            if (!await TryFinishExecutionAsync(entry.Target.ExecutionId, ResultState(result), ResultReason(result)).ConfigureAwait(false))
                return result with { Status = MirasimTurnStatus.Ambiguous, ErrorMessage = "Mirasim terminal journal commit не подтверждён; ownership сохранён." };
            await ReleaseSessionLockAsync(sessionKey, turnId, entry.Target.ExecutionId, $"Mirasim turn {result.Status} after cancellation")
                .ConfigureAwait(false);
            if (_turnExecutions.ContainsKey(BuildTurnRouteKey(sessionKey, turnId)))
                return result with { Status = MirasimTurnStatus.Ambiguous, ErrorMessage = "Mirasim writer release не подтверждён; повторите сверку состояния." };
            if (!_turnExecutions.ContainsKey(BuildTurnRouteKey(sessionKey, turnId)))
                ClearRouteExpectation(sessionKey, turnId, ResolveRouteExpectation(sessionKey, turnId));
        }

        return result;
    }

    public Task<MirasimTurnResult> ReconcileTurnAsync(string sessionKey, string turnId, string? authToken = null,
        CancellationToken cancellationToken = default) => _lifetime.RunAsync(token => ReconcileTurnCoreAsync(
            sessionKey, turnId, authToken, token), cancellationToken, () => OwnedOperationRefusal(turnId));

    private async Task<MirasimTurnResult> ReconcileTurnCoreAsync(
        string sessionKey,
        string turnId,
        string? authToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        if (string.IsNullOrWhiteSpace(turnId) && _localCleanup.TryGetValue(sessionKey, out var localExecution))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var committed = await TryFinishExecutionAsync(localExecution, ExecutionState.Failed, ExecutionFailureReason.StartupFailure).ConfigureAwait(false);
            if (committed) await ReleaseLockForExecutionAsync(sessionKey, localExecution, "Mirasim local refusal cleanup retried").ConfigureAwait(false);
            var pending = !committed || _writerLockTokens.ContainsKey(localExecution);
            if (!pending) _localCleanup.TryRemove(new KeyValuePair<string, string>(sessionKey, localExecution));
            if (!pending) RemoveSessionExecution(sessionKey, localExecution);
            return PolicyRefusal(pending);
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        if (_unsupportedTransportReason is not null)
        {
            return Failure(
                turnId,
                MirasimTurnStatus.UnsupportedTransport,
                MirasimTurnErrorClass.UnsupportedChannel,
                _unsupportedTransportReason,
                MirasimRouteModes.ManualOnly);
        }

        using var timeoutCts = new CancellationTokenSource(_options.RequestTimeout, _timeProvider);
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri($"api/sessions/{Uri.EscapeDataString(sessionKey)}/turns/{Uri.EscapeDataString(turnId)}"));

        ApplyAuthorization(httpRequest, authToken);

        MirasimExecutionAdmission entry;
        try { entry = await RequireOwnedOperationAsync(sessionKey, turnId, authToken, requestCts.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { return OwnedOperationRefusal(turnId); }
        var call = await SendHttpAsync(httpRequest, cancellationToken, requestCts.Token).ConfigureAwait(false);

        if (call.IsFailure)
        {
            return Failure(
                turnId,
                MirasimTurnStatus.Ambiguous,
                call.FailureErrorClass,
                "The Mirasim turn state could not be reconciled; the outcome remains unknown.",
                MirasimRouteModes.ManualOnly);
        }

        using var response = call.Response!;

        if (!response.IsSuccessStatusCode)
        {
            var errorClass = ClassifyStatusCode(response.StatusCode) ?? MirasimTurnErrorClass.Unknown;

            return Failure(
                turnId,
                MirasimTurnStatus.Ambiguous,
                errorClass,
                $"The Mirasim reconcile request returned HTTP {(int)response.StatusCode}; the outcome remains unknown.",
                MirasimRouteModes.ManualOnly);
        }

        var read = await ReadBodyAsync(response, cancellationToken, requestCts.Token).ConfigureAwait(false);

        if (read.Failure is not null)
        {
            return Failure(
                turnId,
                MirasimTurnStatus.Ambiguous,
                read.Failure.ErrorClass,
                "The Mirasim turn state could not be read; the outcome remains unknown.",
                MirasimRouteModes.ManualOnly);
        }

        var expectation = ResolveRouteExpectation(sessionKey, turnId);
        var result = ParseReconcileResponse(sessionKey, turnId, read.Body!, expectation);

        if (result.IsTerminal)
        {
            if (!await TryFinishExecutionAsync(entry.Target.ExecutionId, ResultState(result), ResultReason(result)).ConfigureAwait(false))
                return result with { Status = MirasimTurnStatus.Ambiguous, ErrorMessage = "Mirasim terminal journal commit не подтверждён; ownership сохранён." };
            await ReleaseSessionLockAsync(sessionKey, turnId, entry.Target.ExecutionId, $"Mirasim turn {result.Status} after reconciliation")
                .ConfigureAwait(false);
            if (_turnExecutions.ContainsKey(BuildTurnRouteKey(sessionKey, turnId)))
                return result with { Status = MirasimTurnStatus.Ambiguous, ErrorMessage = "Mirasim writer release не подтверждён; повторите сверку состояния." };
            if (!_turnExecutions.ContainsKey(BuildTurnRouteKey(sessionKey, turnId)))
                ClearRouteExpectation(sessionKey, turnId, expectation);
        }

        return result;
    }

    private async Task<MirasimTurnResult> SendTurnRequestAsync(
        MirasimTurnRequest request,
        string policyFingerprint,
        CancellationToken cancellationToken,
        Action onDispatch)
    {
        using var timeoutCts = new CancellationTokenSource(_options.TurnHardTimeout, _timeProvider);
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri($"api/sessions/{Uri.EscapeDataString(request.SessionKey)}/turns"))
        {
            Content = CreateJsonContent(new TurnRequestPayload
            {
                Prompt = request.Prompt,
                ExecutionMode = request.ExecutionMode,
                RequestedHarness = request.RequestedHarness,
                RequestedModel = request.RequestedModelId,
                RequestedAccount = request.RequestedAccount,
                RequestedRouteLeg = request.RequestedRouteLeg,
                ExecutionId = request.ExecutionId,
                ProcessGeneration = request.ProcessGeneration
            })
        };

        ApplyAuthorization(httpRequest, request.AuthToken);

        if (await ValidateTurnPolicyAsync(request, requestCts.Token).ConfigureAwait(false) != policyFingerprint) throw new MirasimEgressPolicyException();
        var binding = _localSessions[request.SessionKey];
        var body = await httpRequest.Content.ReadAsStringAsync(requestCts.Token).ConfigureAwait(false);
        await _egressPolicy!.AuthorizeAsync(new(binding.ProjectContext!, binding.WorkspacePath, _baseUri!.AbsoluteUri,
            "Turn", binding.SessionKey, binding.Harness, binding.ModelId, request.ExecutionId, body)
                { ExpectedPolicyFingerprint = policyFingerprint }, requestCts.Token).ConfigureAwait(false);
        await _executionJournal!.MarkRunningAsync(_entries[request.ExecutionId], body, requestCts.Token).ConfigureAwait(false);
        requestCts.Token.ThrowIfCancellationRequested();

        var call = await SendHttpAsync(httpRequest, cancellationToken, requestCts.Token, onDispatch,
            _options.TurnHardTimeout).ConfigureAwait(false);

        if (call.IsFailure)
        {
            return Failure(
                string.Empty,
                call.FailureStatus,
                call.FailureErrorClass,
                call.FailureMessage,
                MirasimRouteModes.ManualOnly);
        }

        using var response = call.Response!;

        var statusFailure = ClassifyStatusCode(response.StatusCode);

        if (statusFailure is not null)
        {
            return Failure(
                string.Empty,
                MirasimTurnStatus.Failed,
                statusFailure.Value,
                $"The Mirasim turn endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown"}).",
                MirasimRouteModes.ManualOnly);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Failure(string.Empty, MirasimTurnStatus.Ambiguous, MirasimTurnErrorClass.Unknown,
                $"The Mirasim turn endpoint returned HTTP {(int)response.StatusCode} without a recognized refusal; the delivered turn outcome remains unknown.",
                MirasimRouteModes.ManualOnly);
        }

        var read = await ReadBodyAsync(response, cancellationToken, requestCts.Token,
            _options.TurnHardTimeout).ConfigureAwait(false);

        return read.Failure ?? ParseTurnResponse(request, read.Body!);
    }

    private async Task<HttpCallResult> SendHttpAsync(
        HttpRequestMessage httpRequest,
        CancellationToken callerToken,
        CancellationToken requestToken,
        Action? onDispatch = null,
        TimeSpan? timeoutBudget = null)
    {
        if (callerToken.IsCancellationRequested)
            return HttpCallResult.Failure(MirasimTurnStatus.Cancelled, MirasimTurnErrorClass.None,
                "The Mirasim request was cancelled before dispatch.");
        requestToken.ThrowIfCancellationRequested();
        try
        {
            onDispatch?.Invoke();
            var response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, requestToken)
                .ConfigureAwait(false);

            return HttpCallResult.Success(response);
        }
        catch (HttpRequestException)
        {
            return HttpCallResult.Failure(
                MirasimTurnStatus.Ambiguous,
                MirasimTurnErrorClass.ConnectionDrop,
                "The Mirasim request lost its connection without a response; the outcome is unknown.");
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            return HttpCallResult.Failure(
                MirasimTurnStatus.Ambiguous,
                MirasimTurnErrorClass.Unknown,
                "The Mirasim request was cancelled without a terminal response; the outcome is unknown.");
        }
        catch (OperationCanceledException)
        {
            return HttpCallResult.Failure(
                MirasimTurnStatus.Ambiguous,
                MirasimTurnErrorClass.Timeout,
                $"The Mirasim request timed out after {timeoutBudget ?? _options.RequestTimeout} after delivery; the outcome is unknown.");
        }
    }

    private async Task<BodyReadResult> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken callerToken,
        CancellationToken requestToken,
        TimeSpan? timeoutBudget = null)
    {
        try
        {
            var body = await BoundedHttpContent.ReadTextAsync(response.Content, MaxResponseBytes, requestToken).ConfigureAwait(false);

            return new BodyReadResult(body, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            return new BodyReadResult(
                null,
                Failure(
                    string.Empty,
                    MirasimTurnStatus.Ambiguous,
                    MirasimTurnErrorClass.Unknown,
                    "The Mirasim turn was cancelled after the request was delivered; the outcome is unknown.",
                    MirasimRouteModes.ManualOnly));
        }
        catch (OperationCanceledException)
        {
            return new BodyReadResult(
                null,
                Failure(
                    string.Empty,
                    MirasimTurnStatus.Ambiguous,
                    MirasimTurnErrorClass.Timeout,
                    $"The Mirasim turn response was not received within {timeoutBudget ?? _options.RequestTimeout} after delivery; the outcome is unknown.",
                    MirasimRouteModes.ManualOnly));
        }
        catch (InvalidDataException)
        {
            return new BodyReadResult(null, Failure(string.Empty, MirasimTurnStatus.Ambiguous,
                MirasimTurnErrorClass.Unknown, "The Mirasim response exceeded its permitted size or encoding; the outcome is unknown.",
                MirasimRouteModes.ManualOnly));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new BodyReadResult(
                null,
                Failure(
                    string.Empty,
                    MirasimTurnStatus.Ambiguous,
                    MirasimTurnErrorClass.ConnectionDrop,
                    "The Mirasim response connection dropped after delivery; the outcome is unknown.",
                    MirasimRouteModes.ManualOnly));
        }
    }

    private async Task<TurnResponsePayload> SendSessionRequestAsync(
        Uri uri,
        SessionRequestPayload payload,
        string? authToken,
        string operation,
        CancellationToken cancellationToken,
        MirasimEgressOperation authorization,
        Action onDispatch)
    {
        using var timeoutCts = new CancellationTokenSource(_options.RequestTimeout, _timeProvider);
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = CreateJsonContent(payload)
        };

        ApplyAuthorization(httpRequest, authToken);

        var requestBody = await httpRequest.Content.ReadAsStringAsync(requestCts.Token).ConfigureAwait(false);
        await _egressPolicy!.AuthorizeAsync(authorization with { Body = requestBody }, requestCts.Token).ConfigureAwait(false);
        requestCts.Token.ThrowIfCancellationRequested();

        HttpResponseMessage response;

        try
        {
            onDispatch();
            response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, requestCts.Token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new MirasimClientException(
                $"The Mirasim {operation} request could not reach the loopback host.",
                exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MirasimClientException(
                $"The Mirasim {operation} request timed out after {_options.RequestTimeout}.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new MirasimClientException(
                    $"The Mirasim {operation} request returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown"}).",
                    response.StatusCode);
            }

            string body;

            try
            {
                body = await BoundedHttpContent.ReadTextAsync(response.Content, MaxResponseBytes, requestCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MirasimClientException(
                    $"The Mirasim {operation} response timed out after {_options.RequestTimeout}.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
            {
                throw new MirasimClientException(
                    $"The Mirasim {operation} response could not be read to completion.",
                    exception);
            }

            TurnResponsePayload? parsed;

            try
            {
                parsed = JsonSerializer.Deserialize<TurnResponsePayload>(body, JsonOptions);
            }
            catch (JsonException)
            {
                throw new MirasimClientException(
                    $"The Mirasim {operation} response was not a valid JSON object.");
            }

            return parsed ?? throw new MirasimClientException(
                $"The Mirasim {operation} response was not a valid JSON object.");
        }
    }

    private MirasimTurnResult ParseTurnResponse(MirasimTurnRequest request, string body)
    {
        var payload = TryDeserialize(body);

        if (payload is null)
        {
            return Failure(
                string.Empty,
                MirasimTurnStatus.Ambiguous,
                MirasimTurnErrorClass.Unknown,
                "The Mirasim turn response was not a JSON object; the outcome is unknown.",
                MirasimRouteModes.ManualOnly);
        }

        var turnId = payload.TurnId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(turnId) || !EchoedIdentityMatches(payload, request.SessionKey, null))
            return IdentityMismatch(string.Empty);
        var observed = EvaluateRoute(request.RequestedHarness, request.RequestedModelId, payload,
            request.RequestedAccount, request.RequestedRouteLeg);

        if (payload.Incomplete == true)
        {
            return BuildResult(
                turnId,
                MirasimTurnStatus.Incomplete,
                MirasimTurnErrorClass.IncompletePayload,
                "The Mirasim turn reported an incomplete payload; it is never treated as a success.",
                payload.Response,
                observed);
        }

        if (IsPhase(payload.Phase, "done"))
        {
            if (!string.IsNullOrWhiteSpace(payload.Error))
            {
                return BuildResult(
                    turnId,
                    MirasimTurnStatus.Failed,
                    MirasimTurnErrorClass.DoneWithError,
                    "The Mirasim turn reported phase 'done' with a non-empty error; it is never treated as a success.",
                    null,
                    observed);
            }

            if (observed.IsMismatch)
            {
                return BuildResult(
                    turnId,
                    MirasimTurnStatus.Failed,
                    MirasimTurnErrorClass.RouteMismatch,
                    observed.MismatchMessage,
                    null,
                    observed);
            }

            return BuildResult(
                turnId,
                MirasimTurnStatus.Completed,
                MirasimTurnErrorClass.None,
                null,
                payload.Response,
                observed);
        }

        if (IsPhase(payload.Phase, "cancelled") || IsPhase(payload.Phase, "canceled"))
        {
            return BuildResult(
                turnId,
                MirasimTurnStatus.Cancelled,
                MirasimTurnErrorClass.None,
                null,
                payload.Response,
                observed);
        }

        if (IsPhase(payload.Phase, "incomplete"))
        {
            return BuildResult(
                turnId,
                MirasimTurnStatus.Incomplete,
                MirasimTurnErrorClass.IncompletePayload,
                "The Mirasim turn reported an incomplete phase; it is never treated as a success.",
                payload.Response,
                observed);
        }

        if (IsPhase(payload.Phase, "error") || IsPhase(payload.Phase, "failed"))
        {
            return BuildResult(
                turnId,
                MirasimTurnStatus.Failed,
                MirasimTurnErrorClass.Unknown,
                "The Mirasim turn reported a failure phase.",
                null,
                observed);
        }

        if (IsPhase(payload.Phase, "running") || IsPhase(payload.Phase, "pending") || IsPhase(payload.Phase, "queued"))
        {
            return BuildResult(
                turnId,
                MirasimTurnStatus.Running,
                MirasimTurnErrorClass.Unknown,
                "The Mirasim host reported that the turn is not terminal yet.",
                null,
                observed);
        }

        return BuildResult(
            turnId,
            MirasimTurnStatus.Ambiguous,
            MirasimTurnErrorClass.Unknown,
            "The Mirasim turn response did not contain a recognized terminal phase; the outcome is unknown.",
            null,
            observed);
    }

    private static MirasimTurnResult ParseCancelResponse(string sessionKey, string turnId, string body)
    {
        var payload = TryDeserialize(body);

        if (payload is null)
        {
            return Failure(
                turnId,
                MirasimTurnStatus.Ambiguous,
                MirasimTurnErrorClass.Unknown,
                "The Mirasim cancel response was not a JSON object; the cancellation is not confirmed.",
                MirasimRouteModes.ManualOnly);
        }

        if (!EchoedIdentityMatches(payload, sessionKey, turnId))
            return IdentityMismatch(turnId);

        if (payload.Incomplete == true || IsPhase(payload.Phase, "incomplete"))
        {
            return Failure(
                turnId,
                MirasimTurnStatus.Incomplete,
                MirasimTurnErrorClass.IncompletePayload,
                "The Mirasim turn ended incomplete while cancellation was requested.",
                MirasimRouteModes.ManualOnly);
        }

        if (IsPhase(payload.Phase, "cancelled") || IsPhase(payload.Phase, "canceled") || payload.Cancelled == true)
        {
            return Failure(
                turnId,
                MirasimTurnStatus.Cancelled,
                MirasimTurnErrorClass.None,
                null,
                MirasimRouteModes.ManualOnly);
        }

        if (IsPhase(payload.Phase, "done"))
        {
            if (!string.IsNullOrWhiteSpace(payload.Error))
            {
                return Failure(
                    turnId,
                    MirasimTurnStatus.Failed,
                    MirasimTurnErrorClass.DoneWithError,
                    "The Mirasim turn completed with an error while cancellation was requested.",
                    MirasimRouteModes.ManualOnly);
            }

            return Failure(
                turnId,
                MirasimTurnStatus.Completed,
                MirasimTurnErrorClass.None,
                null,
                MirasimRouteModes.ManualOnly);
        }

        if (IsPhase(payload.Phase, "error") || IsPhase(payload.Phase, "failed"))
        {
            return Failure(
                turnId,
                MirasimTurnStatus.Failed,
                MirasimTurnErrorClass.Unknown,
                "The Mirasim turn failed while cancellation was requested.",
                MirasimRouteModes.ManualOnly);
        }

        if (IsPhase(payload.Phase, "running") ||
            IsPhase(payload.Phase, "pending") ||
            IsPhase(payload.Phase, "queued") ||
            IsPhase(payload.Phase, "cancelling") ||
            IsPhase(payload.Phase, "canceling"))
        {
            return Failure(
                turnId,
                MirasimTurnStatus.Running,
                MirasimTurnErrorClass.Unknown,
                "The Mirasim host acknowledged cancellation but reported no terminal outcome yet.",
                MirasimRouteModes.ManualOnly);
        }

        return Failure(
            turnId,
            MirasimTurnStatus.Ambiguous,
            MirasimTurnErrorClass.Unknown,
            "The Mirasim cancel response did not contain a recognized terminal outcome; cancellation is not confirmed.",
            MirasimRouteModes.ManualOnly);
    }

    private static MirasimTurnResult ParseReconcileResponse(
        string sessionKey,
        string turnId,
        string body,
        RouteExpectation? expectation)
    {
        var payload = TryDeserialize(body);

        if (payload is null)
        {
            return Failure(
                turnId,
                MirasimTurnStatus.Ambiguous,
                MirasimTurnErrorClass.Unknown,
                "The Mirasim reconcile response was not a JSON object; the outcome remains unknown.",
                MirasimRouteModes.ManualOnly);
        }

        if (!EchoedIdentityMatches(payload, sessionKey, turnId))
            return IdentityMismatch(turnId);

        var observedAccount = string.IsNullOrWhiteSpace(payload.Account) ? null : payload.Account;
        var observedLeg = string.IsNullOrWhiteSpace(payload.Leg) ? null : payload.Leg;
        var routeMode = observedAccount is null || observedLeg is null
            ? MirasimRouteModes.ManualOnly
            : MirasimRouteModes.Normalize(payload.RouteMode ?? MirasimRouteModes.Opaque);

        if (payload.Incomplete == true || IsPhase(payload.Phase, "incomplete"))
        {
            return new MirasimTurnResult
            {
                TurnId = turnId,
                Status = MirasimTurnStatus.Incomplete,
                ErrorClass = MirasimTurnErrorClass.IncompletePayload,
                ErrorMessage = "The reconciled Mirasim turn reported an incomplete payload; it is never treated as a success.",
                AssistantResponse = payload.Response,
                ObservedModel = payload.Model,
                ObservedAccount = observedAccount,
                ObservedLeg = observedLeg,
                RouteMode = routeMode
            };
        }

        if (IsPhase(payload.Phase, "done"))
        {
            if (!string.IsNullOrWhiteSpace(payload.Error))
            {
                return new MirasimTurnResult
                {
                    TurnId = turnId,
                    Status = MirasimTurnStatus.Failed,
                    ErrorClass = MirasimTurnErrorClass.DoneWithError,
                    ErrorMessage = "The reconciled Mirasim turn reported phase 'done' with a non-empty error; it is never treated as a success.",
                    ObservedModel = payload.Model,
                    ObservedAccount = observedAccount,
                    ObservedLeg = observedLeg,
                    RouteMode = routeMode
                };
            }

            if (expectation is not null)
            {
                var observedRoute = EvaluateRoute(expectation.Harness, expectation.ModelId, payload,
                    expectation.Account, expectation.Leg);

                if (observedRoute.IsMismatch)
                {
                    return BuildResult(
                        turnId,
                        MirasimTurnStatus.Failed,
                        MirasimTurnErrorClass.RouteMismatch,
                        observedRoute.MismatchMessage,
                        null,
                        observedRoute);
                }
            }

            return new MirasimTurnResult
            {
                TurnId = turnId,
                Status = MirasimTurnStatus.Completed,
                ErrorClass = MirasimTurnErrorClass.None,
                AssistantResponse = payload.Response,
                ObservedModel = payload.Model,
                ObservedAccount = observedAccount,
                ObservedLeg = observedLeg,
                RouteMode = routeMode
            };
        }

        if (IsPhase(payload.Phase, "cancelled") || IsPhase(payload.Phase, "canceled"))
        {
            return new MirasimTurnResult
            {
                TurnId = turnId,
                Status = MirasimTurnStatus.Cancelled,
                ErrorClass = MirasimTurnErrorClass.None,
                ObservedModel = payload.Model,
                ObservedAccount = observedAccount,
                ObservedLeg = observedLeg,
                RouteMode = routeMode
            };
        }

        if (IsPhase(payload.Phase, "error") || IsPhase(payload.Phase, "failed"))
        {
            return new MirasimTurnResult
            {
                TurnId = turnId,
                Status = MirasimTurnStatus.Failed,
                ErrorClass = MirasimTurnErrorClass.Unknown,
                ErrorMessage = "The reconciled Mirasim turn reported a failure phase.",
                ObservedModel = payload.Model,
                ObservedAccount = observedAccount,
                ObservedLeg = observedLeg,
                RouteMode = routeMode
            };
        }

        if (IsPhase(payload.Phase, "running") ||
            IsPhase(payload.Phase, "pending") ||
            IsPhase(payload.Phase, "queued") ||
            IsPhase(payload.Phase, "cancelling") ||
            IsPhase(payload.Phase, "canceling"))
        {
            return new MirasimTurnResult
            {
                TurnId = turnId,
                Status = MirasimTurnStatus.Running,
                ErrorClass = MirasimTurnErrorClass.Unknown,
                ErrorMessage = "The Mirasim host reported that the turn is not terminal yet.",
                ObservedModel = payload.Model,
                ObservedAccount = observedAccount,
                ObservedLeg = observedLeg,
                RouteMode = routeMode
            };
        }

        return new MirasimTurnResult
        {
            TurnId = turnId,
            Status = MirasimTurnStatus.Ambiguous,
            ErrorClass = MirasimTurnErrorClass.Unknown,
            ErrorMessage = "The Mirasim reconcile response did not contain a recognized terminal phase; the outcome remains unknown.",
            ObservedModel = payload.Model,
            ObservedAccount = observedAccount,
            ObservedLeg = observedLeg,
            RouteMode = routeMode
        };
    }

    private static MirasimStreamEvent ParseStreamEvent(string sessionKey, string turnId, string line)
    {
        string? eventType = null;
        string? delta = null;
        var terminal = false;

        try
        {
            using var document = JsonDocument.Parse(line);

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("turnId", out var echoedTurn)
                    || echoedTurn.ValueKind != JsonValueKind.String || echoedTurn.GetString() != turnId
                    || !root.TryGetProperty("sessionKey", out var echoedSession)
                    || echoedSession.ValueKind != JsonValueKind.String || echoedSession.GetString() != sessionKey)
                    return new MirasimStreamEvent { TurnId = turnId, EventType = "identity-mismatch", IsTerminal = false };

                eventType = TryGetString(root, "type") ?? TryGetString(root, "phase");
                delta = TryGetString(root, "delta");
                terminal = root.TryGetProperty("terminal", out var terminalElement) &&
                           terminalElement.ValueKind == JsonValueKind.True;
            }
        }
        catch (JsonException)
        {
            eventType = null;
        }

        return new MirasimStreamEvent
        {
            TurnId = turnId,
            EventType = eventType ?? "unknown",
            DeltaText = delta,
            IsTerminal = terminal || IsTerminalEventType(eventType),
            RawPayload = line
        };
    }

    private static RouteEvaluation EvaluateRoute(
        string? requestedHarness,
        string? requestedModelId,
        TurnResponsePayload payload,
        string? requestedAccount = null,
        string? requestedLeg = null)
    {
        var mismatch = false;
        string? mismatchMessage = null;

        if (!string.IsNullOrWhiteSpace(payload.Harness) &&
            !string.IsNullOrWhiteSpace(requestedHarness) &&
            !string.Equals(payload.Harness, requestedHarness, StringComparison.OrdinalIgnoreCase))
        {
            mismatch = true;
            mismatchMessage =
                $"The Mirasim host reported harness '{payload.Harness}' while harness '{requestedHarness}' was requested.";
        }

        if (!string.IsNullOrWhiteSpace(payload.Model) &&
            !string.IsNullOrWhiteSpace(requestedModelId) &&
            !string.Equals(payload.Model, requestedModelId, StringComparison.OrdinalIgnoreCase))
        {
            mismatch = true;
            mismatchMessage =
                $"The Mirasim host reported model '{payload.Model}' while model '{requestedModelId}' was requested.";
        }

        var observedAccount = string.IsNullOrWhiteSpace(payload.Account) ? null : payload.Account;
        var observedLeg = string.IsNullOrWhiteSpace(payload.Leg) ? null : payload.Leg;
        if ((observedAccount is not null && !string.IsNullOrWhiteSpace(requestedAccount)
                && !string.Equals(observedAccount, requestedAccount, StringComparison.Ordinal))
            || (observedLeg is not null && !string.IsNullOrWhiteSpace(requestedLeg)
                && !string.Equals(observedLeg, requestedLeg, StringComparison.Ordinal)))
        {
            mismatch = true;
            mismatchMessage = "The Mirasim host reported an account or route leg that contradicts the requested binding.";
        }
        var routeMode = observedAccount is null || observedLeg is null
            ? MirasimRouteModes.ManualOnly
            : MirasimRouteModes.Normalize(payload.RouteMode ?? MirasimRouteModes.Opaque);

        return new RouteEvaluation(
            string.IsNullOrWhiteSpace(payload.Model) ? null : payload.Model,
            observedAccount,
            observedLeg,
            routeMode,
            mismatch,
            mismatchMessage);
    }

    private RouteExpectation? ResolveRouteExpectation(string sessionKey, string turnId)
    {
        if (_turnRouteExpectations.TryGetValue(BuildTurnRouteKey(sessionKey, turnId), out var turnExpectation))
        {
            return turnExpectation;
        }

        return null;
    }

    private void ClearRouteExpectation(string sessionKey, string? turnId, RouteExpectation? expectation)
    {
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            _turnRouteExpectations.TryRemove(BuildTurnRouteKey(sessionKey, turnId), out _);
        }
    }

    private static string BuildTurnRouteKey(string sessionKey, string turnId) =>
        $"{sessionKey.Length}:{sessionKey}{turnId}";

    private async Task ReleaseLockForExecutionAsync(string sessionKey, string executionId, string reason)
    {
        await _lockReleaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_writerLockTokens.TryGetValue(executionId, out var token))
                return;
            await token.ReleaseAsync(reason, CancellationToken.None).ConfigureAwait(false);
            _writerLockTokens.TryRemove(new KeyValuePair<string, ICheckoutLockToken>(executionId, token));
            RemoveSessionExecution(sessionKey, executionId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Failed to release the Mirasim checkout writer lock for execution {ExecutionId}.",
                executionId);
        }
        finally
        {
            _lockReleaseGate.Release();
        }
    }

    private async Task ReleaseSessionLockAsync(string sessionKey, string turnId, string expectedExecutionId, string reason)
    {
        var key = BuildTurnRouteKey(sessionKey, turnId);
        if (!_turnExecutions.TryGetValue(key, out var executionId) || executionId != expectedExecutionId)
        {
            return;
        }
        await ReleaseLockForExecutionAsync(sessionKey, executionId, reason).ConfigureAwait(false);
        if (!_writerLockTokens.ContainsKey(executionId))
        {
            _turnExecutions.TryRemove(new KeyValuePair<string, string>(key, executionId));
            RemoveSessionExecution(sessionKey, executionId);
        }
    }

    private void RemoveSessionExecution(string sessionKey, string executionId)
    {
        _sessionExecutions.TryRemove(new KeyValuePair<string, string>(sessionKey, executionId));
        if (_writerLockTokens.ContainsKey(executionId)
            || _localCleanup.TryGetValue(sessionKey, out var cleanup) && cleanup == executionId) return;
        // All callers reach this path only after a durable terminal commit and successful physical release
        // (or a terminal local refusal that never acquired a writer). Uncertain ownership is never evicted.
        if (_entries.TryRemove(executionId, out _))
        {
            _executionAuthorities.TryRemove(executionId, out _);
            lock (_resourceGate) _executionReservations--;
            foreach (var turn in _turnExecutions.Where(item => item.Value == executionId))
                if (_turnExecutions.TryRemove(turn)) _turnRouteExpectations.TryRemove(turn.Key, out _);
        }
    }

    private void ReleaseTurnGate(string sessionKey, SemaphoreSlim gate)
    {
        gate.Release();
        if (!_localSessions.ContainsKey(sessionKey)) _turnGates.TryRemove(new KeyValuePair<string, SemaphoreSlim>(sessionKey, gate));
        // A caller may still reference the retired gate; removing the dictionary entry is enough for GC.
    }

    internal bool HasUnresolvedOwnership => !_entries.IsEmpty || !_sessionExecutions.IsEmpty
        || !_writerLockTokens.IsEmpty || !_localCleanup.IsEmpty || !_uncertainContinuations.IsEmpty
        || Volatile.Read(ref _uncertainSessionCreations) != 0;

    private SessionReservation ReserveSession()
    {
        lock (_resourceGate)
        {
            if (_sessionReservations >= _maxSessions) throw new MirasimEgressPolicyException(
                "Достигнут лимит отслеживаемых сессий Mirasim. Завершите или сверьте текущую работу перед перезапуском приложения.");
            _sessionReservations++; return new(this);
        }
    }

    private ExecutionReservation? ReserveExecution()
    {
        lock (_resourceGate)
        {
            if (_executionReservations >= _maxExecutions) return null;
            _executionReservations++; return new(this);
        }
    }

    private sealed class SessionReservation(MirasimSessionLifecycleService owner) : IDisposable
    {
        private bool _dispatched;
        private bool _known;
        private bool _noAdditionalSession;
        public void MarkDispatched() => _dispatched = true;
        public void KeepKnownSession() => _known = true;
        public void NoAdditionalSession() => _noAdditionalSession = true;
        public void Dispose()
        {
            lock (owner._resourceGate)
            {
                if (_known) return;
                if (!_dispatched || _noAdditionalSession) owner._sessionReservations--;
                else owner._uncertainSessionCreations++;
            }
        }
    }

    private sealed class ExecutionReservation(MirasimSessionLifecycleService owner) : IDisposable
    {
        private bool _stored;
        public void KeepAdmission() => _stored = true;
        public void Dispose()
        {
            if (!_stored) lock (owner._resourceGate) owner._executionReservations--;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.BeginShutdown();
        if (_ownsHttpClient)
            _ = _lifetime.IdleTask().ContinueWith(_ => _httpClient.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (_ownsLifetime) _lifetime.Dispose();
        // Retained native ownership and mutex tokens are never released by application disposal.
    }

    private void EnsureTransportSupported()
    {
        if (_unsupportedTransportReason is not null)
        {
            throw new MirasimClientException(_unsupportedTransportReason);
        }
    }

    private Uri BuildUri(string relativePath)
    {
        if (_baseUri is null)
        {
            throw new MirasimClientException(
                _unsupportedTransportReason ?? "The Mirasim loopback transport is unavailable.");
        }

        return new Uri(_baseUri, relativePath);
    }

    private static void ApplyAuthorization(HttpRequestMessage request, string? authToken)
    {
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authToken}");
        }
    }

    private static StringContent CreateJsonContent<T>(T payload)
    {
        return new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");
    }

    private static bool EchoedIdentityMatches(TurnResponsePayload payload, string sessionKey, string? turnId) =>
        string.Equals(payload.SessionKey, sessionKey, StringComparison.Ordinal)
        && (turnId is null || string.Equals(payload.TurnId, turnId, StringComparison.Ordinal));

    private static MirasimTurnResult IdentityMismatch(string turnId) => Failure(turnId,
        MirasimTurnStatus.Ambiguous, MirasimTurnErrorClass.Unknown,
        "The Mirasim response identity does not match the requested session or turn; the outcome remains unknown.",
        MirasimRouteModes.ManualOnly);

    private static MirasimTurnErrorClass? ClassifyStatusCode(System.Net.HttpStatusCode statusCode) =>
        (int)statusCode switch
        {
            503 => MirasimTurnErrorClass.UpstreamUnavailable503,
            422 => MirasimTurnErrorClass.PlatformBusy422,
            404 or 405 => MirasimTurnErrorClass.UnsupportedChannel,
            _ => null
        };

    private static bool IsPhase(string? phase, string expected) =>
        string.Equals(phase, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalEventType(string? eventType) =>
        eventType is not null &&
        (IsPhase(eventType, "done") ||
         IsPhase(eventType, "error") ||
         IsPhase(eventType, "failed") ||
         IsPhase(eventType, "cancelled") ||
         IsPhase(eventType, "canceled") ||
         IsPhase(eventType, "incomplete") ||
         IsPhase(eventType, "terminal"));

    private static TurnResponsePayload? TryDeserialize(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<TurnResponsePayload>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private async Task<bool> TryFinishExecutionAsync(string executionId, ExecutionState state, ExecutionFailureReason reason)
    {
        if (_executionJournal is null || !_entries.TryGetValue(executionId, out var entry)) return false;
        try { await _executionJournal.CompleteAsync(entry, state, reason, CancellationToken.None).ConfigureAwait(false); return true; }
        catch (Exception error)
        { _logger.LogWarning("Mirasim journal commit failed ({ErrorType}); ownership retained.", error.GetType().Name); return false; }
    }

    private async Task<MirasimExecutionAdmission> RequireOwnedOperationAsync(string sessionKey, string turnId,
        string? authToken, CancellationToken token)
    {
        if (_executionJournal is null || !_localSessions.TryGetValue(sessionKey, out var binding)
            || !_turnExecutions.TryGetValue(BuildTurnRouteKey(sessionKey, turnId), out var executionId)
            || !_entries.TryGetValue(executionId, out var entry)
            || entry.Target.NativeSessionId != sessionKey || entry.Target.Context != binding.ProjectContext
            || entry.Target.Harness != binding.Harness || entry.Target.NativeModelId != binding.ModelId
            || !string.Equals(ProjectLock.CanonicalizeRoot(entry.Target.RootPath),
                ProjectLock.CanonicalizeRoot(binding.WorkspacePath), StringComparison.OrdinalIgnoreCase)
            || !_executionAuthorities.TryGetValue(executionId, out var authority)
            || authority.Endpoint != _baseUri || authority.CredentialSha256 != CredentialFingerprint(authToken))
            throw new MirasimEgressPolicyException();
        try { await _executionJournal.AuthorizeOwnedOperationAsync(entry, token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { throw new MirasimEgressPolicyException(); }
        token.ThrowIfCancellationRequested();
        if (!_localSessions.TryGetValue(sessionKey, out var currentBinding) || currentBinding != binding
            || !_turnExecutions.TryGetValue(BuildTurnRouteKey(sessionKey, turnId), out var currentExecution)
            || currentExecution != executionId) throw new MirasimEgressPolicyException();
        return entry;
    }

    private static string? CredentialFingerprint(string? token) => string.IsNullOrWhiteSpace(token) ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private sealed record OwnedTransportAuthority(Uri Endpoint, string? CredentialSha256);
    private static MirasimTurnResult OwnedOperationRefusal(string turnId) => Failure(turnId,
        MirasimTurnStatus.Ambiguous, MirasimTurnErrorClass.Unknown,
        "Локальное владение Mirasim-запросом не подтверждено; операция запрещена, существующий writer lock сохранён.",
        MirasimRouteModes.ManualOnly);
    private static ExecutionState ResultState(MirasimTurnResult result) => result.Status switch
    {
        MirasimTurnStatus.Completed => ExecutionState.Succeeded, MirasimTurnStatus.Cancelled => ExecutionState.Cancelled,
        MirasimTurnStatus.Ambiguous => ExecutionState.Ambiguous, _ => ExecutionState.Failed
    };
    private static ExecutionFailureReason ResultReason(MirasimTurnResult result) => result.Status switch
    {
        MirasimTurnStatus.Completed => ExecutionFailureReason.None, MirasimTurnStatus.Cancelled => ExecutionFailureReason.UserCancelled,
        MirasimTurnStatus.Incomplete => ExecutionFailureReason.MalformedProtocol, _ => ExecutionFailureReason.InternalError
    };

    private async Task<string> ValidateTurnPolicyAsync(MirasimTurnRequest request, CancellationToken token)
    {
        if (_egressPolicy is null || !_localSessions.TryGetValue(request.SessionKey, out var binding)
            || binding.ProjectContext is not { } context || context.ProjectId != request.ProjectId
            || context.ProviderProfileId != request.ProviderProfileId || binding.Harness != request.RequestedHarness
            || binding.ModelId != request.RequestedModelId || string.IsNullOrWhiteSpace(request.ExecutionId))
            throw new MirasimEgressPolicyException();
        try
        {
            if (!Path.IsPathFullyQualified(request.CanonicalRootPath) || !string.Equals(ProjectLock.CanonicalizeRoot(binding.WorkspacePath),
                ProjectLock.CanonicalizeRoot(request.CanonicalRootPath), StringComparison.OrdinalIgnoreCase)) throw new MirasimEgressPolicyException();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new MirasimEgressPolicyException(); }
        return await _egressPolicy.ValidateAsync(context, binding.WorkspacePath, token).ConfigureAwait(false);
    }

    private static MirasimTurnResult PolicyRefusal(bool requiresLocalCleanup = false) => new()
    {
        TurnId = "", Status = MirasimTurnStatus.RefusedByPolicy, ErrorClass = MirasimTurnErrorClass.None,
        RequiresLocalCleanup = requiresLocalCleanup,
        ErrorMessage = requiresLocalCleanup ? "Локальная передача заблокирована; освобождение writer lock не подтверждено. Повторите сверку состояния."
            : "Передача Mirasim заблокирована локальной policy. Проверьте сохранённый проект, профиль и привязку сессии.",
        RouteMode = MirasimRouteModes.ManualOnly
    };

    private static MirasimTurnResult RefusedByLock(MirasimTurnRequest request, string reason)
    {
        return Failure(
            string.Empty,
            MirasimTurnStatus.RefusedByLock,
            MirasimTurnErrorClass.RefusedByWriterLock,
            $"The checkout writer lock for execution '{request.ExecutionId}' could not be acquired; " +
            $"the turn was refused before dispatch ({reason}).",
            MirasimRouteModes.ManualOnly);
    }

    private static MirasimTurnResult BuildResult(
        string turnId,
        MirasimTurnStatus status,
        MirasimTurnErrorClass errorClass,
        string? errorMessage,
        string? assistantResponse,
        RouteEvaluation observed)
    {
        return new MirasimTurnResult
        {
            TurnId = turnId,
            Status = status,
            ErrorClass = errorClass,
            ErrorMessage = errorMessage,
            AssistantResponse = assistantResponse,
            ObservedModel = observed.Model,
            ObservedAccount = observed.Account,
            ObservedLeg = observed.Leg,
            RouteMode = observed.RouteMode
        };
    }

    private static MirasimTurnResult Failure(
        string turnId,
        MirasimTurnStatus status,
        MirasimTurnErrorClass errorClass,
        string? errorMessage,
        string routeMode)
    {
        return new MirasimTurnResult
        {
            TurnId = turnId,
            Status = status,
            ErrorClass = errorClass,
            ErrorMessage = errorMessage,
            RouteMode = routeMode
        };
    }

    private readonly record struct RouteEvaluation(
        string? Model,
        string? Account,
        string? Leg,
        string RouteMode,
        bool IsMismatch,
        string? MismatchMessage);

    private sealed record RouteExpectation(string Harness, string ModelId, string? Account, string? Leg);

    private sealed record HttpCallResult(
        HttpResponseMessage? Response,
        MirasimTurnStatus FailureStatus,
        MirasimTurnErrorClass FailureErrorClass,
        string? FailureMessage)
    {
        public bool IsFailure => Response is null;

        public static HttpCallResult Success(HttpResponseMessage response) =>
            new(response, default, default, null);

        public static HttpCallResult Failure(
            MirasimTurnStatus status,
            MirasimTurnErrorClass errorClass,
            string message) =>
            new(null, status, errorClass, message);
    }

    private sealed record BodyReadResult(string? Body, MirasimTurnResult? Failure);

    private sealed class EmptyRequestPayload
    {
    }

    private sealed class SessionRequestPayload
    {
        [JsonPropertyName("instance")]
        public string? Instance { get; set; }

        [JsonPropertyName("harness")]
        public string? Harness { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("workspace")]
        public string? Workspace { get; set; }
    }

    private sealed class TurnRequestPayload
    {
        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("executionMode")]
        public string? ExecutionMode { get; set; }

        [JsonPropertyName("requestedHarness")]
        public string? RequestedHarness { get; set; }

        [JsonPropertyName("requestedModel")]
        public string? RequestedModel { get; set; }

        [JsonPropertyName("requestedAccount")]
        public string? RequestedAccount { get; set; }

        [JsonPropertyName("requestedRouteLeg")]
        public string? RequestedRouteLeg { get; set; }

        [JsonPropertyName("executionId")]
        public string? ExecutionId { get; set; }

        [JsonPropertyName("processGeneration")]
        public long ProcessGeneration { get; set; }
    }

    private sealed class TurnResponsePayload
    {
        [JsonPropertyName("turnId")]
        public string? TurnId { get; set; }

        [JsonPropertyName("sessionKey")]
        public string? SessionKey { get; set; }

        [JsonPropertyName("phase")]
        public string? Phase { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("incomplete")]
        public bool? Incomplete { get; set; }

        [JsonPropertyName("terminal")]
        public bool? Terminal { get; set; }

        [JsonPropertyName("cancelled")]
        public bool? Cancelled { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("harness")]
        public string? Harness { get; set; }

        [JsonPropertyName("account")]
        public string? Account { get; set; }

        [JsonPropertyName("leg")]
        public string? Leg { get; set; }

        [JsonPropertyName("routeMode")]
        public string? RouteMode { get; set; }
    }
}
