using System.Text.Json;
using LLMGateway.Core;

namespace LLMGateway.Native.Adapters;

public sealed partial class CodexAdapter
{
    async Task<AuthenticatedModelOptions> IAuthenticatedModelOptionsAdapter.DiscoverModelOptionsAsync(
        AccountProfile account, string nativeModel, CancellationToken token)
    {
        await using var session = await OpenAsync(account, experimentalApi: false, token).ConfigureAwait(false);
        var before = ToStatus(await session.Rpc.RequestAsync("account/read", new { refreshToken = false }, ShortTimeout, token).ConfigureAwait(false), session.Version);
        if (before.Availability != AccountAvailability.Ready)
            throw new GatewayException(GatewayErrorKind.AuthenticationRequired, "Codex account is not authenticated.");
        List<string[]> matches = [];
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var response = await session.Rpc.RequestAsync("model/list", cursor is null ? new { } : new { cursor }, ShortTimeout, token).ConfigureAwait(false);
            matches.AddRange(ParseReasoningOptions(response, nativeModel));
            cursor = response.Str("nextCursor");
            if (cursor is null) break;
        }
        if (cursor is not null || matches.Count != 1)
            throw new GatewayException(GatewayErrorKind.ModelNotFound, "Native model discovery was incomplete or ambiguous.");
        var after = ToStatus(await session.Rpc.RequestAsync("account/read", new { refreshToken = false }, ShortTimeout, token).ConfigureAwait(false), session.Version);
        if (after.Availability != AccountAvailability.Ready || before.Identity != after.Identity)
            throw new GatewayException(GatewayErrorKind.AuthenticationRequired, "Native account changed during model discovery.");
        return new(account.Id, account.Provider, nativeModel, Array.AsReadOnly(matches[0]), DateTimeOffset.UtcNow,
            "codex app-server · authenticated account/read + model/list");
    }

    internal static IEnumerable<string[]> ParseReasoningOptions(JsonElement response, string nativeModel)
    {
        if (response.Prop("data") is not { ValueKind: JsonValueKind.Array } data)
            throw new GatewayException(GatewayErrorKind.Upstream, "Native model list is malformed.");
        foreach (var item in data.EnumerateArray())
        {
            var model = Unique(item, "model");
            if (model is not { ValueKind: JsonValueKind.String } || model.Value.GetString() != nativeModel) continue;
            if (item.Bool("hidden")) continue;
            if (Unique(item, "supportedReasoningEfforts") is not { ValueKind: JsonValueKind.Array } options || options.GetArrayLength() > 64)
                throw new GatewayException(GatewayErrorKind.Unsupported, "Native model does not publish reasoning options.");
            var values = options.EnumerateArray().Select(option => Unique(option, "reasoningEffort") is { ValueKind: JsonValueKind.String } value
                ? value.GetString()! : "").ToArray();
            if (values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim() || value.Any(char.IsControl))
                || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                throw new GatewayException(GatewayErrorKind.Upstream, "Native reasoning options are malformed.");
            yield return values;
        }
    }

    private static JsonElement? Unique(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var values = element.EnumerateObject().Where(p => p.Name == name).ToArray();
        if (values.Length > 1) throw new GatewayException(GatewayErrorKind.Upstream, "Duplicate native model metadata.");
        return values.Length == 1 ? values[0].Value : null;
    }
}
