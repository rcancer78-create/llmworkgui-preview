using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.ReviewerIdentity;

/// <summary>
/// One persisted <c>Routes</c> row together with the provider profile, account and model it names, as the
/// domain reads them.
/// <para>
/// The four are carried together on purpose. A route row's foreign keys were checked when it was written,
/// so a reader that trusted them would be reading a fact about the past; and the cross-links that make a
/// route meaningful are not foreign keys at all - an account may be attached to one provider while the route
/// that selects it names another, and the schema permits it. Keeping all four in one value lets the
/// resolver check that the route is internally coherent as it is read, rather than trusting that it was.
/// </para>
/// </summary>
public sealed record GatewayRouteCandidate(
    Route Route,
    ProviderProfile Profile,
    Account Account,
    ModelDescriptor Model)
{
    private static readonly IReadOnlyList<string> NoMissing = Array.Empty<string>();

    /// <summary>
    /// The identity the route carries towards a gateway, in the order the three rows declare it. Nulls are
    /// absences, and an identity with a null in it is not an identity.
    /// </summary>
    public IReadOnlyList<string?> NativeIdentity =>
        new string?[] { Profile.GatewayNativeId, Account.GatewayNativeId, Model.GatewayNativeId };

    /// <summary>True when all three native names are recorded, so this row can be matched by name at all.</summary>
    public bool HasEveryNativeName =>
        Profile.GatewayNativeId is not null
        && Account.GatewayNativeId is not null
        && Model.GatewayNativeId is not null;

    /// <summary>
    /// Which of the route's own cross-links do not hold, in a fixed order. Empty means the row is
    /// internally coherent: the account and the model belong to the very provider profile this route names,
    /// and the route points at exactly the account and the model it was read with. A row that fails any of
    /// these is not a candidate for anything, however complete its gateway identity is.
    /// </summary>
    public IReadOnlyList<string> Incoherences
    {
        get
        {
            var missing = new List<string>(5);

            if (!string.Equals(Account.ProviderProfileId, Profile.Id, StringComparison.Ordinal))
            {
                missing.Add("AccountProviderProfile");
            }

            if (!string.Equals(Model.ProviderProfileId, Profile.Id, StringComparison.Ordinal))
            {
                missing.Add("ModelProviderProfile");
            }

            if (!string.Equals(Route.Binding.ProviderProfileId, Profile.Id, StringComparison.Ordinal))
            {
                missing.Add("RouteProviderProfile");
            }

            if (!string.Equals(Route.Binding.AccountId, Account.Id, StringComparison.Ordinal))
            {
                missing.Add("RouteAccount");
            }

            if (!string.Equals(Route.Binding.ModelId, Model.Id, StringComparison.Ordinal))
            {
                missing.Add("RouteModel");
            }

            return missing.Count == 0 ? NoMissing : missing;
        }
    }

    public bool IsInternallyCoherent => Incoherences.Count == 0;

    /// <summary>
    /// The provider/account/model triple this candidate occupies. Two candidates that share a triple are two
    /// labels for one identity and can only be separated by their mode dimensions; two that do not are a
    /// namespace collision, which is a different and more serious failure - the same observed name would mean
    /// two different stored things.
    /// </summary>
    public (string ProfileId, string AccountId, string ModelId) NamespaceTuple =>
        (Profile.Id, Account.Id, Model.Id);
}
