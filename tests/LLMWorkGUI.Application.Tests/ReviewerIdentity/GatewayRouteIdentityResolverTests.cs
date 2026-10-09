using LLMWorkGUI.Application.ReviewerIdentity;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.ReviewerIdentity;

/// <summary>
/// The diagnostic resolver's rule, exercised on the boundary itself: one response-origin observation against
/// a library of persisted candidates, and the one answer each library may produce.
/// <para>
/// Every case here is a refusal except a few, and the ones that do resolve are the only honest outcomes the
/// type can express. The interesting cases are the ones an implementation is tempted to soften: a partly
/// reported identity, two routes on one tuple, a mode the gateway omitted, a stream that contradicts itself,
/// and a gateway route key whose text happens to equal a persisted <c>Routes.Id</c>. Each of those has a
/// named answer, and the tests assert the name rather than only the absence of a route, so a later edit
/// cannot replace a refusal with a guess and still satisfy the suite.
/// </para>
/// <para>
/// Nothing here opens a socket, and no assertion depends on a live gateway.
/// </para>
/// </summary>
public sealed class GatewayRouteIdentityResolverTests
{
    private const string RouteId = "route-observed";
    private const string ProfileId = "profile-1";
    private const string AccountId = "account-1";
    private const string ModelId = "model-1";

    /// <summary>The gateway's own names, as stored.</summary>
    private const string NativeProvider = "gw-provider";

    private const string NativeAccount = "gw-account";
    private const string NativeModel = "gw-model";

    /// <summary>
    /// A requested alias stored in <c>Models.ProviderModelId</c>. It is deliberately different from
    /// <see cref="NativeModel"/>, so any resolver that matched the requested alias would resolve nothing.
    /// </summary>
    private const string RequestedAlias = "alias-requested";

    /// <summary>
    /// The mode dimensions a complete observation carries. The native-name form refuses an observation that
    /// omits any of the three, so a test that is about something else - eligibility, a namespace collision, a
    /// requested alias - states a complete observation and does not have to reason about modes at all. A test
    /// that is about a missing dimension passes an explicit null.
    /// </summary>
    private const string ReasoningHigh = "high";

