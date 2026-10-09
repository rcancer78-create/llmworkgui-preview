using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

/// <summary>
/// The three identities a persisted route depends on, named so a refusal can say which of them is absent
/// instead of reporting a generic "unknown route".
/// <para>
/// These are separate values on purpose. "There is no <c>Routes</c> row with that id" and "the row exists
/// but the account it points at does not" are different facts, and a caller that has to refuse has to be
/// able to say which one it found.
/// </para>
/// </summary>
public enum WorkflowRouteIdentity
{
    ProviderProfile,
    Account,
    Model
}

/// <summary>
/// A persisted route together with the outcome of resolving everything it needs in order to be executed.
/// <para>
/// A route that names an account, a provider profile and a model is only a label until all three rows
/// exist, and the storage layer's foreign keys only guarantee that at insert time. This value is the
/// read-time statement of the same fact, so an execution that is being persisted cannot be handed a route
/// whose identity has since been removed.
/// </para>
/// </summary>
public sealed record WorkflowRouteAssignment(Route Route, IReadOnlyList<WorkflowRouteIdentity> MissingIdentities)
{
    private static readonly IReadOnlyList<WorkflowRouteIdentity> None = Array.Empty<WorkflowRouteIdentity>();

    public static WorkflowRouteAssignment Complete(Route route) => new(route, None);

    /// <summary>True only when the account, the provider profile and the model all resolve to stored rows.</summary>
    public bool HasEveryIdentity => MissingIdentities.Count == 0;
}

/// <summary>
/// Reads persisted routes and nothing else. There is no create, update or delete here on purpose: a route
/// is a routing decision with a real account, profile and model behind it, and nothing in the model-review
/// path is allowed to invent one. A caller that needs a route to exist is asking for configuration, and the
/// refusal that follows is the honest answer.
/// </para>
/// </summary>
public interface IRouteRepository
{
    /// <summary>
    /// The assignment of <paramref name="routeId"/>, or null when no <c>Routes</c> row carries that id.
    /// A null answer is the only way a caller learns that an id such as <c>route-opencode</c> is a label
    /// rather than a route: it was never a row, and it will not be made into one.
    /// </summary>
    Task<WorkflowRouteAssignment?> GetAssignmentAsync(
        string routeId,
        CancellationToken cancellationToken = default);
}
