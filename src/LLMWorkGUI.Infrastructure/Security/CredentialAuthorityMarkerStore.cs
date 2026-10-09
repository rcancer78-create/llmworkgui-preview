using System.Text;

namespace LLMWorkGUI.Infrastructure.Security;

/// <summary>
/// The persistent, reference-only marker that records "the Credential Manager is the authority for
/// this URN" (ADR-0005 §1.5).
///
/// Without it a <c>CredRead</c> that answers <c>1168</c> after an out-of-band credential deletion
/// would silently fall back to the legacy DPAPI file and resurrect a value that is older than the one
/// the reference is supposed to mean. The marker contains no secret value: only a fixed header and
/// the URN, so it can be written before the credential exists and read on any later probe.
/// </summary>
internal sealed class CredentialAuthorityMarkerStore
{
    private const string FileExtension = ".cmref";
    private const string Header = "LLMWGCM1";

    private readonly string _secretsDirectory;

    public CredentialAuthorityMarkerStore(string secretsDirectory)
    {
        _secretsDirectory = secretsDirectory;
    }

    public string GetMarkerPath(string identifier) =>
        Path.Combine(_secretsDirectory, identifier + FileExtension);

    public bool Exists(string identifier) => File.Exists(GetMarkerPath(identifier));

    /// <summary>
    /// Creates the marker, overwriting an existing one with identical content. Creation failure is
    /// not swallowed: the caller must abort before <c>CredWrite</c>, because a credential that is
    /// written without its marker would leave the legacy file as a silent fallback.
    /// </summary>
    public async Task CreateAsync(string identifier, string secretReference, CancellationToken cancellationToken)
    {
        var content = Encoding.UTF8.GetBytes($"{Header}\n{secretReference}\n");

        try
        {
            await Storage.AtomicFile
                .WriteAllBytesAsync(GetMarkerPath(identifier), content, cancellationToken, overwrite: true)
                .ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(content);
        }
    }

    /// <summary>
    /// Best-effort removal. It is only called for a marker this process has just created (to restore
    /// the legacy read path) and after both payload representations are gone.
    /// </summary>
    public bool TryRemove(string identifier)
    {
        try
        {
            var path = GetMarkerPath(identifier);

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
