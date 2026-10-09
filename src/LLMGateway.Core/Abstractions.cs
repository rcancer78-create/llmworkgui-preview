namespace LLMGateway.Core;

/// <summary>
/// Single entry point used by applications (LLMWorkGUI, the chat app, the HTTP server).
/// Implemented in-process by <see cref="LlmGateway"/> and remotely by <see cref="Client.OpenAiGatewayClient"/>.
/// </summary>
public interface ILlmGateway
{
    Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountInfo> AddAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default);
    Task<AccountInfo> UpdateAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Makes the account the active one for its provider.</summary>
    Task<AccountInfo> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Asks the native client about installation and authentication state.</summary>
    Task<AccountInfo> CheckAccountAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Opens the native client's own interactive login for the account (on the gateway machine).</summary>
    Task StartNativeLoginAsync(string accountId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GatewayModel>> GetModelsAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuotaSnapshot>> GetQuotasAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default);
    Task<AuthenticatedModelOptions> DiscoverModelOptionsAsync(string accountId, string nativeModel,
        CancellationToken cancellationToken = default) =>
        throw new GatewayException(GatewayErrorKind.Unsupported, "Authenticated model options are unavailable for this gateway.");
    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ChatUpdate> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}

public interface IAccountStore
{
    IReadOnlyList<AccountProfile> GetAll();
    AccountProfile? Find(string accountId);
    Task AddAsync(AccountProfile profile, CancellationToken cancellationToken = default);
    Task UpdateAsync(AccountProfile profile, CancellationToken cancellationToken = default);
    Task RemoveAsync(string accountId, CancellationToken cancellationToken = default);
    Task SelectAsync(string accountId, CancellationToken cancellationToken = default);
}

public sealed class NativeChatRequest
{
    public INativeDispatchAuthorization? DispatchAuthorization { get; init; }
    public required string Prompt { get; init; }
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public required string WorkingDirectory { get; init; }
    public required TimeSpan Timeout { get; init; }
}

/// <summary>Optional local gate. It conveys no route permission by itself; the owning dispatcher
/// consumes consent, pins the adapter and durably revalidates admission before its transport call.</summary>
public interface INativeDispatchAuthorization
{
    DateTimeOffset ExpiresAtUtc { get; }
    bool RequiresProcessBinding => false;
    Task BindProcessAsync(INativeProcessIdentity process, CancellationToken cancellationToken)
        => throw new NotSupportedException("This dispatch gate does not bind an owned native process.");
    Task ValidatePreparedAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken);
    Task AuthorizeTransportAsync(AccountProfile account, NativeChatRequest request, string wirePrompt, CancellationToken cancellationToken);
}

/// <summary>Identity of the locally owned OS process; does not identify a remote model operation.</summary>
public interface INativeProcessIdentity
{
    long ProcessGeneration { get; }
    bool HasExited { get; }
}

// Implemented only by shipped transport boundaries and explicit friend-assembly fixtures.
// Provider/catalog metadata cannot grant this capability to an arbitrary adapter.
internal interface INativeDispatchAuthorizationAdapter
{
    bool SupportsNativeDispatchAuthorization { get; }
}

/// <summary>Binds native credential isolation to the actual account store, not shared mutable options.</summary>
internal interface ICredentialEnvironmentAdapter
{
    void BindCredentialEnvironment(IAccountStore store);
}

// Only native implementations and explicit friend-assembly fixtures can publish this report.
internal interface IAuthenticatedModelOptionsAdapter
{
    Task<AuthenticatedModelOptions> DiscoverModelOptionsAsync(AccountProfile account, string nativeModel, CancellationToken token);
}

public sealed record AuthenticatedModelOptions(string AccountId, ProviderKind Provider, string NativeModel,
    IReadOnlyList<string> ReasoningEfforts, DateTimeOffset ObservedAtUtc, string Source);

public enum NativeChatEventKind
{
    Text,
    Reasoning,
    Usage,

    /// <summary>Full answer reported at the end; used only when no <see cref="Text"/> deltas were produced.</summary>
    FinalText,
    Error
}

public sealed record NativeChatEvent(
    NativeChatEventKind Kind,
    string? Text = null,
    TokenUsage? Usage = null,
    GatewayErrorKind ErrorKind = GatewayErrorKind.Upstream,
    DateTimeOffset? RetryAt = null)
{
    public static NativeChatEvent Delta(string text) => new(NativeChatEventKind.Text, text);
    public static NativeChatEvent Final(string text) => new(NativeChatEventKind.FinalText, text);
    public static NativeChatEvent Reported(TokenUsage usage) => new(NativeChatEventKind.Usage, Usage: usage);
    public static NativeChatEvent Fail(GatewayErrorKind kind, string message, DateTimeOffset? retryAt = null) => new(NativeChatEventKind.Error, message, ErrorKind: kind, RetryAt: retryAt);
}

/// <summary>
/// Adapter for one native client. Implementations must only use the client's public CLI/RPC surface,
/// must not read credential files and must not call private provider endpoints themselves.
/// </summary>
public interface IProviderAdapter
{
    ProviderKind Provider { get; }
    string DisplayName { get; }
    string DefaultExecutable { get; }
    ProviderCapabilities Capabilities { get; }
    void ValidateAccount(AccountProfile account) { }
    /// <summary>Local validation/reservation before Started; the gateway holds the lease through stream disposal.</summary>
    ValueTask<IAsyncDisposable?> PrepareRequestAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult<IAsyncDisposable?>(null);

    /// <summary>Resolved executable path or null when the client is not installed.</summary>
    string? ResolveExecutable(AccountProfile account);

    Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken cancellationToken);
    Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken cancellationToken);
    Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken cancellationToken);
    IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken);
    Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken cancellationToken);

    /// <summary>Directories in the user profile that look like extra isolated accounts (e.g. ~/.codex_work).</summary>
    IEnumerable<AccountProfile> DiscoverProfiles();
}

public sealed class GatewayOptions
{
    /// <summary>accounts.json location. Default: %LOCALAPPDATA%/LLMGateway/accounts.json.</summary>
    public string? AccountsFile { get; set; }

    /// <summary>Working directory for native agents. Default: an empty %LOCALAPPDATA%/LLMGateway/workspace.</summary>
    public string? WorkspaceDirectory { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 600;
    public int QuotaCacheSeconds { get; set; } = 60;
    public int ModelCacheSeconds { get; set; } = 600;
    public int MaxConcurrentRequestsPerAccount { get; set; } = 2;
    public ProviderKind DefaultProvider { get; set; } = ProviderKind.Codex;
    public bool DiscoverProfiles { get; set; } = true;

    /// <summary>
    /// Extra child-environment names removed before config-directory mapping and selected API-key injection.
    /// Values stay in the parent process.
    /// </summary>
    public IReadOnlyList<string> CredentialVariableNames { get; set; } = [];

    public string ResolveAccountsFile() => string.IsNullOrWhiteSpace(AccountsFile)
        ? Path.Combine(DataDirectory, "accounts.json")
        : Path.GetFullPath(AccountsFile);

    public string ResolveWorkspace() => string.IsNullOrWhiteSpace(WorkspaceDirectory)
        ? Path.Combine(DataDirectory, "workspace")
        : Path.GetFullPath(WorkspaceDirectory);

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLMGateway");
}
