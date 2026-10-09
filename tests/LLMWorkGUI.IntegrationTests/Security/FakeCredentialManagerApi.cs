using LLMWorkGUI.Infrastructure.Security;

namespace LLMWorkGUI.IntegrationTests.Security;

/// <summary>A single native call the fake boundary received, kept for target-mapping assertions.</summary>
internal sealed record CredentialManagerCall(string Operation, string Target, string? UserName = null, string? Comment = null);

/// <summary>
/// A scriptable, in-memory <see cref="ICredentialManagerApi"/>. It exists so the storage procedure of
/// <see cref="CredentialManagerSecretStore"/> can be exercised for the failure windows Windows will
/// not reproduce on demand: a refused write, an unreadable record, a classified outage.
/// </summary>
internal sealed class FakeCredentialManagerApi : ICredentialManagerApi
{
    private readonly Dictionary<string, byte[]> _credentials = new(StringComparer.Ordinal);

    public List<CredentialManagerCall> Calls { get; } = new();

    /// <summary>Replaces the outcome of <see cref="Read"/>. <c>null</c> uses the stored credentials.</summary>
    public CredentialManagerOutcome? ReadOutcomeOverride { get; set; }

    /// <summary>Replaces the blob a successful <see cref="Read"/> returns, e.g. to model a corrupt record.</summary>
    public byte[]? ReadBlobOverride { get; set; }

    /// <summary>Replaces the outcome of <see cref="Write"/>. <c>null</c> writes the credential.</summary>
    public CredentialManagerOutcome? WriteOutcomeOverride { get; set; }

    /// <summary>Replaces the outcome of <see cref="Delete"/>. <c>null</c> deletes the credential.</summary>
    public CredentialManagerOutcome? DeleteOutcomeOverride { get; set; }

    public int StoredCredentialCount => _credentials.Count;

    public IReadOnlyCollection<string> Targets => _credentials.Keys;

    public IReadOnlyList<CredentialManagerCall> CallsFor(string operation) =>
        Calls.Where(call => call.Operation == operation).ToList();

    public void Seed(string target, string value) => _credentials[target] = System.Text.Encoding.UTF8.GetBytes(value);

    public void SeedRaw(string target, byte[] blob) => _credentials[target] = blob;

    public string? StoredValue(string target) =>
        _credentials.TryGetValue(target, out var blob) ? System.Text.Encoding.UTF8.GetString(blob) : null;

    public CredentialManagerReadResult Read(string target)
    {
        Calls.Add(new CredentialManagerCall("Read", target));

        if (ReadOutcomeOverride is not null)
        {
            return ReadOutcomeOverride == CredentialManagerOutcome.Success
                ? CredentialManagerReadResult.Success(ReadBlobOverride ?? Array.Empty<byte>())
                : CredentialManagerReadResult.Failure(ReadOutcomeOverride.Value);
        }

        if (!_credentials.TryGetValue(target, out var blob))
        {
            return CredentialManagerReadResult.Failure(CredentialManagerOutcome.NotFound);
        }

        // A copy, so a test that mutates it cannot reach into what the store already cleared.
        return CredentialManagerReadResult.Success((byte[])blob.Clone());
    }

    public CredentialManagerOperationResult Write(string target, string secret, string userName, string comment)
    {
        Calls.Add(new CredentialManagerCall("Write", target, userName, comment));

        if (WriteOutcomeOverride is not null)
        {
            return CredentialManagerOperationResult.Failure(WriteOutcomeOverride.Value);
        }

        _credentials[target] = System.Text.Encoding.UTF8.GetBytes(secret);
        return CredentialManagerOperationResult.Success();
    }

    public CredentialManagerOperationResult Delete(string target)
    {
        Calls.Add(new CredentialManagerCall("Delete", target));

        if (DeleteOutcomeOverride is not null)
        {
            return CredentialManagerOperationResult.Failure(DeleteOutcomeOverride.Value);
        }

        return _credentials.Remove(target)
            ? CredentialManagerOperationResult.Success()
            : CredentialManagerOperationResult.Failure(CredentialManagerOutcome.NotFound);
    }
}
