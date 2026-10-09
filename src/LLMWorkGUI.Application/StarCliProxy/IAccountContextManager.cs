namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>
/// Single abstraction that resolves and verifies immutable Codex/AGY account contexts behind the
/// star-cliproxy boundary (ADR-0007). Codex contexts are explicit absolute CODEX_HOME directories;
/// AGY contexts are selected only through <c>agy-profile</c> with all §6.11a safety invariants.
/// <see cref="ExecuteSerializedAsync"/> serializes select → verify → launch under one account/writer
/// lock so concurrent profile switches cannot race (TOCTOU-free).
/// </summary>
public interface IAccountContextManager
{
    IReadOnlyList<CodexAccountContext> CodexContexts { get; }

    /// <summary>True when at least one account context can be pinned (Codex context or available agy-profile).</summary>
    bool HasPinnableContexts { get; }

    void RegisterCodexContext(CodexAccountContext context);

    Task<IReadOnlyList<AgyAccountContext>> ListAgyContextsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the currently active AGY profile, or null when agy-profile is unavailable or reports none.</summary>
    Task<string?> GetActiveAgyProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Selects and verifies a context, performing a serialized agy-profile switch when required.
    /// Must be reached through <see cref="ExecuteSerializedAsync"/> when the context is about to be used.
    /// </summary>
    Task<AccountContextSelection> SelectAndVerifyAsync(
        AccountContextRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only verification that never switches an AGY profile; used by auth probes.
    /// </summary>
    Task<AccountContextSelection> VerifyContextAsync(
        AccountContextRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> under the shared account lock after select → verify.
    /// The lock covers the launch callback, excluding TOCTOU between account switch and launch.
    /// The callback also receives an unresolved selection to return a structured refusal;
    /// it must check IsResolved before launching. Selection and verification use a non-reentrant
    /// lock and must not be called again from this callback.
    /// </summary>
    Task<TResult> ExecuteSerializedAsync<TResult>(
        AccountContextRequest request,
        Func<AccountContextSelection, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}
