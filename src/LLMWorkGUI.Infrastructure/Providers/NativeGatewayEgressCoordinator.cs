using LLMGateway.Core;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Reviews;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.GrokBot;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>Consumes the actual authority itself; caller-owned payloads never reach admission.</summary>
public sealed class NativeGatewayEgressCoordinator(ISqliteConnectionFactory factory, IEgressApprovalService approvals,
    IApplicationInstanceGuard guard, Func<ILlmGateway> gatewayFactory, GrokBotProviderAdapter? adapter,
    IGrokBotReviewTransport transport, TimeProvider clock) : INativeGatewayEgressService
{
    public async Task<EgressPreview> PrepareAsync(NativeGatewayTurnRequest request, CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        var rows = await ReadEligibleAsync(request, cancellationToken).ConfigureAwait(false);
        return await Authority.PrepareAsync(Target(request, rows), Fragments(request, rows), cancellationToken).ConfigureAwait(false);
    }

    public void ApproveFragment(Guid previewId, string fragmentId, string displayedContentSha256) =>
        Authority.ApproveFragment(previewId, fragmentId, displayedContentSha256);
    public void Revoke(Guid previewId) => approvals.Revoke(previewId);
    private EgressApprovalService Authority => approvals as EgressApprovalService ?? throw new EgressApprovalException();

    internal async Task<NativeGatewayEgressAuthorization> ConsumeAsync(NativeGatewayTurnRequest request, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        if (request.EgressPreviewId is not { } previewId) throw new EgressApprovalException();
        // Consume first: a failed/malformed attempt must also spend the authority-owned ID.
        EgressPolicyRows rows;
        try { rows = await ReadEligibleAsync(request, token).ConfigureAwait(false); }
        catch { Authority.Revoke(previewId); throw; }
        ApprovedEgressPayload payload;
        try { payload = await Authority.ConsumeAsync(previewId, Target(request, rows), Fragments(request, rows), token).ConfigureAwait(false); }
        catch { Authority.Revoke(previewId); throw; }
        if (payload.Fragments.Count != 2 || payload.Fragments[0].Id != "instructions" || payload.Fragments[1].Id != "prompt"
            || payload.Fragments[0].Content != GrokBotRestrictions.ReviewInstructions) throw new EgressApprovalException();
        var prompt = payload.Fragments[1].Content;
        // Sending the approved transcript as one User must not generate another unpreviewed wrapper.
        if (Render(prompt) != prompt) throw new EgressApprovalException();
        var wire = GrokBotPromptEnvelope.Build(prompt);
        token.ThrowIfCancellationRequested();
        if (adapter is null || !adapter.HasRegisteredTransport(transport)
            || gatewayFactory() is not LlmGateway gateway || !gateway.HasRegisteredAdapter(adapter)) throw new EgressApprovalException();
        return new(payload, request.ExpectedBinding!, prompt, wire, gateway, clock, guard);
    }

    private async Task<EgressPolicyRows> ReadEligibleAsync(NativeGatewayTurnRequest request, CancellationToken token)
    {
        if (request is null || request.ExpectedBinding is not { } expected
            || expected.ProviderProfileId != GrokBotRestrictions.ProviderProfileId
            || expected.NativeAccountId != "grokbot-default" || expected.NativeModelId != "grok-bot"
            || !NativeGatewayRouteQuery.HasValidBinding(expected)) throw new EgressApprovalException();
        var root = ProjectLock.CanonicalizeRoot(request.RootPath);
        if (!Directory.Exists(root)) throw new EgressApprovalException();
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = NativeGatewayRouteQuery.PreviewCandidatesSql + " AND r.Id=$route";
            command.Parameters.AddWithValue("$route", request.RouteId); command.Parameters.AddWithValue("$project", request.ProjectId);
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToString("O"));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.IsDBNull(3)
                || new NativeGatewayRouteBinding(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)) != expected)
                throw new EgressApprovalException();
        }
        var rows = await SqliteEgressPolicyReader.ReadAsync(connection, transaction, request.ProjectId, request.RouteId, token).ConfigureAwait(false);
        if (!string.Equals(ProjectLock.CanonicalizeRoot(rows.Project.RootPath), root, StringComparison.OrdinalIgnoreCase))
            throw new EgressApprovalException();
        transaction.Commit(); return rows;
    }

    private static EgressTarget Target(NativeGatewayTurnRequest request, EgressPolicyRows rows) =>
        new(request.ProjectId, request.RootPath, request.RouteId, rows.Route.Binding, RoutingPolicy.ManualOnly);
    private static EgressFragmentInput[] Fragments(NativeGatewayTurnRequest request, EgressPolicyRows rows)
    {
        var prompt = Render(request.Prompt);
        _ = GrokBotPromptEnvelope.Build(prompt); // includes the fixed provider instruction and byte bounds
        return [new("instructions", "Инструкция провайдера", GrokBotRestrictions.ReviewInstructions, DataClassification.PublicSource),
            new("prompt", "Текст задания", prompt, rows.Project.DataClassification)];
    }
    private static string Render(string prompt) => PromptBuilder.Build(new ChatRequest { Messages = [ChatMessage.User(prompt)] },
        GrokBotRestrictions.MaxPromptCharacters);
}
