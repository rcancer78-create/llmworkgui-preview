using LLMWorkGUI.Backends.Abstractions.OpenCode.Routing;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Backends.OpenCode.Routing;

public sealed class OpenCodeRouteVerificationService : IRouteVerificationService
{
    public const bool TreatRequestedAsObservedAllowed = false;

    private readonly TimeProvider _timeProvider;

    public OpenCodeRouteVerificationService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RouteEvidence VerifyRoute(
        SessionBinding requested,
        SessionBinding? observed,
        string? evidenceSource = null)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var checkedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        if (observed is null)
        {
            return new RouteEvidence
            {
                RequestedBinding = requested,
                ObservedBinding = null,
                EvidenceSource = evidenceSource,
                CheckedAtUtc = checkedAtUtc,
                IsMatched = false,
                Result = RouteVerificationResult.OpaqueRouteMissingEvidence,
                Mismatches = new[] { "Observed route evidence is missing." }
            };
        }

        var mismatches = CompareBindings(requested, observed);

        return new RouteEvidence
        {
            RequestedBinding = requested,
            ObservedBinding = observed,
            EvidenceSource = evidenceSource,
            CheckedAtUtc = checkedAtUtc,
            IsMatched = mismatches.Count == 0,
            Result = mismatches.Count == 0
                ? RouteVerificationResult.Matched
                : RouteVerificationResult.Mismatch,
            Mismatches = mismatches
        };
    }

    public void EnsureRouteMatches(
        SessionBinding requested,
        SessionBinding? observed,
        string? evidenceSource = null)
    {
        var evidence = VerifyRoute(requested, observed, evidenceSource);

        if (!evidence.IsMatched)
        {
            throw new RouteMismatchException(evidence);
        }
    }

    private static IReadOnlyList<string> CompareBindings(SessionBinding requested, SessionBinding observed)
    {
        var mismatches = new List<string>();

        AddMismatch(mismatches, "backend", requested.Backend.ToString(), observed.Backend.ToString());
        AddMismatch(mismatches, "providerProfile", requested.ProviderProfileId, observed.ProviderProfileId);
        AddMismatch(mismatches, "account", requested.AccountId, observed.AccountId);
        AddMismatch(mismatches, "model", requested.ModelId, observed.ModelId);
        AddMismatch(mismatches, "reasoning", requested.ReasoningEffort, observed.ReasoningEffort);
        AddMismatch(mismatches, "speed", requested.SpeedMode, observed.SpeedMode);
        AddMismatch(mismatches, "mode", requested.ExecutionMode, observed.ExecutionMode);

        return mismatches;
    }

    private static void AddMismatch(
        List<string> mismatches,
        string field,
        string? requested,
        string? observed)
    {
        if (string.Equals(requested, observed, StringComparison.Ordinal))
        {
            return;
        }

        mismatches.Add($"{field}: requested '{Describe(requested)}' != observed '{Describe(observed)}'");
    }

    private static string Describe(string? value)
    {
        return value ?? "<none>";
    }
}
