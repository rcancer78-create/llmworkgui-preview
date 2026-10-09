using System.Security.Cryptography;
using System.Text;
using LLMGateway.Server;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Infrastructure.Hosting;

/// <summary>Optional, authenticated, project-bound loopback HTTP host. Starts only after database recovery.</summary>
public sealed class NativeGatewayHttpServer(IOptions<NativeGatewayHttpOptions> options, IApplicationInstanceGuard guard,
    ISecretLifecycleService secrets, ISecretStore store, INativeGatewayRouteCatalog catalog, INativeGatewayTurnService turns)
    : IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifetime = new(1, 1);
    private EmbeddedGatewayServer? _server;
    public string? Url => _server?.Url;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartEnabledAsync(CancellationToken cancellationToken = default)
    {
        await _lifetime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_server is not null) return;
            var configured = options.Value;
            if (!configured.Enabled) return;
            guard.EnsureSupervisorPermitted();
            var project = configured.ProjectId;
            var root = configured.RootPath;
            var reference = configured.ApiKeySecretReference;
            var port = configured.Port;
            var seconds = configured.RequestTimeoutSeconds;
            if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)
                || !Directory.Exists(root) || string.IsNullOrWhiteSpace(reference) || port is < 0 or > 65535 || seconds is < 1 or > 3600)
                throw new InvalidOperationException("Для HTTP-доступа задайте проект, существующий каталог, ссылку на отдельный ключ и допустимый порт.");
            root = LLMWorkGUI.Domain.Entities.ProjectLock.CanonicalizeRoot(root);
            if (await ResolveKeyAsync(reference, cancellationToken).ConfigureAwait(false) is null)
                throw new InvalidOperationException("Отдельный ключ HTTP-доступа отсутствует, отозван или недоступен.");
            var gateway = new ProjectNativeGateway(catalog, turns, project, root, TimeSpan.FromSeconds(seconds));
            var serverOptions = new GatewayServerOptions
            {
                RequireApiKey = true, LoopbackOnly = true, ExposeManagement = false, SanitizeErrors = true,
                ExposeRoutingMetadata = false,
                ApiKeyValidator = async (provided, token) =>
                {
                    guard.EnsureSupervisorPermitted();
                    var current = await ResolveKeyAsync(reference, token).ConfigureAwait(false);
                    if (current is null) return false;
                    var supplied = Encoding.UTF8.GetBytes(provided);
                    var expected = Encoding.UTF8.GetBytes(current);
                    return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
                }
            };
            _server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:" + port,
                serverOptions, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { _lifetime.Release(); }
    }

    private async Task<string?> ResolveKeyAsync(string reference, CancellationToken token)
    {
        var before = await secrets.GetStatusAsync(reference, token).ConfigureAwait(false);
        if (!before.IsRegistered || !before.IsUsable || before.Kind != SecretReferenceKind.GatewayApiKey) return null;
        var value = await store.GetSecretAsync(reference, token).ConfigureAwait(false);
        var after = await secrets.GetStatusAsync(reference, token).ConfigureAwait(false);
        return value is { Length: >= 32 and <= 4096 } && after.IsRegistered && after.IsUsable
            && after.Kind == SecretReferenceKind.GatewayApiKey && before.LastRotatedAtUtc == after.LastRotatedAtUtc ? value : null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifetime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var running = _server; _server = null;
            if (running is not null) await running.DisposeAsync().ConfigureAwait(false);
        }
        finally { _lifetime.Release(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
