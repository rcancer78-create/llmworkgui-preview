using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Infrastructure.Storage;

namespace LLMWorkGUI.Infrastructure.Security;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore, ISecretPayloadManager
{
    private const string FileExtension = ".secret";
    private static readonly byte[] MagicHeader = "LLMWGUS1"u8.ToArray();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LLMWorkGUI.SecretStore.v1");

    public DpapiSecretStore(string? secretsDirectory = null)
    {
        SecretsDirectory = Path.GetFullPath(secretsDirectory ?? AppDataPaths.GetSecretsDirectory());
    }

    public string SecretsDirectory { get; }

    public async Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (secret.Length == 0)
        {
            throw new ArgumentException("Secret value must not be empty.", nameof(secret));
        }

        var identifier = Guid.NewGuid().ToString("N");
        var reference = SecretReference.Create(identifier);
        var payload = CreatePayload(secret);

        try
        {
            await AtomicFile.WriteAllBytesAsync(GetSecretPath(identifier), payload, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
        return reference;
    }

    public async Task<string?> GetSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        var identifier = SecretReference.GetIdentifier(secretReference);
        var path = GetSecretPath(identifier);

        if (!File.Exists(path))
        {
            return null;
        }

        var payload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Length <= MagicHeader.Length
                || !payload.AsSpan(0, MagicHeader.Length).SequenceEqual(MagicHeader))
                throw new InvalidDataException("Secret payload is not a supported LLMWorkGUI secret record.");
            encrypted = payload.AsSpan(MagicHeader.Length).ToArray();
            plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            cancellationToken.ThrowIfCancellationRequested();
            if (plaintext.Length == 0 || plaintext.AsSpan().Contains((byte)0))
                throw new InvalidDataException("The legacy credential record is corrupt.");
            try { return new UTF8Encoding(false, true).GetString(plaintext); }
            catch (DecoderFallbackException) { throw new InvalidDataException("The legacy credential record is corrupt."); }
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public Task<bool> DeleteSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var identifier = SecretReference.GetIdentifier(secretReference);
        var path = GetSecretPath(identifier);

        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public async Task<bool> PayloadExistsAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
        => (await ProbePayloadAsync(secretReference, cancellationToken).ConfigureAwait(false)).IsPresent;

    /// <summary>Checks readability without migrating, publishing or returning the plaintext.</summary>
    public async Task<SecretPayloadProbe> ProbePayloadAsync(
        string secretReference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identifier = SecretReference.GetIdentifier(secretReference);
        byte[]? payload = null;
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            payload = await File.ReadAllBytesAsync(GetSecretPath(identifier), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Length <= MagicHeader.Length || !payload.AsSpan(0, MagicHeader.Length).SequenceEqual(MagicHeader))
                return SecretPayloadProbe.Unavailable(SecretStorageFailureReason.LegacyPayloadCorrupt);
            encrypted = payload.AsSpan(MagicHeader.Length).ToArray();
            plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            cancellationToken.ThrowIfCancellationRequested();
            // Match the nonempty UTF8 credential contract without allocating an immutable secret string.
            if (plaintext.Length == 0 || plaintext.AsSpan().Contains((byte)0))
                return SecretPayloadProbe.Unavailable(SecretStorageFailureReason.LegacyPayloadCorrupt);
            _ = new UTF8Encoding(false, true).GetCharCount(plaintext);
            return SecretPayloadProbe.Present();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return SecretPayloadProbe.Absent();
        }
        catch (Exception ex) when (ex is CryptographicException or DecoderFallbackException)
        {
            return SecretPayloadProbe.Unavailable(SecretStorageFailureReason.LegacyPayloadCorrupt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SecretPayloadProbe.Unavailable(SecretStorageFailureReason.LegacyPayloadUnavailable);
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// Overwrites the payload behind an existing reference. ADR-0005 §5.1 requires a rotation to
    /// overwrite the value at the same target, so the URN, and therefore every row that references
    /// it, keeps working. The write is atomic: a reader sees either the previous or the new payload,
    /// never a partial record.
    /// </summary>
    public async Task OverwriteSecretAsync(
        string secretReference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (secret.Length == 0)
        {
            throw new ArgumentException("Secret value must not be empty.", nameof(secret));
        }

        var identifier = SecretReference.GetIdentifier(secretReference);
        var path = GetSecretPath(identifier);
        var payload = CreatePayload(secret);

        try
        {
            await AtomicFile
                .WriteAllBytesAsync(path, payload, cancellationToken, overwrite: true)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// Writes the legacy layout for an <em>already minted</em> identifier. This is the only way the
    /// primary store can keep writing the one existing DPAPI format: <see cref="SaveSecretAsync"/>
    /// always invents its own reference and could therefore not honour a reference chosen upstream.
    /// </summary>
    internal async Task WriteSecretPayloadAsync(
        string identifier,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (secret.Length == 0)
        {
            throw new ArgumentException("Secret value must not be empty.", nameof(secret));
        }

        var payload = CreatePayload(secret);

        try
        {
            await AtomicFile
                .WriteAllBytesAsync(GetSecretPath(identifier), payload, cancellationToken, overwrite: true)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// Best-effort removal of the legacy payload behind an identifier. It is used after the
    /// Credential Manager has become authoritative: a deletion that fails here leaves a stale file
    /// that is never read again, and it is retried on a later overwrite or delete.
    /// </summary>
    internal bool TryDeleteSecretPayload(string identifier)
    {
        AtomicFile.TryDelete(GetSecretPath(identifier));
        return !File.Exists(GetSecretPath(identifier));
    }

    private static byte[] CreatePayload(string secret)
    {
        var plaintext = Encoding.UTF8.GetBytes(secret);
        byte[] payload;

        try
        {
            var ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            payload = new byte[MagicHeader.Length + ciphertext.Length];
            MagicHeader.CopyTo(payload, 0);
            ciphertext.CopyTo(payload, MagicHeader.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return payload;
    }

    private string GetSecretPath(string identifier)
    {
        var path = Path.Combine(SecretsDirectory, identifier + FileExtension);

        if (!SecretReference.IsValid(SecretReference.Prefix + identifier))
        {
            throw new ArgumentException("Secret identifier contains unsupported characters.", nameof(identifier));
        }

        return path;
    }
}
