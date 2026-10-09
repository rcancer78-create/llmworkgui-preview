namespace LLMWorkGUI.Application.Security;

public interface ISecretStore
{
    /// <summary>Creates a fresh reference and payload without modifying any previously saved reference.</summary>
    Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default);

    Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken = default);

    Task<bool> DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default);
}
