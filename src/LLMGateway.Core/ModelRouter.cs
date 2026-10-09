using System.Text.RegularExpressions;

namespace LLMGateway.Core;

public sealed record RouteResult(AccountProfile Account, string? NativeModel);

/// <summary>
/// Resolves <c>model</c> + optional <c>account_id</c> to a concrete account and native model.
/// Model ids: <c>provider/account/model</c>, <c>provider/model</c>, <c>provider</c>, a bare native model, or <c>auto</c>.
/// </summary>
public static partial class ModelRouter
{
    // Account IDs are bounded to 64 characters; the native model retains its own 160-character limit.
    private static readonly int MaximumRouteCharacters = 160 + 64 + 2 + Enum.GetNames<ProviderKind>().Max(name => name.Length);

    public static RouteResult Resolve(
        ChatRequest request,
        IReadOnlyList<AccountProfile> accounts,
        ProviderKind defaultProvider,
        Func<string, IReadOnlyList<NativeModel>?> cachedModels,
        Func<ProviderKind, bool>? allowAutomatic = null)
    {
        var model = (request.Model ?? string.Empty).Trim();
        if (model.Length > 0 && (model.Length > MaximumRouteCharacters || !RoutePattern().IsMatch(model)))
            throw GatewayException.Invalid($"Недопустимое имя модели '{model}'.");
        var segments = model.Split('/');
        ProviderKind? provider = segments.Length > 0 && Enum.TryParse<ProviderKind>(segments[0], true, out var parsed) && !int.TryParse(segments[0], out _)
            ? parsed : null;

        if (!string.IsNullOrWhiteSpace(request.AccountId))
        {
            var account = accounts.FirstOrDefault(a => a.Id.Equals(request.AccountId, StringComparison.OrdinalIgnoreCase))
                ?? throw new GatewayException(GatewayErrorKind.NotFound, $"Аккаунт '{request.AccountId}' не найден.");
            if (provider is { } p && p != account.Provider)
                throw GatewayException.Invalid($"Модель '{model}' относится к провайдеру {p}, а аккаунт '{account.Id}' — к {account.Provider}.");
            var rest = provider is null ? segments : segments.Skip(1).ToArray();
            // A known account segment belongs to the route being overridden. Otherwise the
            // remaining slash-separated name is the native model, including vendor prefixes.
            if (provider is { } routeProvider && rest.Length > 0
                && accounts.Any(candidate => candidate.Provider == routeProvider
                    && candidate.Id.Equals(rest[0], StringComparison.OrdinalIgnoreCase))) rest = rest[1..];
            return Finish(account, rest);
        }

        if (provider is { } kind)
        {
            var byId = segments.Length >= 2
                ? accounts.FirstOrDefault(a => a.Provider == kind && a.Id.Equals(segments[1], StringComparison.OrdinalIgnoreCase))
                : null;
            if (byId is not null) return Finish(byId, segments[2..]);
            return Finish(Active(kind), segments[1..]);
        }

        if (model.Length == 0 || model.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var eligible = accounts.Where(a => allowAutomatic?.Invoke(a.Provider) != false);
            var preferred = eligible.FirstOrDefault(a => a.Provider == defaultProvider && a.IsActive && a.Enabled)
                ?? eligible.FirstOrDefault(a => a.IsActive && a.Enabled)
                ?? throw new GatewayException(GatewayErrorKind.ProviderUnavailable, "Нет активных аккаунтов.");
            return Finish(preferred, []);
        }

        if (!IsValidModelName(model)) throw GatewayException.Invalid($"Недопустимое имя модели '{model}'.");
        var candidates = accounts.Where(a => a.Enabled && allowAutomatic?.Invoke(a.Provider) != false)
            .OrderByDescending(a => a.IsActive)
            .ThenBy(a => a.Provider == defaultProvider ? 0 : 1);
        foreach (var account in candidates)
        {
            var models = cachedModels(account.Id);
            if (models is not null && models.Any(m => m.Id.Equals(model, StringComparison.OrdinalIgnoreCase)))
                return Finish(account, segments);
        }
        throw new GatewayException(GatewayErrorKind.ModelNotFound,
            $"Модель '{model}' не найдена. Используйте id из /v1/models (provider/account/model).");

        AccountProfile Active(ProviderKind kind) =>
            accounts.FirstOrDefault(a => a.Provider == kind && a.IsActive && a.Enabled)
            ?? accounts.FirstOrDefault(a => a.Provider == kind && a.Enabled)
            ?? throw new GatewayException(GatewayErrorKind.ProviderUnavailable, $"Для провайдера {kind} нет включённых аккаунтов.");
    }

    public static bool IsValidModelName(string model) => ModelPattern().IsMatch(model);

    private static RouteResult Finish(AccountProfile account, string[] modelSegments)
    {
        if (!account.Enabled)
            throw new GatewayException(GatewayErrorKind.ProviderUnavailable, $"Аккаунт '{account.Id}' отключён.");
        var native = string.Join('/', modelSegments);
        if (native.Length == 0 || native.Equals("auto", StringComparison.OrdinalIgnoreCase)) native = account.DefaultModel ?? string.Empty;
        if (native.Length > 0 && !IsValidModelName(native))
            throw GatewayException.Invalid($"Недопустимое имя модели '{native}'.");
        return new RouteResult(account, native.Length == 0 ? null : native);
    }

    public static string ModelId(AccountProfile account, string nativeModel) =>
        $"{account.Provider.ToString().ToLowerInvariant()}/{account.Id}/{nativeModel}";

    /// <summary>No leading '-', so a model name can never be parsed as a command-line option.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._:/@+\[\]-]{0,159}\z")]
    private static partial Regex ModelPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._:/@+\[\]-]*\z")]
    private static partial Regex RoutePattern();
}
