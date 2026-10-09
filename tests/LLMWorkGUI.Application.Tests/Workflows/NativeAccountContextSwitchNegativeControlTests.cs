using LLMWorkGUI.Application.Workflows.Studio;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

/// <summary>
/// Negative controls for the Phase 10 native account-context switch, at the application layer. They keep
/// the refused switch refused: the request exposes no field a caller can fill with its own wish, and the
/// studio exposes no seam that could mint a native session id or be handed adapter evidence.
///
/// The composition-level controls (that a reporting fake adapter still cannot authorize a switch, and
/// that no fake enters the shipped graph, the hardening runner or the WPF HostBootstrapper) live in
/// <c>LLMWorkGUI.IntegrationTests.Workflows.NativeAccountContextSwitchCompositionTests</c>, where the
/// production service graph can be resolved.
/// </summary>
public sealed class NativeAccountContextSwitchNegativeControlTests
{
    [Fact]
    public void SwitchRequest_ExposesNoCallerSuppliedObservationField()
    {
        var parameters = typeof(WorkflowAccountContextSwitchRequest)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.Name!)
            .ToArray();

        // The former proof compared a caller-supplied "observed" route with the requested one.
        Assert.DoesNotContain("ObservedRouteId", parameters);
        Assert.DoesNotContain("ObservedAccountId", parameters);
        Assert.DoesNotContain("NativeSessionId", parameters);

        // What a request may carry: the wanted context, the previous session and the Mirasim snapshot.
        Assert.Contains("AccountId", parameters);
        Assert.Contains("PreviousNativeSessionId", parameters);
        Assert.Contains("RequestedRouteId", parameters);
        Assert.Contains("MirasimState", parameters);
    }

    [Fact]
    public void StudioService_ExposesNoNativeSessionOrAdapterSeam()
    {
        var parameters = typeof(WorkflowStudioService)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .ToArray();

        // No local session id factory: a native session id has to be reported by the backend, and this
        // process can no longer be handed one to hand out.
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(Func<string>));

        // No seam through which adapter, processor or fixture evidence could be supplied to the switch.
        var evidenceSeams = parameters
            .Where(parameter => parameter.ParameterType.Name.Contains(
                "Adapter",
                StringComparison.Ordinal)
                || parameter.ParameterType.Name.Contains("Evidence", StringComparison.Ordinal)
                || parameter.ParameterType.Name.Contains("Probe", StringComparison.Ordinal))
            .Select(parameter => parameter.ParameterType.Name)
            .ToArray();

        Assert.Empty(evidenceSeams);
    }

    [Fact]
    public void SwitchResult_ReportsAnObservedRouteAsAlwaysAbsent()
    {
        // A future proven observation needs an honest place to land, so the property exists, but nothing
        // can set it: no producer on the sealed result can invent an observed route.
        var observedRouteId = typeof(WorkflowAccountContextSwitchResult)
            .GetProperty(nameof(WorkflowAccountContextSwitchResult.ObservedRouteId))!;

        Assert.False(observedRouteId.CanWrite);
        Assert.Null(observedRouteId.GetSetMethod(nonPublic: true));
    }

    [Fact]
    public void SwitchResult_CarriesNoFieldThatCouldClaimASwitchHappened()
    {
        // The success claim is structural, not conventional: the result stores no "switched" flag, so no
        // factory, caller or reflection-based composition can set one. The proof a real switch needs is
        // the only way IsSwitched can ever become true, and this slice does not have it.
        var storedBoolean = typeof(WorkflowAccountContextSwitchResult)
            .GetProperties(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool))
            .Select(property => (property.Name, CanWrite: property.CanWrite))
            .ToArray();

        Assert.All(
            storedBoolean,
            property => Assert.False(
                property.CanWrite,
                $"'{property.Name}' is a writable switch claim and must be a constant."));

        // IsSwitched is one of those constants, and the only factories that build a result are the three
        // named refusals. There is no public factory that could produce a "switched" outcome.
        Assert.Contains(
            storedBoolean,
            property => property.Name == nameof(WorkflowAccountContextSwitchResult.IsSwitched));

        var factories = typeof(WorkflowAccountContextSwitchResult)
            .GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(method => method.ReturnType == typeof(WorkflowAccountContextSwitchResult))
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(
            new[] { "Cancelled", "InvalidContext", "MissingNativeSwitchProof" }.OrderBy(name => name),
            factories.OrderBy(name => name));
    }
}
