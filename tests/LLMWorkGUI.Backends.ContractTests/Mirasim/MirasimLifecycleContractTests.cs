using System.Net;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Mirasim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed partial class MirasimLifecycleContractTests
{
    private const string SessionKey = "sess-0001";

    private const string TurnId = "turn-0001";

    private const string Harness = "codex";

    private const string ModelId = "gpt-6-luna";

    private const string ExecutionId = "exec-0001";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalEvidenceForAnotherTurn_DoesNotReleaseTheCurrentWriter(bool cancel)
    {
        const string currentTurn = "turn-current";
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/turns", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                    Json(new { sessionKey = SessionKey, turnId = currentTurn, phase = "running", model = ModelId })));
            var turn = path.Contains(currentTurn, StringComparison.Ordinal) ? currentTurn : TurnId;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                Json(new { sessionKey = SessionKey, turnId = turn, phase = cancel ? "cancelled" : "done", terminal = true,
                    model = ModelId, response = "Synthetic terminal evidence" })));
        });
        var locks = new FakeCheckoutLockService();
        var service = CreateService(handler, locks);
        var running = await service.ExecuteTurnAsync(CreateRequest());
        Assert.Equal(currentTurn, running.TurnId);
        Assert.False(running.IsTerminal);

        var unrelated = cancel
            ? await service.CancelTurnAsync(SessionKey, TurnId)
            : await service.ReconcileTurnAsync(SessionKey, TurnId);
        Assert.Equal(MirasimTurnStatus.Ambiguous, unrelated.Status);
        Assert.False(unrelated.IsTerminal);
        Assert.Single(handler.Requests);
        Assert.Equal(0, locks.Token.ReleaseCount);
        Assert.True(locks.Token.IsHeld);

        var terminal = cancel
            ? await service.CancelTurnAsync(SessionKey, currentTurn)
            : await service.ReconcileTurnAsync(SessionKey, currentTurn);
        Assert.True(terminal.IsTerminal);
        Assert.Equal(1, locks.Token.ReleaseCount);
    }

    [Fact]
    public async Task CallerCancellationAfterDispatchBeforeHeaders_RetainsUncertainWriterOwnership()
    {
        using var cancellation = new CancellationTokenSource();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHttpMessageHandler(async (_, token) =>
        {
            delivered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable after cancellation");
        });
        var locks = new FakeCheckoutLockService();
        var service = CreateService(handler, locks);
        var execution = service.ExecuteTurnAsync(CreateRequest(), cancellation.Token);
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.False(result.IsTerminal);
        Assert.Single(handler.Requests);
        Assert.Equal(0, locks.Token.ReleaseCount);
        Assert.True(locks.Token.IsHeld);
    }

    [Fact]
    public async Task ExecuteTurn_TransmitsPromptInHttpBody_NeverInArgvOrUrlOrHeaders()
    {
        const string Prompt = "confidential prompt payload alpha-42";
        const string Token = "contract-auth-token";

        var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, DoneJson())));

        var service = CreateService(handler);

        var result = await service.ExecuteTurnAsync(CreateRequest(prompt: Prompt, authToken: Token));

        Assert.Equal(MirasimTurnStatus.Completed, result.Status);
        Assert.Equal(MirasimTurnErrorClass.None, result.ErrorClass);
        Assert.Equal("Sanitized assistant response.", result.AssistantResponse);

        var recorded = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, recorded.Method);
        Assert.Equal($"/api/sessions/{SessionKey}/turns", recorded.Path);
        Assert.NotNull(recorded.Body);
        Assert.Contains(Prompt, recorded.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Prompt, recorded.UriText, StringComparison.Ordinal);
        Assert.DoesNotContain(Prompt, recorded.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(Prompt, recorded.HeadersText, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, recorded.Body ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal($"Bearer {Token}", recorded.Headers["Authorization"]);
    }

    [Fact]
    public async Task ExecuteTurn_When503NoUpstream_NormalizesToUpstreamUnavailable503()
    {
        var fixture = File.ReadAllText(FixturePath("turn-503-upstream-sample.json"));

        var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, fixture)));

        var service = CreateService(handler);

        var result = await service.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.Failed, result.Status);
        Assert.Equal(MirasimTurnErrorClass.UpstreamUnavailable503, result.ErrorClass);
        Assert.NotEqual(MirasimTurnStatus.Completed, result.Status);
        Assert.False(result.IsAmbiguous);
    }

    [Fact]
    public async Task ExecuteTurn_When422PlatformBusy_NormalizesToPlatformBusy422()
    {
        var fixture = File.ReadAllText(FixturePath("turn-422-busy-sample.json"));

        var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.UnprocessableEntity, fixture)));

        var service = CreateService(handler);

        var result = await service.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.Failed, result.Status);
        Assert.Equal(MirasimTurnErrorClass.PlatformBusy422, result.ErrorClass);
        Assert.NotEqual(MirasimTurnStatus.Completed, result.Status);
        Assert.False(result.IsAmbiguous);
    }

    [Fact]
    public async Task ExecuteTurn_WhenPhaseDoneWithError_IsNormalizedToFailed_NeverSuccess()
    {
        var fixture = SyntheticIdentityBoundFixture("turn-done-with-error-sample.json");

        var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, fixture)));

        var lockService = new FakeCheckoutLockService();
        var service = CreateService(handler, lockService);

        var result = await service.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.Failed, result.Status);
        Assert.Equal(MirasimTurnErrorClass.DoneWithError, result.ErrorClass);
        Assert.NotEqual(MirasimTurnStatus.Completed, result.Status);
        Assert.Null(result.AssistantResponse);
        Assert.Equal(1, lockService.Token.ReleaseCount);
    }

    [Fact]
    public async Task ExecuteTurn_WhenIncompleteTrue_IsNormalizedToIncomplete_NeverSuccess()
    {
        var fixture = SyntheticIdentityBoundFixture("turn-incomplete-sample.json");

        var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, fixture)));

        var lockService = new FakeCheckoutLockService();
        var service = CreateService(handler, lockService);

        var result = await service.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.Incomplete, result.Status);
        Assert.Equal(MirasimTurnErrorClass.IncompletePayload, result.ErrorClass);
        Assert.NotEqual(MirasimTurnStatus.Completed, result.Status);
        Assert.True(result.IsTerminal);
        Assert.Equal(1, lockService.Token.ReleaseCount);
        Assert.False(lockService.Token.IsHeld);
    }

    [Fact]
    public async Task ExecuteTurn_WhenWriterLockConflict_CatchesLockExceptionsAndRefusesTurnFailClosed()
    {
        var handler = new RecordingHttpMessageHandler(
            (_, _) => throw new InvalidOperationException("No network call is permitted for a refused turn."));

        var conflict = new FakeCheckoutLockService
        {
            AcquireException = new ProjectLockConflictException("The checkout is locked by another execution.")
        };

        var service = CreateService(handler, conflict);

        var result = await service.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.RefusedByLock, result.Status);
        Assert.Equal(MirasimTurnErrorClass.RefusedByWriterLock, result.ErrorClass);
        Assert.True(result.IsTerminal);
        Assert.Empty(handler.Requests);

        var readOnly = new FakeCheckoutLockService
        {
            AcquireException = new SecondaryInstanceReadOnlyException("This instance is read-only.")
        };

        var secondService = CreateService(handler, readOnly);

        var secondResult = await secondService.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.RefusedByLock, secondResult.Status);
        Assert.Equal(MirasimTurnErrorClass.RefusedByWriterLock, secondResult.ErrorClass);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteTurn_WhenConnectionDropsAfterSend_ReturnsAmbiguous_AndRetainsLock()
    {
        var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(DroppedResponse()));

        var lockService = new FakeCheckoutLockService();
        var service = CreateService(handler, lockService);

        var result = await service.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.Equal(MirasimTurnErrorClass.ConnectionDrop, result.ErrorClass);
        Assert.True(result.IsAmbiguous);
        Assert.False(result.IsTerminal);
        Assert.Single(handler.Requests);
        Assert.Equal(0, lockService.Token.ReleaseCount);
        Assert.True(lockService.Token.IsHeld);
    }

    [Fact]
    public async Task ExecuteTurn_WhenSendTimesOut_ReturnsAmbiguous_AndRetainsLock()
    {
        var handler = new RecordingHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);

            return JsonResponse(HttpStatusCode.OK, DoneJson());
        });

        var lockService = new FakeCheckoutLockService();
        var service = CreateService(
            handler,
            lockService,
            requestTimeout: TimeSpan.FromMilliseconds(50));

        var result = await service.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.Equal(MirasimTurnErrorClass.Timeout, result.ErrorClass);
        Assert.True(result.IsAmbiguous);
        Assert.False(result.IsTerminal);
        Assert.Single(handler.Requests);
        Assert.Equal(0, lockService.Token.ReleaseCount);
        Assert.True(lockService.Token.IsHeld);
    }

    [Fact]
    public async Task CancelTurn_AwaitsTerminalEvidence_BeforeMarkingCancelled()
    {
        var nonTerminalHandler = new RecordingHttpMessageHandler(
            (request, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                Json(new { sessionKey = SessionKey, turnId = TurnId,
                    phase = request.RequestUri!.AbsolutePath.EndsWith("/turns", StringComparison.Ordinal) ? "running" : "cancelling", terminal = false }))));

        var nonTerminalService = CreateService(nonTerminalHandler);
        await nonTerminalService.ExecuteTurnAsync(CreateRequest());

        var pending = await nonTerminalService.CancelTurnAsync(SessionKey, TurnId);

        Assert.NotEqual(MirasimTurnStatus.Cancelled, pending.Status);
        Assert.False(pending.IsTerminal);
        Assert.Equal(MirasimTurnStatus.Running, pending.Status);

        var cancelFixture = SyntheticIdentityBoundFixture("turn-cancel-response-sample.json");

        var terminalHandler = new RecordingHttpMessageHandler(
            (request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal)
                    ? JsonResponse(HttpStatusCode.OK, cancelFixture)
                    : JsonResponse(HttpStatusCode.OK, Json(new { sessionKey = SessionKey, turnId = TurnId, phase = "running", model = ModelId }))));

        var lockService = new FakeCheckoutLockService();
        var terminalService = CreateService(terminalHandler, lockService);

        var running = await terminalService.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.Running, running.Status);
        Assert.Equal(0, lockService.Token.ReleaseCount);

        var cancelled = await terminalService.CancelTurnAsync(SessionKey, TurnId);

        Assert.Equal(MirasimTurnStatus.Cancelled, cancelled.Status);
        Assert.Equal(MirasimTurnErrorClass.None, cancelled.ErrorClass);
        Assert.True(cancelled.IsTerminal);
        Assert.Equal(1, lockService.Token.ReleaseCount);
        Assert.False(lockService.Token.IsHeld);

        var cancelRequest = Assert.Single(
            terminalHandler.Requests.Where(recorded => recorded.Path.EndsWith("/cancel", StringComparison.Ordinal)));

        Assert.Equal(HttpMethod.Post, cancelRequest.Method);
        Assert.Equal($"/api/sessions/{SessionKey}/turns/{TurnId}/cancel", cancelRequest.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlTurn_RefusesUnobservedTurnIdWithoutTransportOrWriterRelease(bool cancel)
    {
        const string Prompt = "prompt-that-must-never-be-resent";

        var handler = new RecordingHttpMessageHandler(
            (request, _) => Task.FromResult(
                request.Method == HttpMethod.Get
                    ? JsonResponse(HttpStatusCode.OK, DoneJson())
                    : DroppedResponse()));

        var lockService = new FakeCheckoutLockService();
        var service = CreateService(handler, lockService);

        var ambiguous = await service.ExecuteTurnAsync(CreateRequest(prompt: Prompt));

        Assert.Equal(MirasimTurnStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(0, lockService.Token.ReleaseCount);

        var reconciled = cancel ? await service.CancelTurnAsync(SessionKey, TurnId)
            : await service.ReconcileTurnAsync(SessionKey, TurnId);

        Assert.Equal(MirasimTurnStatus.Ambiguous, reconciled.Status);
        Assert.False(reconciled.IsTerminal);
        Assert.Equal(0, lockService.Token.ReleaseCount);
        Assert.True(lockService.Token.IsHeld);

        Assert.Single(handler.Requests);
        Assert.Equal(1, handler.Requests.Count(recorded => recorded.Method == HttpMethod.Post));
        Assert.Empty(handler.Requests.Where(recorded => recorded.Method == HttpMethod.Get
            || recorded.Path.EndsWith("/cancel", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ReconcileTurn_WhenObservedModelContradictsRequested_ReturnsRouteMismatch()
    {
        var handler = new RecordingHttpMessageHandler(
            (request, _) => Task.FromResult(
                request.Method == HttpMethod.Get
                    ? JsonResponse(HttpStatusCode.OK, DoneJson(model: "gpt-6-unexpected"))
                    : JsonResponse(HttpStatusCode.OK, Json(new { sessionKey = SessionKey, turnId = TurnId, phase = "running", model = ModelId }))));

        var lockService = new FakeCheckoutLockService();
        var service = CreateService(handler, lockService);

        var ambiguous = await service.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.Running, ambiguous.Status);
        Assert.Equal(0, lockService.Token.ReleaseCount);

        var reconciled = await service.ReconcileTurnAsync(SessionKey, TurnId);

        Assert.Equal(MirasimTurnStatus.Failed, reconciled.Status);
        Assert.Equal(MirasimTurnErrorClass.RouteMismatch, reconciled.ErrorClass);
        Assert.NotEqual(MirasimTurnStatus.Completed, reconciled.Status);
        Assert.Equal("gpt-6-unexpected", reconciled.ObservedModel);
        Assert.True(reconciled.IsTerminal);
        Assert.Equal(1, lockService.Token.ReleaseCount);
        Assert.False(lockService.Token.IsHeld);
    }

    [Fact]
    public async Task RouteComparison_WhenObservedContradictsRequested_ReturnsRouteMismatch_NeverCompleted()
    {
        var modelHandler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                DoneJson(model: "gpt-6-unexpected"))));

        var modelService = CreateService(modelHandler);

        var modelResult = await modelService.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.Failed, modelResult.Status);
        Assert.Equal(MirasimTurnErrorClass.RouteMismatch, modelResult.ErrorClass);
        Assert.NotEqual(MirasimTurnStatus.Completed, modelResult.Status);
        Assert.Equal("gpt-6-unexpected", modelResult.ObservedModel);

        var harnessHandler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                DoneJson(harness: "antigravity"))));

        var harnessService = CreateService(harnessHandler);

        var harnessResult = await harnessService.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.Failed, harnessResult.Status);
        Assert.Equal(MirasimTurnErrorClass.RouteMismatch, harnessResult.ErrorClass);
        Assert.NotEqual(MirasimTurnStatus.Completed, harnessResult.Status);
    }

    [Fact]
    public async Task RouteComparison_WhenObservedAccountMissing_SetsRouteModeToManualOnly()
    {
        var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                DoneJson(account: null, leg: null))));

        var service = CreateService(handler);

        var result = await service.ExecuteTurnAsync(CreateRequest());

        Assert.Equal(MirasimTurnStatus.Completed, result.Status);
        Assert.Null(result.ObservedAccount);
        Assert.Null(result.ObservedLeg);
        Assert.Equal(ModelId, result.ObservedModel);
        Assert.Equal(MirasimRouteModes.ManualOnly, result.RouteMode);
    }

    [Fact]
    public async Task TurnRequest_RedactsSensitivePromptAndTokensInLogsAndToString()
    {
        const string Prompt = "secret prompt that must never be logged";
        const string Token = "secret-token-42";

        var request = CreateRequest(prompt: Prompt, authToken: Token);
        var text = request.ToString();

        Assert.DoesNotContain(Prompt, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.Contains(MirasimTurnRequest.Redacted, text, StringComparison.Ordinal);

        var logger = new CapturingLogger<MirasimSessionLifecycleService>();

        var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.ServiceUnavailable,
                File.ReadAllText(FixturePath("turn-503-upstream-sample.json")))));

        var service = CreateService(handler, logger: logger);

        var result = await service.ExecuteTurnAsync(request);

        Assert.Equal(MirasimTurnStatus.Failed, result.Status);
        Assert.False(result.ErrorMessage?.Contains(Prompt, StringComparison.Ordinal) ?? false);
        Assert.False(result.ErrorMessage?.Contains(Token, StringComparison.Ordinal) ?? false);

        var logText = string.Join('\n', logger.Messages);

        Assert.DoesNotContain(Prompt, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, logText, StringComparison.Ordinal);

        var recorded = Assert.Single(handler.Requests);

        Assert.DoesNotContain(Prompt, recorded.HeadersText, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, recorded.UriText, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, recorded.Query, StringComparison.Ordinal);

        var throwingHandler = new RecordingHttpMessageHandler(
            (_, _) => throw new HttpRequestException(
                HttpRequestError.ConnectionError,
                $"The transport failed while carrying prompt '{Prompt}' and token '{Token}'."));

        var throwingService = CreateService(throwingHandler);

        var failed = await throwingService.ExecuteTurnAsync(request);

        Assert.NotEqual(MirasimTurnStatus.Completed, failed.Status);
        Assert.False(failed.ErrorMessage?.Contains(Prompt, StringComparison.Ordinal) ?? false);
        Assert.False(failed.ErrorMessage?.Contains(Token, StringComparison.Ordinal) ?? false);
    }

    [Fact]
    public void DiRegistration_RegistersSessionLifecycleService_Cleanly()
    {
        var services = new ServiceCollection();
        services.AddMirasimBackend();

        using var provider = services.BuildServiceProvider();

        var lifecycle = provider.GetRequiredService<IMirasimSessionLifecycleService>();

        Assert.IsType<MirasimSessionLifecycleService>(lifecycle);
        Assert.Same(lifecycle, provider.GetRequiredService<IMirasimSessionLifecycleService>());
    }

    private static MirasimSessionLifecycleService CreateService(
        HttpMessageHandler handler,
        ICheckoutLockService? checkoutLockService = null,
        ILogger<MirasimSessionLifecycleService>? logger = null,
        TimeSpan? requestTimeout = null,
        MirasimLifecycleHostedService? lifetime = null,
        TimeSpan? turnHardTimeout = null,
        TimeProvider? timeProvider = null)
    {
        var bootstrap = new FixtureBootstrapHandler(handler);
        var httpClient = new HttpClient(bootstrap)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var options = new MirasimOptions { Hostname = "127.0.0.1", Port = 4970, RequestTimeout = TimeSpan.FromSeconds(10) };
        var service = new MirasimSessionLifecycleService(
            httpClient,
            Options.Create(options),
            checkoutLockService ?? new FakeCheckoutLockService { RequiresLock = false },
            logger, timeProvider: timeProvider, egressPolicy: new FixtureMetadataPolicy(), executionJournal: new FixtureExecutionJournal(), lifetime: lifetime);
        // Protocol fixtures use an explicitly synthetic policy. Seed the actual local lifecycle
        // binding through session creation, then record only the operation under test.
        service.CreateSessionAsync("fixture-instance", Harness, ModelId, "C:\\workspace\\project-1",
            projectContext: new("project-1", "mirasim")).GetAwaiter().GetResult();
        bootstrap.IsBootstrapping = false;
        options.RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        options.TurnHardTimeout = turnHardTimeout ?? requestTimeout ?? MirasimOptions.DefaultTurnHardTimeout;
        return service;
    }

    private static MirasimTurnRequest CreateRequest(
        string prompt = "sanitized prompt",
        string? executionMode = "agent",
        string? requestedAccount = "own",
        string? requestedRouteLeg = "own",
        string? authToken = null)
    {
        return new MirasimTurnRequest
        {
            SessionKey = SessionKey,
            Prompt = prompt,
            ExecutionMode = executionMode,
            RequestedHarness = Harness,
            RequestedModelId = ModelId,
            RequestedAccount = requestedAccount,
            RequestedRouteLeg = requestedRouteLeg,
            ProjectId = "project-1",
            ProviderProfileId = "mirasim",
            CanonicalRootPath = "C:\\workspace\\project-1",
            ExecutionId = ExecutionId,
            ProcessGeneration = 7,
            AuthToken = authToken
        };
    }

    private sealed class FixtureMetadataPolicy : IMirasimEgressPolicy
    {
        public Task<string> ValidateAsync(ProjectProviderContext context, string root, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult("synthetic-contract-policy"); }
        public Task AuthorizeAsync(MirasimEgressOperation operation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
    private sealed class FixtureExecutionJournal : IMirasimExecutionJournal
    {
        public Task<MirasimExecutionAdmission> BeginAsync(MirasimExecutionTarget target, string fingerprint, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new MirasimExecutionAdmission(target, "synthetic-session", "synthetic-route", "synthetic-account", "synthetic-model", fingerprint, "synthetic-route-policy")); }
        public Task MarkRunningAsync(MirasimExecutionAdmission entry, string body, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task AuthorizeOwnedOperationAsync(MirasimExecutionAdmission entry, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task CompleteAsync(MirasimExecutionAdmission entry, ExecutionState state, ExecutionFailureReason reason, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class FixtureBootstrapHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool IsBootstrapping = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => IsBootstrapping
            ? Task.FromResult(JsonResponse(HttpStatusCode.OK, Json(new { sessionKey = SessionKey, routeMode = "manualOnly" })))
            : base.SendAsync(request, token);
    }

    private static string DoneJson(
        string? model = ModelId,
        string? harness = null,
        string? account = "own",
        string? leg = "own",
        string? response = "Sanitized assistant response.")
    {
        return Json(new
        {
            sessionKey = SessionKey,
            turnId = TurnId,
            phase = "done",
            response,
            model,
            harness,
            account,
            leg
        });
    }

    private static string Json(object payload) => JsonSerializer.Serialize(payload);

    // Normalization tests require an identity-bound response. Captured historical fixtures remain
    // unchanged; this synthetic augmentation is not evidence of native protocol support.
    private static string SyntheticIdentityBoundFixture(string name)
    {
        var payload = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FixturePath(name)))!;
        payload["sessionKey"] = SessionKey;
        payload["turnId"] = TurnId;
        return payload.ToJsonString();
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage DroppedResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new DroppingStream())
        };
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(FindRepositoryRoot(), "docs", "protocols", "mirasim", fileName);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LLMWorkGUI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root containing 'LLMWorkGUI.sln' was not found.");
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public RecordingHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            _handler = handler;
        }

        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                body,
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => string.Join(", ", header.Value),
                    StringComparer.OrdinalIgnoreCase)));

            return await _handler(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? Uri,
        string? Body,
        IReadOnlyDictionary<string, string> Headers)
    {
        public string UriText => Uri?.ToString() ?? string.Empty;

        public string Query => Uri?.Query ?? string.Empty;

        public string Path => Uri?.AbsolutePath ?? string.Empty;

        public string HeadersText =>
            string.Join('\n', Headers.Select(header => $"{header.Key}: {header.Value}"));
    }

    private sealed class DroppingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("The Mirasim response connection dropped.");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeCheckoutLockService : ICheckoutLockService
    {
        public bool RequiresLock { get; set; } = true;

        public Exception? AcquireException { get; set; }

        public FakeCheckoutLockToken Token { get; } = new();

        public bool RequiresWriterLock(string? executionMode) => RequiresLock;

        public bool RequiresWriterLock(WorkflowRole role, string? executionMode) => RequiresLock;

        public Task<ICheckoutLockToken> AcquireWriterLockAsync(
            string projectId,
            string canonicalRootPath,
            string executionId,
            long processGeneration,
            CancellationToken cancellationToken = default)
        {
            if (AcquireException is not null)
            {
                return Task.FromException<ICheckoutLockToken>(AcquireException);
            }

            return Task.FromResult<ICheckoutLockToken>(Token);
        }

        public async Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(
            string projectId,
            string canonicalRootPath,
            string executionId,
            long processGeneration,
            string? executionMode,
            WorkflowRole role = WorkflowRole.Unknown,
            CancellationToken cancellationToken = default)
        {
            if (!RequiresLock)
            {
                return null;
            }

            return await AcquireWriterLockAsync(
                    projectId,
                    canonicalRootPath,
                    executionId,
                    processGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class FakeCheckoutLockToken : ICheckoutLockToken
    {
        public string LockId { get; } = "lock-0001";

        public string ProjectId { get; } = "project-1";

        public string CanonicalRootPath { get; } = "C:\\workspace\\project-1";

        public string ExecutionId { get; } = "exec-0001";

        public string ApplicationInstanceId { get; } = "instance-0001";

        public int ReleaseCount { get; private set; }

        public bool IsHeld => ReleaseCount == 0;

        public Func<Task>? ReleaseHandler { get; set; }

        public async Task ReleaseAsync(string reason, CancellationToken cancellationToken = default)
        {
            if (ReleaseHandler is not null)
                await ReleaseHandler();
            ReleaseCount++;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
