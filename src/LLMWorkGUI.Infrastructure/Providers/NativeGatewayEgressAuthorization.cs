using System.Security.Cryptography;
using System.Text;
using LLMGateway.Core;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>Internal admission proof; validates again where the sealed adapter invokes its transport.</summary>
internal sealed class NativeGatewayEgressAuthorization(ApprovedEgressPayload payload, NativeGatewayRouteBinding binding,
    string prompt, string wire, LlmGateway gateway, TimeProvider clock, IApplicationInstanceGuard guard) : INativeDispatchAuthorization
{
    private NativeGatewayAdmission? _entry;
    private SqliteNativeGatewayJournal? _journal;
    private int _claimed;
    private volatile bool _transportAttempted;
    internal ApprovedEgressPayload Payload => payload;
    internal NativeGatewayRouteBinding Binding => binding;
    internal string Prompt => prompt;
    internal string WireSha256 { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wire)));
    internal LlmGateway Gateway => gateway;
    internal string ActorInstanceId => guard.InstanceId;
    internal bool TransportAttempted => _transportAttempted;
    public DateTimeOffset ExpiresAtUtc => payload.ExpiresAtUtc;
    internal void Attach(NativeGatewayAdmission entry, SqliteNativeGatewayJournal journal)
    {
        if (Interlocked.CompareExchange(ref _entry, entry, null) is not null) throw new EgressApprovalException();
        _journal = journal;
    }

    internal void ValidateRows(EgressPolicyRows rows)
    {
        guard.EnsureSupervisorPermitted();
        if (clock.GetUtcNow() >= payload.ExpiresAtUtc || rows.Fingerprint != payload.PolicyFingerprint
            || rows.Route.Binding != payload.Target.Binding || rows.Project.Id != payload.Target.ProjectId
            || rows.Route.Id != payload.Target.RouteId
            || !string.Equals(ProjectLock.CanonicalizeRoot(rows.Project.RootPath),
                ProjectLock.CanonicalizeRoot(payload.Target.RootPath), StringComparison.OrdinalIgnoreCase)) throw new EgressApprovalException();
    }

    private void ValidateRequest(AccountProfile account, NativeChatRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); guard.EnsureSupervisorPermitted();
        if (_entry is null || _journal is null || clock.GetUtcNow() >= payload.ExpiresAtUtc
            || account.Provider != ProviderKind.GrokBot || account.Id != binding.NativeAccountId
            || request.Model != binding.NativeModelId || request.Prompt != prompt || request.ReasoningEffort is not null
            || !ReferenceEquals(request.DispatchAuthorization, this)
            || !string.Equals(ProjectLock.CanonicalizeRoot(request.WorkingDirectory),
                ProjectLock.CanonicalizeRoot(payload.Target.RootPath), StringComparison.OrdinalIgnoreCase)) throw new EgressApprovalException();
    }

    public async Task ValidatePreparedAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(account, request, cancellationToken);
        await _journal!.ValidateEgressAsync(_entry!, this, cancellationToken).ConfigureAwait(false);
    }

    public async Task AuthorizeTransportAsync(AccountProfile account, NativeChatRequest request, string wirePrompt, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0) throw new EgressApprovalException();
        ValidateRequest(account, request, cancellationToken);
        if (wirePrompt != wire) throw new EgressApprovalException();
        await _journal!.MarkRunningAsync(_entry!, cancellationToken, this).ConfigureAwait(false);
        ValidateRequest(account, request, cancellationToken);
        // Set immediately before returning to the sealed adapter's single transport invocation.
        _transportAttempted = true;
    }
}
