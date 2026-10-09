using System.Runtime.CompilerServices;
using LLMGateway.Core;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>The HTTP facade reads saved routes and dispatches exclusively through the project journal.</summary>
public sealed class ProjectNativeGateway(INativeGatewayRouteCatalog catalog, INativeGatewayTurnService turns,
    string projectId, string rootPath, TimeSpan timeout) : ILlmGateway
{
    public async Task<IReadOnlyList<GatewayModel>> GetModelsAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default)
    {
        var routes = await catalog.ListAsync(projectId, rootPath, cancellationToken).ConfigureAwait(false);
        return routes.Where(route => accountId is null || route.Binding.AccountId == accountId)
            .Select(route => new GatewayModel(route.Id, route.Binding.NativeModelId, route.ModelName,
                Provider(route), route.Binding.AccountId, false)).ToArray();
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Freeze the caller's values before the first await. The HTTP facade supports one plain user turn.
        if (request.ExecutionContext is not null || request.DispatchAuthorization is not null || request.Messages.Count != 1
            || request.Messages[0].Role != ChatRole.User || string.IsNullOrWhiteSpace(request.Messages[0].Content)
            || request.Messages[0].ToolCalls is { Count: > 0 } || request.Messages[0].ToolCallId is not null
            || request.Tools is { Count: > 0 } || request.ToolChoice is not null || request.ResponseFormat is not null
            || request.Stop is { Count: > 0 } || request.ReasoningEffort is not null || request.MaxOutputTokens is not null)
            throw new GatewayException(GatewayErrorKind.Unsupported, "Поддерживается один текстовый пользовательский запрос без дополнительных параметров.");
        var model = request.Model;
        var account = request.AccountId;
        var prompt = request.Messages[0].Content;
        if (prompt.Length > 128 * 1024) throw GatewayException.Invalid("Запрос превышает допустимый размер.");
        var routes = await catalog.ListAsync(projectId, rootPath, cancellationToken).ConfigureAwait(false);
        var route = routes.SingleOrDefault(item => item.Id == model);
        if (route is null || account is not null && account != route.Binding.AccountId)
            throw new GatewayException(GatewayErrorKind.ModelNotFound, "Выбранный маршрут недоступен для проекта.");
        var result = await turns.ExecuteAsync(new(projectId, rootPath, route.Id, Guid.NewGuid().ToString("D"), prompt)
        {
            ExpectedBinding = route.Binding, Timeout = timeout
        }, cancellationToken).ConfigureAwait(false);
        if (result.State != ExecutionState.Succeeded || result.RequiresReconciliation || string.IsNullOrWhiteSpace(result.Content))
            throw new GatewayException(GatewayErrorKind.Upstream, "Завершение запроса не подтверждено. Проверьте локальный журнал приложения.");
        return new("chatcmpl-" + result.ExecutionId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), route.Id,
            route.Binding.NativeModelId, Provider(route), route.Binding.AccountId, result.Content, [], "stop",
            TokenUsage.Estimate(prompt, result.Content));
    }

    public async IAsyncEnumerable<ChatUpdate> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The journal service returns a verified whole turn. No apparent success is sent before its completion.
        var result = await CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        yield return new(ChatUpdateKind.Started, AccountId: result.AccountId, Provider: result.Provider, Model: result.Model);
        yield return new(ChatUpdateKind.TextDelta, result.Content);
        yield return new(ChatUpdateKind.Completed, Result: result, AccountId: result.AccountId, Provider: result.Provider, Model: result.Model);
    }

    private static ProviderKind Provider(NativeGatewayRouteOption route) => Enum.GetValues<ProviderKind>()
        .Where(kind => kind != ProviderKind.Unknown)
        .Single(kind => GatewayCatalogMapper.ProviderId(kind) == route.Binding.ProviderProfileId);
    private static GatewayException ManagementUnavailable() => new(GatewayErrorKind.Unsupported, "Настройки нативных аккаунтов доступны в локальном приложении.");
    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default) => throw ManagementUnavailable();
    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default) => throw ManagementUnavailable();
    public Task<AccountInfo> AddAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default) => throw ManagementUnavailable();
    public Task<AccountInfo> UpdateAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default) => throw ManagementUnavailable();
    public Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw ManagementUnavailable();
    public Task<AccountInfo> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw ManagementUnavailable();
    public Task<AccountInfo> CheckAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw ManagementUnavailable();
    public Task StartNativeLoginAsync(string accountId, CancellationToken cancellationToken = default) => throw ManagementUnavailable();
    public Task<IReadOnlyList<QuotaSnapshot>> GetQuotasAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default) => throw ManagementUnavailable();
}