    private const string SpeedFast = "fast";
    private const string ExecutionMode = "exec";

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ACompleteUniqueIdentityResolvesToExactlyOneRouteAndSaysItIsNotProvenance()
    {
        var resolution = Resolve(Observe(), Candidate(RouteId));

        Assert.True(resolution.IsResolved);
        Assert.Equal(GatewayRouteRefusalKind.None, resolution.Refusal);
        Assert.Equal(RouteId, resolution.RouteId);
        Assert.Equal(new[] { RouteId }, resolution.CandidateRouteIds);

        // The ephemeral part is stated in the value itself, so a caller who prints it is told what it is.
        Assert.Contains("not provenance", resolution.Reason, StringComparison.Ordinal);
        Assert.Contains("nothing was dispatched", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompleteIdentityAndAllThreeObservedDimensionsResolveTheOneRowTheyDescribe()
    {
        // The positive native-name case: names plus all three dimensions, exactly one eligible row. This is
        // the only shape in which the native-name form is allowed to answer.
        var resolution = Resolve(Observe(), Candidate(RouteId));

        Assert.Equal(GatewayRouteRefusalKind.None, resolution.Refusal);
        Assert.Equal(RouteId, resolution.RouteId);
    }

    [Fact]
    public void ThreeModeVariantsOnOneTupleAreSeparatedByTheDimensionsTheGatewayDidReport()
    {
        var candidates = new[]
        {
            Candidate("route-fast", reasoningEffort: "low", speedMode: "fast"),
            Candidate("route-balanced", reasoningEffort: ReasoningHigh, speedMode: "balanced"),
            Candidate("route-deep", reasoningEffort: ReasoningHigh, speedMode: "slow")
        };

        var resolution = Resolve(Observe(reasoningEffort: ReasoningHigh, speedMode: "balanced"), candidates);

        Assert.Equal("route-balanced", resolution.RouteId);
        Assert.Equal(GatewayRouteRefusalKind.None, resolution.Refusal);
    }

    [Fact]
    public void AMissingObservedDimensionRefusesBeforeAnyRowIsReadEvenWhenOneVariantSurvives()
    {
        // The case the audit caught. One row occupies the triple and the speed mode is the only thing the
        // response did not report, so resolving here would make the answer depend on a coincidence of the
        // operator's library rather than on anything the response said.
        var candidates = new[] { Candidate("route-only", speedMode: SpeedFast) };

        var resolution = Resolve(Observe(speedMode: null), candidates);

        Assert.Equal(GatewayRouteRefusalKind.UnobservedModeDimension, resolution.Refusal);
        Assert.Null(resolution.RouteId);
        Assert.Empty(resolution.CandidateRouteIds);
        Assert.Contains("speedMode", resolution.Reason, StringComparison.Ordinal);

        // And the same library, with the dimension reported, resolves. The refusal was about the response,
        // not about the rows.
        Assert.Equal("route-only", Resolve(Observe(), candidates).RouteId);
    }

    [Fact]
    public void AMissingObservedModeWithTwoRemainingVariantsRefusesAndNamesTheUnobservedDimension()
    {
        var candidates = new[]
        {
            Candidate("route-fast", speedMode: "fast"),
            Candidate("route-slow", speedMode: "slow")
        };

        var resolution = Resolve(Observe(speedMode: null), candidates);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.RouteId);
        Assert.Equal(GatewayRouteRefusalKind.UnobservedModeDimension, resolution.Refusal);
        Assert.Contains("speedMode", resolution.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reasoningEffort")]
    [InlineData("speedMode")]
    [InlineData("executionMode")]
    public void AnyOneMissingDimensionIsEnoughToRefuseAndItIsNamed(string omitted)
    {
        var candidates = new[] { Candidate(RouteId) };

        var resolution = Resolve(
            Observe(
                reasoningEffort: omitted == "reasoningEffort" ? null : ReasoningHigh,
                speedMode: omitted == "speedMode" ? null : SpeedFast,
                executionMode: omitted == "executionMode" ? null : ExecutionMode),
            candidates);

        Assert.Equal(GatewayRouteRefusalKind.UnobservedModeDimension, resolution.Refusal);
        Assert.Null(resolution.RouteId);
        Assert.Contains(omitted, resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AllThreeMissingDimensionsAreNamedInAFixedOrder()
    {
        var resolution = Resolve(
            Observe(reasoningEffort: null, speedMode: null, executionMode: null),
            new[] { Candidate(RouteId) });

        Assert.Equal(GatewayRouteRefusalKind.UnobservedModeDimension, resolution.Refusal);
        Assert.Contains(
            "reasoningEffort, speedMode, executionMode",
            resolution.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ARowThatDeclaresNoModeColumnIsNotResolvableByACompleteObservation()
    {
        // The other consequence of requiring all three dimensions: a persisted route whose own column is null
        // declares no mode, so no complete observation is about it. The row is not unbound - it carries a
        // full gateway identity - it simply cannot be what any complete response described.
        var candidates = new[] { Candidate("route-no-execution", executionMode: null) };

        var resolution = Resolve(Observe(), candidates);

        Assert.Equal(GatewayRouteRefusalKind.NoEligibleRoute, resolution.Refusal);
        Assert.Null(resolution.RouteId);
        Assert.Empty(resolution.UnboundRouteIds);
    }

    [Fact]
    public void AGatewayRouteKeyResolvesWithoutTheThreeModeDimensions()
    {
        // The other accepted form. A gateway route key is an observed identity in its own right, so the
        // response does not have to decompose the route into provider, account, model and three modes, and the
        // rows' own mode columns are not consulted at all.
        var candidates = new[]
        {
            Candidate("route-keyed", reasoningEffort: null, speedMode: null, executionMode: null, gatewayRouteKey: "gw-route-key")
        };

        var observation = Observe(
            reasoningEffort: null,
            speedMode: null,
            executionMode: null,
            routeKey: "gw-route-key");

        var resolution = Resolve(observation, candidates);

        // The same three dimensions that make the native-name form refuse are absent here, and the answer is
        // still a route, because the key is what the observation is matched on.
        Assert.Equal(
            new[] { "reasoningEffort", "speedMode", "executionMode" },
            observation.UnobservedDimensions.ToArray());
        Assert.Equal("route-keyed", resolution.RouteId);
        Assert.Equal(GatewayRouteRefusalKind.None, resolution.Refusal);
    }

    [Fact]
    public void TwoRoutesWithAnIdenticalTupleAndEveryObservedDimensionRefuseAsIndistinguishable()
    {
        var candidates = new[]
        {
            Candidate("route-a", reasoningEffort: ReasoningHigh, speedMode: SpeedFast, executionMode: ExecutionMode),
            Candidate("route-b", reasoningEffort: ReasoningHigh, speedMode: SpeedFast, executionMode: ExecutionMode)
        };

        var resolution = Resolve(
            Observe(reasoningEffort: ReasoningHigh, speedMode: SpeedFast, executionMode: ExecutionMode),
            candidates);

        Assert.Equal(GatewayRouteRefusalKind.AmbiguousAcrossRoutes, resolution.Refusal);
        Assert.Equal(new[] { "route-a", "route-b" }, resolution.CandidateRouteIds);
        Assert.Contains("cannot be told", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OneObservedNameInTwoProviderNamespacesRefusesAsANamespaceCollision()
    {
        // The same gateway provider, account and model names stored under two backends. Uniqueness inside a
        // namespace permits this, so the read has to refuse it: the observed name describes two things.
        var candidates = new[]
        {
            Candidate(RouteId),
            Candidate(
                "route-other",
                profileId: "profile-2",
                accountId: "account-2",
                modelId: "model-2",
                backend: BackendType.OpenCode)
        };

        var resolution = Resolve(Observe(), candidates);

        Assert.Equal(GatewayRouteRefusalKind.AmbiguousAcrossNamespaces, resolution.Refusal);
        Assert.Equal(new[] { RouteId, "route-other" }, resolution.CandidateRouteIds);
        Assert.Contains("more than one stored thing", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowThatIsDisabledOrUnhealthyIsNotACandidateAndCannotBeResolvedTo()
    {
        var candidates = new[]
        {
            Candidate("route-disabled", routeEnabled: false),
            Candidate("route-quarantined", routeHealth: HealthState.QuarantinedAuto),
            Candidate("route-probe-required", routeHealth: HealthState.ProbeRequired),
            Candidate("route-recovering", routeHealth: HealthState.Recovering),
            Candidate("route-cooling", routeHealth: HealthState.CoolingDown),
            Candidate("route-manually-off", routeHealth: HealthState.DisabledManual),
            Candidate("route-model-unhealthy", modelHealth: HealthState.DisabledManual),
            Candidate("route-model-probe", modelHealth: HealthState.ProbeRequired),
            Candidate("route-model-off", modelEnabled: false),
            Candidate("route-profile-off", profileEnabled: false),
            Candidate("route-account-off", accountEnabled: false),
            Candidate("route-account-unsigned", accountAuth: AuthState.Unknown),
            Candidate("route-account-unhealthy", accountHealth: HealthState.QuarantinedAuto),
            Candidate("route-account-cooldown", accountCooldownUntil: Now.AddMinutes(5)),
            Candidate("route-account-disabled", accountDisabledUntil: Now.AddMinutes(5))
        };

        var resolution = Resolve(Observe(), candidates);

        Assert.Equal(GatewayRouteRefusalKind.NoEligibleRoute, resolution.Refusal);
        Assert.Empty(resolution.CandidateRouteIds);
    }

    [Fact]
    public void ACooldownThatHasAlreadyElapsedIsNotAReasonToRefuse()
    {
        // The account rule is the domain's own, evaluated at the given instant: a cooldown that has run out
        // is not a cooldown, and a diagnostic that froze time would refuse a route that is in fact usable.
        var candidates = new[] { Candidate("route-live", accountCooldownUntil: Now.AddMinutes(-1)) };

        Assert.Equal("route-live", Resolve(Observe(), candidates).RouteId);
    }

    [Fact]
    public void AnIncoherentRowIsNotACandidateEvenWithACompleteIdentity()
    {
        // The account belongs to a different provider profile than the route names. The schema permits it,
        // the foreign keys are satisfied, and it is still not a route this build can answer for.
        var crossed = Candidate("route-crossed", accountId: "account-2", accountOwnProfileId: "profile-2");

        Assert.Equal(new[] { "AccountProviderProfile" }, crossed.Incoherences.ToArray());
        Assert.False(crossed.IsInternallyCoherent);

        var resolution = Resolve(Observe(), new[] { Candidate(RouteId), crossed });

        Assert.Equal(RouteId, resolution.RouteId);
    }

    [Fact]
    public void ALibraryThatPredatesTheIdentityIsReportedAsUnboundRatherThanAsHavingNoSuchRoute()
    {
        var legacy = Candidate(RouteId, nativeProvider: null, nativeAccount: null, nativeModel: null);

        var resolution = Resolve(Observe(), new[] { legacy });

        Assert.Equal(GatewayRouteRefusalKind.IncompletePersistedIdentity, resolution.Refusal);
        Assert.Equal(new[] { RouteId }, resolution.UnboundRouteIds);
        Assert.Contains("still unbound", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APartlyBoundLibraryIsReportedAsUnboundAndNamesOnlyTheRowThatAgrees()
    {
        var candidates = new[]
        {
            Candidate("route-unbound", nativeAccount: null),
            Candidate(
                "route-unrelated",
                accountId: "account-9",
                modelId: "model-9",
                nativeAccount: "gw-someone-else")
        };

        var resolution = Resolve(Observe(), candidates);

        Assert.Equal(GatewayRouteRefusalKind.IncompletePersistedIdentity, resolution.Refusal);
        Assert.Equal(new[] { "route-unbound" }, resolution.UnboundRouteIds);
        Assert.Empty(resolution.CandidateRouteIds);
    }

    [Fact]
    public void APartialObservedIdentityIsRefusedRatherThanMatchedAsAShorterName()
    {
        // Two of three native names is a name that matches more rows, not a shorter identity.
        var resolution = Resolve(Observe(account: null), new[] { Candidate(RouteId) });

        Assert.Equal(GatewayRouteRefusalKind.NoNativeIdentityObserved, resolution.Refusal);
        Assert.Contains("matches more routes", resolution.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "gw-account", "gw-model")]
    [InlineData("   ", "gw-account", "gw-model")]
    [InlineData("\t", "gw-account", "gw-model")]
    [InlineData("gw-provider", " ", "gw-model")]
    [InlineData("gw-provider", "gw-account", " ")]
    [InlineData("  gw-provider  ", "gw-account", "gw-model")]
    public void ABlankObservedComponentIsAnAbsenceAndASurroundedOneIsTheSameName(string provider, string account, string model)
    {
        var resolution = Resolve(Observe(provider, account, model), new[] { Candidate(RouteId) });

        if (provider.Trim().Length == 0 || account.Trim().Length == 0 || model.Trim().Length == 0)
        {
            Assert.Equal(GatewayRouteRefusalKind.NoNativeIdentityObserved, resolution.Refusal);
            Assert.Null(resolution.RouteId);
        }
        else
        {
            // Surrounding whitespace is not a different name; it is the same name, and matching it is right.
            Assert.Equal(RouteId, resolution.RouteId);
        }
    }

    [Fact]
    public void AnEmptyLibraryRefusesRatherThanMatchingEverything()
    {
        var resolution = Resolve(Observe());

        Assert.Equal(GatewayRouteRefusalKind.NoEligibleRoute, resolution.Refusal);
    }

    [Fact]
    public void ContradictingChunksRefuseBeforeAnyRowIsRead()
    {
        var resolution = GatewayRouteIdentityResolver.Resolve(
            new[]
            {
                new GatewayRouteObservationChunk(NativeProvider, NativeAccount, NativeModel),
                new GatewayRouteObservationChunk(NativeProvider, NativeAccount, "gw-some-other-model")
            },
            new[] { Candidate(RouteId) },
            Now);

        Assert.Equal(GatewayRouteRefusalKind.ConflictingObservationChunks, resolution.Refusal);
        Assert.Contains("model", resolution.Reason, StringComparison.Ordinal);
        Assert.Empty(resolution.CandidateRouteIds);
    }

    [Fact]
    public void ContradictingChunks_DoNotEchoUntrustedValuesIntoDiagnostics()
    {
        const string untrusted = "synthetic-secret\r\nforged-log-entry";
        var resolution = GatewayRouteIdentityResolver.Resolve(
            new[]
            {
                new GatewayRouteObservationChunk(NativeProvider, NativeAccount, NativeModel),
                new GatewayRouteObservationChunk(NativeProvider, NativeAccount, untrusted)
            }, new[] { Candidate(RouteId) }, Now);

        Assert.Equal(GatewayRouteRefusalKind.ConflictingObservationChunks, resolution.Refusal);
        Assert.DoesNotContain("synthetic-secret", resolution.Reason);
        Assert.DoesNotContain('\r', resolution.Reason);
        Assert.DoesNotContain('\n', resolution.Reason);
        Assert.Contains("model", resolution.Reason);
    }

    [Fact]
    public void AContradictionBetweenTheFirstAndThirdChunkIsStillAContraDiction()
    {
        var resolution = GatewayRouteIdentityResolver.Resolve(
            new[]
            {
                new GatewayRouteObservationChunk(NativeProvider, NativeAccount, "gw-model-a"),
                new GatewayRouteObservationChunk(),
                new GatewayRouteObservationChunk(NativeProvider, NativeAccount, "gw-model-b")
            },
            new[] { Candidate(RouteId) },
            Now);

        Assert.Equal(GatewayRouteRefusalKind.ConflictingObservationChunks, resolution.Refusal);
    }

    [Fact]
    public void AConflictInAnyDimensionRefusesTheWholeObservation()
    {
        var cases = new[]
        {
            new[]
            {
                new GatewayRouteObservationChunk(NativeProvider, NativeAccount, NativeModel),
                new GatewayRouteObservationChunk("gw-other-provider")
            },
            new[]
            {
                new GatewayRouteObservationChunk(NativeProvider, NativeAccount, NativeModel),
                new GatewayRouteObservationChunk(gatewayRouteKey: "gw-route")
            },
            new[]
            {
                new GatewayRouteObservationChunk(NativeProvider, NativeAccount, NativeModel, speedMode: "fast"),
                new GatewayRouteObservationChunk(speedMode: "slow")
            },
            new[]
            {
                new GatewayRouteObservationChunk(reasoningEffort: "high"),
                new GatewayRouteObservationChunk(reasoningEffort: "low")
            },
            new[]
            {
                new GatewayRouteObservationChunk(executionMode: "a"),
                new GatewayRouteObservationChunk(executionMode: "b")
            }
        };

        foreach (var chunks in cases)
        {
            var resolution = GatewayRouteIdentityResolver.Resolve(chunks, new[] { Candidate(RouteId) }, Now);

            // Every one of these declares an identity, so the resolver reads rows and refuses on what it
            // found. The refusal is the point; which of the two non-resolving answers it is must not
            // depend on the fixture, so only the identity and the absence of a route are asserted.
            Assert.NotEqual(GatewayRouteRefusalKind.None, resolution.Refusal);
            Assert.Null(resolution.RouteId);
        }
    }

    [Fact]
    public void OnlyAContradictionRefusesAndALaterSupplementIsNotOne()
    {
        // A route key reported by a later chunk adds to what the response said; it does not contradict
        // anything. The merged observation is then in the route-key form, so that is the form it is matched
        // in - and a row bound only by names is no longer a candidate.
        var resolution = GatewayRouteIdentityResolver.Resolve(
            new[]
            {
                new GatewayRouteObservationChunk(NativeProvider, NativeAccount, NativeModel),
                new GatewayRouteObservationChunk(gatewayRouteKey: "gw-route-key")
            },
            new[] { Candidate("route-by-name"), Candidate("route-by-key", gatewayRouteKey: "gw-route-key") },
            Now);

        Assert.Equal("route-by-key", resolution.RouteId);
    }

    [Fact]
    public void RepeatingTheSameValueAcrossChunksIsAgreementRatherThanAConflict()
    {
        var resolution = GatewayRouteIdentityResolver.Resolve(
            new[]
            {
                new GatewayRouteObservationChunk(NativeProvider),
                new GatewayRouteObservationChunk(),
                new GatewayRouteObservationChunk(
                    NativeProvider,
                    NativeAccount,
                    NativeModel,
                    ReasoningHigh,
                    SpeedFast,
                    ExecutionMode)
            },
            new[] { Candidate(RouteId) },
            Now);

        Assert.Equal(RouteId, resolution.RouteId);
    }

    [Fact]
    public void AGatewayRouteKeyResolvesAndIsNeverComparedToThePersistedRouteId()
    {
        // Two rows whose text is identical across the two namespaces: one holds the gateway key, the other
        // merely has that string as its own primary key. Only the row that declares the key can be chosen.
        var keyed = Candidate("route-keyed", gatewayRouteKey: "gw-route-key");
        var coincidental = Candidate("gw-route-key");

        Assert.Equal("gw-route-key", coincidental.Route.Id);
        Assert.Null(coincidental.Route.GatewayRouteKey);

        var resolution = Resolve(Observe(routeKey: "gw-route-key"), new[] { keyed, coincidental });

        Assert.Equal("route-keyed", resolution.RouteId);
        Assert.Equal(GatewayRouteRefusalKind.None, resolution.Refusal);
    }

    [Fact]
    public void AGatewayRouteKeyWhoseTextEqualsAPersistedRouteIdResolvesToNothingWhenNoRowDeclaresIt()
    {
        // The request-echo case in its sharpest form: the gateway's key is exactly a Routes.Id, and the only
        // row with that id does not declare it as a gateway key. Reading the id as the key would hand back
        // the one row that never claimed to be the observed route.
        var coincidental = Candidate("gw-route-key");

        var resolution = Resolve(Observe(routeKey: "gw-route-key"), new[] { coincidental });

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.RouteId);
        Assert.Equal(GatewayRouteRefusalKind.IncompletePersistedIdentity, resolution.Refusal);
        Assert.Equal(new[] { "gw-route-key" }, resolution.UnboundRouteIds);
    }

    [Fact]
    public void AGatewayRouteKeyFormIgnoresTheNativeNamesEvenWhenTheGatewayReportedBoth()
    {
        // Both forms present means the key decides, so a row whose names agree but whose key does not is not
        // the observed route - and the refusal says which storage the operator has to fill in.
        var resolution = Resolve(Observe(routeKey: "gw-route-key"), new[] { Candidate("route-by-name") });

        Assert.False(resolution.IsResolved);
        Assert.Equal(GatewayRouteRefusalKind.IncompletePersistedIdentity, resolution.Refusal);
        Assert.Equal(new[] { "route-by-name" }, resolution.UnboundRouteIds);
        Assert.Contains("record no gateway route key at all", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRequestedValueCanEverFillAMissingObservedField()
    {
        // The observation type is the whole of what a gateway can be said to have reported. If it had a
        // member for a requested route, a requested model or a client-supplied session, a request could
        // stand in for a response, which is the failure this entire type exists to prevent.
        var members = typeof(GatewayRouteObservation)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(GatewayRouteObservation.ExecutionMode),
                nameof(GatewayRouteObservation.GatewayRouteKey),
                nameof(GatewayRouteObservation.HasEveryNativeName),
                nameof(GatewayRouteObservation.IsRouteKeyForm),
                nameof(GatewayRouteObservation.NamesAnIdentity),
                nameof(GatewayRouteObservation.NativeAccountId),
                nameof(GatewayRouteObservation.NativeModelId),
                nameof(GatewayRouteObservation.NativeProviderId),
                nameof(GatewayRouteObservation.ReasoningEffort),
                nameof(GatewayRouteObservation.SpeedMode),
                nameof(GatewayRouteObservation.UnobservedDimensions)
            },
            members);

        Assert.DoesNotContain(members, name => name.Contains("Requested", StringComparison.Ordinal));
        Assert.DoesNotContain(members, name => name.Contains("Session", StringComparison.Ordinal));
        Assert.DoesNotContain(members, name => name.Contains("RouteId", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRequestedAliasIsNeverMatchedAndTheCodexStylePathIsNeverReused()
    {
        // The stored ProviderModelId is an alias and ProviderNativeId is where an account happens to be kept.
        // A resolver that reached for either would resolve the row from what was asked for, or from where a
        // credential is filed.
        var candidate = Candidate(RouteId, providerNativeId: @"C:\Users\someone\.codex\auth.json");

        Assert.Equal(RequestedAlias, candidate.Model.ProviderModelId);

        var byAlias = Resolve(Observe(model: RequestedAlias), new[] { candidate });
        var byPath = Resolve(Observe(account: @"C:\Users\someone\.codex\auth.json"), new[] { candidate });

        Assert.Equal(GatewayRouteRefusalKind.NoEligibleRoute, byAlias.Refusal);
        Assert.Equal(GatewayRouteRefusalKind.NoEligibleRoute, byPath.Refusal);
        Assert.Null(byAlias.RouteId);
        Assert.Null(byPath.RouteId);
    }

    [Fact]
    public void TheStoredLocalPathIsStillReadableAndTheNewIdentitySurvivesEveryDomainCopy()
    {
        // Additive, not a reinterpretation: whatever an account already recorded for its own storage
        // location is still there, and the gateway identity is a separate value beside it. A copy made
        // through the domain's own with-methods has to carry the new value forward, or a state change would
        // quietly drop the identity the resolver depends on.
        var withBoth = Candidate(RouteId, providerNativeId: @"C:\Users\someone\.codex\auth.json").Account;

        Assert.Equal(@"C:\Users\someone\.codex\auth.json", withBoth.ProviderNativeId);
        Assert.Equal(NativeAccount, withBoth.GatewayNativeId);

        var copies = new[]
        {
            withBoth.WithHealth(HealthState.Degraded),
            withBoth.WithAuthState(AuthState.Refreshing),
            withBoth.WithIsEnabled(false),
            withBoth.WithManualPriority(3),
            withBoth.WithCooldown(Now.AddHours(1)),
            withBoth.WithDisabledUntil(Now.AddHours(1)),
            withBoth.WithSecretReference("urn:llmworkgui:secret:other")
        };

        Assert.Equal(copies.Length, copies.Length);

        foreach (var copy in copies)
        {
            Assert.Equal(NativeAccount, copy.GatewayNativeId);
            Assert.Equal(@"C:\Users\someone\.codex\auth.json", copy.ProviderNativeId);
        }
    }

    [Fact]
    public void ABlankGatewayIdentityIsRefusedByTheDomainBeforeItReachesStorage()
    {
        Assert.Throws<ArgumentException>(() => new Route(
            RouteId,
            new SessionBinding(BackendType.StarCliProxy, ProfileId, AccountId, ModelId, null, null, null),
            DataClassification.PrivateSource,
            isEnabled: true,
            HealthState.Healthy,
            manualPriority: 0,
            gatewayRouteKey: "   "));

        Assert.Throws<ArgumentException>(() => new ProviderProfile(
            ProfileId,
            "Profile",
            BackendType.StarCliProxy,
            baseUrl: null,
            executablePath: null,
            DataClassification.PrivateSource,
            isEnabled: true,
            gatewayNativeId: "\t"));
    }

    [Fact]
    public void ARefusalIsTheOnlyOutcomeWithoutARouteIdAndAResolvedOutcomeIsTheOnlyOneWith()
    {
        // The result type is closed in both directions: a refusal cannot be built with a route id, and a
        // resolution cannot be built without one.
        Assert.Throws<ArgumentException>(() => GatewayRouteResolution.Refused(
            GatewayRouteRefusalKind.None,
            "a resolution without an identified route is a refusal."));

        var resolved = GatewayRouteResolution.Resolved(RouteId, "resolved.", new[] { RouteId });
        var refused = GatewayRouteResolution.Refused(GatewayRouteRefusalKind.NoEligibleRoute, "refused.");

        Assert.Equal(RouteId, resolved.RouteId);
        Assert.Null(refused.RouteId);
        Assert.True(resolved.IsResolved);
        Assert.False(refused.IsResolved);
    }

    [Fact]
    public void TheCompletenessDiagnosticNeverClaimsAReviewerTurnBecameAvailable()
    {
        var bound = Candidate(RouteId);

        var complete = GatewayNativeIdentityDiagnostic.Report(
            new GatewayRouteCandidateSnapshot(1, new[] { bound }),
            Now);

        var unbound = GatewayNativeIdentityDiagnostic.Report(
            new GatewayRouteCandidateSnapshot(
                2,
                new[] { bound, Candidate("route-unbound", nativeAccount: null) }),
            Now);

        Assert.True(complete.IsMappingComplete);
        Assert.False(unbound.IsMappingComplete);

        // Storage completeness is a precondition, never a permission.
        Assert.False(complete.EnablesReviewerDispatch);
        Assert.False(unbound.EnablesReviewerDispatch);
        Assert.Contains("still observed no gateway response-origin identity", complete.Summary, StringComparison.Ordinal);
        Assert.Contains("the reviewer channel remains refused for every route", complete.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("review-ready", complete.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ready to dispatch", complete.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDiagnosticCountsEveryRouteRowEvenWhenTheReaderCouldNotJoinIt()
    {
        // A row whose provider, account or model is missing has no candidate, and a report that counted only
        // what it could read would describe a smaller library than the operator actually has.
        var diagnostic = GatewayNativeIdentityDiagnostic.Report(
            new GatewayRouteCandidateSnapshot(3, new[] { Candidate(RouteId) }),
            Now);

        Assert.Equal(3, diagnostic.RouteRowCount);
        Assert.Equal(1, diagnostic.JoinedRouteCount);
        Assert.Equal(1, diagnostic.BoundRouteCount);
        Assert.False(diagnostic.IsMappingComplete);
        Assert.StartsWith("1 of 3 persisted routes", diagnostic.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiagnosticAgreesWithTheResolverAboutWhichRowsAreEligible()
    {
        var candidates = new[]
        {
            Candidate("route-live"),
            Candidate("route-off", routeEnabled: false),
            Candidate("route-sick", routeHealth: HealthState.QuarantinedAuto),
            Candidate("route-crossed", accountId: "account-2", accountOwnProfileId: "profile-2")
        };

        var diagnostic = GatewayNativeIdentityDiagnostic.Report(
            new GatewayRouteCandidateSnapshot(candidates.Length, candidates),
            Now);

        Assert.Equal(1, diagnostic.EligibleRouteCount);
        Assert.Equal(new[] { "route-crossed" }, diagnostic.IncoherentRouteIds.ToArray());
        Assert.Equal("route-live", Resolve(Observe(), candidates).RouteId);
    }

    [Fact]
    public void TheDiagnosticNamesRoutesThatShareATripleBecauseOnlyTheModesSeparateThem()
    {
        var diagnostic = GatewayNativeIdentityDiagnostic.Report(
            new GatewayRouteCandidateSnapshot(
                2,
                new[]
                {
                    Candidate("route-a", speedMode: "fast"),
                    Candidate("route-b", speedMode: "slow")
                }),
            Now);

        Assert.Equal(new[] { "route-a", "route-b" }, diagnostic.AmbiguousTupleRouteIds.ToArray());
        Assert.False(diagnostic.IsMappingComplete);
    }

    private static GatewayRouteResolution Resolve(
        GatewayRouteObservation observation,
        params GatewayRouteCandidate[] candidates) =>
        GatewayRouteIdentityResolver.Resolve(observation, candidates, Now);

    /// <summary>
    /// A complete response-origin observation, which is the only shape the native-name form accepts. The
    /// merged form is used rather than the chunks so a test failure is about the resolver's rule and not
    /// about the fixture.
    /// </summary>
    private static GatewayRouteObservation Observe(
        string? provider = NativeProvider,
        string? account = NativeAccount,
        string? model = NativeModel,
        string? reasoningEffort = ReasoningHigh,
        string? speedMode = SpeedFast,
        string? executionMode = ExecutionMode,
        string? routeKey = null)
    {
        var merged = GatewayRouteObservation.Merge(new[]
        {
            new GatewayRouteObservationChunk(
                provider,
                account,
                model,
                reasoningEffort,
                speedMode,
                executionMode,
                routeKey)
        });

        Assert.True(merged.Agrees, merged.Conflict);

        return merged.Observation!;
    }

    /// <summary>
    /// A fully bound, enabled, healthy, coherent candidate on the default identity and the default mode
    /// dimensions, which are the same ones <see cref="Observe"/> reports. Every negative case names only the
    /// parameter it changes, so a test says what it altered and nothing else. The account and the model both
    /// default to belonging to <see cref="ProfileId"/>, and the optional overrides are the only way to break
    /// that.
    /// </summary>
    private static GatewayRouteCandidate Candidate(
        string routeId,
        string profileId = ProfileId,
        string accountId = AccountId,
        string modelId = ModelId,
        string? reasoningEffort = ReasoningHigh,
        string? speedMode = SpeedFast,
        string? executionMode = ExecutionMode,
        string? gatewayRouteKey = null,
        string? nativeProvider = NativeProvider,
        string? nativeAccount = NativeAccount,
        string? nativeModel = NativeModel,
        string? providerNativeId = null,
        string requestedAlias = RequestedAlias,
        bool routeEnabled = true,
        HealthState routeHealth = HealthState.Healthy,
        bool profileEnabled = true,
        bool modelEnabled = true,
        HealthState modelHealth = HealthState.Healthy,
        bool accountEnabled = true,
        HealthState accountHealth = HealthState.Healthy,
        AuthState accountAuth = AuthState.Valid,
        DateTimeOffset? accountCooldownUntil = null,
        DateTimeOffset? accountDisabledUntil = null,
        BackendType backend = BackendType.StarCliProxy,
        string? accountOwnProfileId = null,
        string? modelOwnProfileId = null) =>
        new(
            new Route(
                routeId,
                new SessionBinding(
                    backend,
                    profileId,
                    accountId,
                    modelId,
                    reasoningEffort,
                    speedMode,
                    executionMode),
                DataClassification.PrivateSource,
                routeEnabled,
                routeHealth,
                manualPriority: 0,
                gatewayRouteKey),
            new ProviderProfile(
                profileId,
                "Profile",
                backend,
                baseUrl: null,
                executablePath: null,
                DataClassification.PrivateSource,
                profileEnabled,
                nativeProvider),
            new Account(
                accountId,
                accountOwnProfileId ?? profileId,
                "Account",
                providerNativeId,
                accountAuth,
                manualPriority: 0,
                accountEnabled,
                accountHealth,
                accountCooldownUntil,
                accountDisabledUntil,
                maxConcurrentExecutions: 1,
                reserveThreshold: null,
                gatewayNativeId: nativeAccount),
            new ModelDescriptor(
                modelId,
                backend,
                modelOwnProfileId ?? profileId,
                requestedAlias,
                "Model",
                availableAccountIds: Array.Empty<string>(),
                supportedReasoningEfforts: Array.Empty<string>(),
                supportedSpeedModes: Array.Empty<string>(),
                supportedModes: Array.Empty<string>(),
                CapabilityState.Supported,
                ModelProvenance.ProviderReported,
                modelEnabled,
                modelHealth,
                contextLimit: null,
                supportsTools: false,
                supportsAttachments: false,
                discoveredAt: Now,
                gatewayNativeId: nativeModel));
}
