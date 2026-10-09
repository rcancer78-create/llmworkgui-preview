namespace LLMWorkGUI.Application.Workflows.Legacy;

/// <summary>
/// Explicit operator command to run one user-declared legacy entrypoint as a single supervised opaque
/// process in an isolated scratch copy under the checkout writer lock (ТЗ §6.15, ROADMAP Phase 10B).
/// No entrypoint is ever selected automatically: <see cref="DeclaredEntrypoint"/> must be declared by
/// the user (<see cref="WorkflowEntrypointDescriptor.IsDeclared"/>).
/// </summary>
public sealed record LegacyWorkflowExecutionRequest
{
    public LegacyWorkflowExecutionRequest(
        string projectId,
        string canonicalCheckoutPath,
        string workflowPackageId,
        string workflowVersionId,
        string blobId,
        WorkflowEntrypointDescriptor? declaredEntrypoint,
        string? workflowRunId = null,
        IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TimeSpan? timeout = null,
        string? executionId = null)
    {
        if (timeout is { } declaredTimeout && declaredTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                declaredTimeout,
                "Legacy execution timeout must be positive when provided.");
        }

        ProjectId = ApplicationGuard.NotBlank(projectId, nameof(projectId));
        CanonicalCheckoutPath = ApplicationGuard.NotBlank(canonicalCheckoutPath, nameof(canonicalCheckoutPath));
        WorkflowPackageId = ApplicationGuard.NotBlank(workflowPackageId, nameof(workflowPackageId));
        WorkflowVersionId = ApplicationGuard.NotBlank(workflowVersionId, nameof(workflowVersionId));
        BlobId = ApplicationGuard.NotBlank(blobId, nameof(blobId));
        DeclaredEntrypoint = declaredEntrypoint;
        WorkflowRunId = ApplicationGuard.OptionalNotBlank(workflowRunId, nameof(workflowRunId));
        ExecutionId = ApplicationGuard.OptionalNotBlank(executionId, nameof(executionId));
        Arguments = (arguments ?? Array.Empty<string>()).ToArray();
        EnvironmentVariables = environmentVariables is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(environmentVariables, StringComparer.Ordinal);
        Timeout = timeout;
    }

    public string ProjectId { get; }

    /// <summary>Canonical repository root that the checkout writer lock scope is built from.</summary>
    public string CanonicalCheckoutPath { get; }

    public string WorkflowPackageId { get; }

    public string WorkflowVersionId { get; }

    public string BlobId { get; }

    /// <summary>User-declared entrypoint; null, undeclared, or blank paths are refused by the runner.</summary>
    public WorkflowEntrypointDescriptor? DeclaredEntrypoint { get; }

    public string? WorkflowRunId { get; }

    /// <summary>
    /// Optional id of an Execution aggregate the caller already persisted. It becomes the checkout
    /// writer lock owner and the scratch scope id, so a persisted execution passes the ProjectLocks
    /// foreign key. When omitted, the runner generates a fresh opaque id.
    /// </summary>
    public string? ExecutionId { get; }

    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Scoped child-process environment; star-cliproxy bypass variables are filtered out.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    public TimeSpan? Timeout { get; }
}
