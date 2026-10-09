using System.Security.Cryptography;
using System.Text;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.StateMachines;

namespace LLMWorkGUI.Application.Executions;

public sealed class ExecutionDeduplicationGuard : IExecutionDeduplicationGuard
{
    private readonly IExecutionRepository _executionRepository;
    private readonly object _sync = new();
    private readonly Dictionary<string, HashSet<string>> _activeClientRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _activePromptHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Session, string Client), ExecutionAdmission> _activeAdmissions = new();

    public ExecutionDeduplicationGuard(IExecutionRepository executionRepository)
    {
        ArgumentNullException.ThrowIfNull(executionRepository);

        _executionRepository = executionRepository;
    }

    public string ComputePromptHash(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var canonicalPrompt = prompt.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPrompt));

        return Convert.ToHexString(hash);
    }

    public async Task<ExecutionAdmission> AdmitAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LocalSessionId);

        var clientRequestId = request.ClientRequestId.ToString("D");
        var promptHash = ComputePromptHash(request.Prompt);
        var admission = new ExecutionAdmission(
            request.LocalSessionId, clientRequestId, promptHash, request.RetryOfExecutionId);

        // Reserve before reading persisted history: an older snapshot must not become
        // admissible after another request commits and releases its active reservation.
        lock (_sync)
        {
            var activeClientRequests = GetOrAdd(_activeClientRequests, request.LocalSessionId);

            if (activeClientRequests.Contains(clientRequestId))
            {
                throw new ExecutionDeduplicationException(
                    $"clientRequestId '{clientRequestId}' already has an active execution in session "
                    + $"'{request.LocalSessionId}'; an automatic repeat is forbidden.");
            }

            var activePromptHashes = GetOrAdd(_activePromptHashes, request.LocalSessionId);

            if (activePromptHashes.Contains(promptHash))
            {
                throw new ExecutionDeduplicationException(
                    $"An execution with the same canonical prompt hash is already active in session "
                    + $"'{request.LocalSessionId}'.");
            }

            activeClientRequests.Add(clientRequestId);
            activePromptHashes.Add(promptHash);
            _activeAdmissions.Add((request.LocalSessionId, clientRequestId), admission);
        }

        try
        {
            var executions = await _executionRepository
                .ListBySessionAsync(request.LocalSessionId, cancellationToken)
                .ConfigureAwait(false);

            EnsureClientRequestIdIsUnique(executions, clientRequestId);
            ValidateManualRetry(request, executions, clientRequestId);
            return admission;
        }
        catch
        {
            Release(admission);
            throw;
        }
    }

    public void Release(ExecutionAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);

        lock (_sync)
        {
            // Only the current process-local receipt owns this reservation. A late cleanup
            // cannot release a newer admission, even if its client id/hash were reused locally.
            var key = (admission.LocalSessionId, admission.ClientRequestId);
            if (!_activeAdmissions.TryGetValue(key, out var current) || !ReferenceEquals(current, admission)) return;
            _activeAdmissions.Remove(key);
            Remove(_activeClientRequests, admission.LocalSessionId, admission.ClientRequestId);
            Remove(_activePromptHashes, admission.LocalSessionId, admission.PromptHash);
        }
    }

    private static void EnsureClientRequestIdIsUnique(
        IReadOnlyList<Execution> executions,
        string clientRequestId)
    {
        if (executions.Any(execution =>
                string.Equals(execution.ClientRequestId, clientRequestId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ExecutionDeduplicationException(
                $"clientRequestId '{clientRequestId}' was already used by a persisted execution; "
                + "an automatic repeat is forbidden.");
        }
    }

    private static void ValidateManualRetry(
        ExecutionRequest request,
        IReadOnlyList<Execution> executions,
        string clientRequestId)
    {
        if (!request.IsManualRetry)
        {
            if (!string.IsNullOrWhiteSpace(request.RetryOfExecutionId))
            {
                throw new ExecutionDeduplicationException(
                    "retryOfExecutionId is only valid for a manual retry request.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(request.RetryOfExecutionId))
        {
            throw new ExecutionDeduplicationException(
                "A manual retry requires an explicit retryOfExecutionId.");
        }

        var previous = executions.FirstOrDefault(execution =>
            string.Equals(execution.Id, request.RetryOfExecutionId, StringComparison.Ordinal));

        if (previous is null)
        {
            throw new ExecutionDeduplicationException(
                $"Manual retry references unknown execution '{request.RetryOfExecutionId}' in session "
                + $"'{request.LocalSessionId}'.");
        }

        if (!ExecutionStateMachine.IsTerminal(previous.State))
        {
            throw new ExecutionDeduplicationException(
                $"Manual retry requires a terminal previous execution; '{previous.Id}' is '{previous.State}'.");
        }

        if (string.Equals(previous.ClientRequestId, clientRequestId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExecutionDeduplicationException(
                "A manual retry must use a new clientRequestId and a new ExecutionId.");
        }
    }

    private static HashSet<string> GetOrAdd(Dictionary<string, HashSet<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var values))
        {
            values = new HashSet<string>(StringComparer.Ordinal);
            map[key] = values;
        }

        return values;
    }

    private static void Remove(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (map.TryGetValue(key, out var values) && values.Remove(value) && values.Count == 0)
        {
            map.Remove(key);
        }
    }
}
