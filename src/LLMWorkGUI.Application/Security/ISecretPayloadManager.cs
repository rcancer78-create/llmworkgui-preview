namespace LLMWorkGUI.Application.Security;

/// <summary>
/// Optional capability of an <see cref="ISecretStore"/> that can inspect and overwrite the payload
/// behind an <em>existing</em> reference. ADR-0005 §5.1 requires rotation to overwrite the value at
/// the same target, and §2.4 requires an explicit <c>Missing</c> state, neither of which is possible
/// through <see cref="ISecretStore.SaveSecretAsync"/> alone because it always mints a new URN.
///
/// A store that does not implement this interface keeps working unchanged: the lifecycle then
/// checks presence through <see cref="ISecretStore.GetSecretAsync"/> and rotates by registering a
/// new URN and revoking the old one, instead of claiming an in-place rotation it cannot perform.
/// </summary>
public interface ISecretPayloadManager
{
    /// <summary>True when a payload exists for the reference. A reference without one is <c>Missing</c>.</summary>
    Task<bool> PayloadExistsAsync(string secretReference, CancellationToken cancellationToken = default);

    /// <summary>Overwrites the payload behind an existing reference without changing the reference.</summary>
    Task OverwriteSecretAsync(string secretReference, string secret, CancellationToken cancellationToken = default);
}
