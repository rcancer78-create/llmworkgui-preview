using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

/// <summary>Real adapter and owned Node processes; synthetic catalog, no agy, login or network.</summary>
public sealed class AntigravityStatusTests
{
    [Theory]
    [InlineData(AccountAuthMode.NativeLogin)]
    [InlineData(AccountAuthMode.ApiKeyFromEnvironment)]
    public async Task AvailableCatalogDoesNotProveAuthenticationOrAccountIdentity(AccountAuthMode mode)
    {
        using var fixture = new Fixture(mode);
        var before = DateTimeOffset.UtcNow;
        var status = await fixture.Adapter.GetStatusAsync(fixture.Account, fixture.Timeout.Token);

        Assert.Equal(AccountAvailability.Unknown, status.Availability);
        Assert.Null(status.Identity);
        Assert.Null(status.ClientVersion);
        Assert.Contains("каталог", status.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не подтвержден", status.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(status.CheckedAt, before, DateTimeOffset.UtcNow);
        Assert.Equal("models", File.ReadAllText(fixture.Marker));
        Assert.DoesNotContain("synthetic-key", status.Message!);

        var model = Assert.Single(await fixture.Adapter.ListModelsAsync(fixture.Account, fixture.Timeout.Token));
        Assert.Equal("gemini-fixture-model", model.Id);
        Assert.Equal("Fixture model", model.DisplayName);
    }

    [Theory]
    [InlineData(AccountAuthMode.NativeLogin)]
    [InlineData(AccountAuthMode.ApiKeyFromEnvironment)]
    public async Task UnsupportedQuotaDoesNotInventAPlanOrReadyState(AccountAuthMode mode)
    {
        using var fixture = new Fixture(mode);
        var quota = await fixture.Adapter.GetQuotaAsync(fixture.Account, fixture.Timeout.Token);

        Assert.False(quota.Supported);
        Assert.Equal(AccountAvailability.Unknown, quota.Availability);
        Assert.Null(quota.Plan);
        Assert.Empty(quota.Buckets);
        Assert.Equal(fixture.Account.Id, quota.AccountId);
        Assert.Equal("models", File.ReadAllText(fixture.Marker));
    }

    [Theory]
    [InlineData("auth", AccountAvailability.AuthenticationRequired)]
    [InlineData("rate", AccountAvailability.RateLimited)]
    [InlineData("empty", AccountAvailability.Error)]
    public async Task CatalogFailureRetainsFailureClassification(string outcome, AccountAvailability expected)
    {
        using var fixture = new Fixture(AccountAuthMode.NativeLogin, outcome);
        var status = await fixture.Adapter.GetStatusAsync(fixture.Account, fixture.Timeout.Token);

        Assert.Equal(expected, status.Availability);
        Assert.Null(status.Identity);
        Assert.Equal("models", File.ReadAllText(fixture.Marker));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        private readonly string _keyVariable = "LLMWORKGUI_AGY_FIXTURE_" + Guid.NewGuid().ToString("N");
        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(15));
        public AccountProfile Account { get; }
        public AntigravityAdapter Adapter { get; }
        public string Marker => _directory.GetPath("models-called.txt");

        public Fixture(AccountAuthMode mode, string outcome = "catalog")
        {
            var script = _directory.GetPath("node_modules/fixture/cli.mjs");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, """
                import fs from 'node:fs';
                if (process.argv.slice(2).join(' ') !== 'models') process.exit(9);
                fs.writeFileSync('models-called.txt', 'models');
                const outcome = process.env.LLMWORKGUI_AGY_FIXTURE_OUTCOME;
                if (outcome === 'auth') { console.error('authentication required'); process.exit(1); }
                if (outcome === 'rate') { console.error('rate limit exceeded'); process.exit(1); }
                if (outcome !== 'empty') console.log('gemini-fixture-model\tFixture model');
                """);
            var shim = _directory.GetPath("fixture.cmd");
            // Resolver unwraps this into node.exe + the local script; cmd.exe is never executed.
            File.WriteAllText(shim, "@node \"%dp0%\\node_modules\\fixture\\cli.mjs\" %*\n");
            Account = new AccountProfile
            {
                Id = "synthetic-agy-status", Provider = ProviderKind.Antigravity,
                Executable = shim, WorkingDirectory = _directory.Root,
                ConfigDirectory = _directory.GetPath("isolated-api-home"), AuthMode = mode,
                ApiKeyVariable = mode == AccountAuthMode.ApiKeyFromEnvironment ? _keyVariable : null,
                Environment = new() { ["LLMWORKGUI_AGY_FIXTURE_OUTCOME"] = outcome }
            };
            if (mode == AccountAuthMode.ApiKeyFromEnvironment)
                System.Environment.SetEnvironmentVariable(_keyVariable, "synthetic-key");
            Adapter = new AntigravityAdapter(new ExecutableResolver(),
                new GatewayOptions { WorkspaceDirectory = _directory.Root }, NullLogger<AntigravityAdapter>.Instance);
        }

        public void Dispose()
        {
            System.Environment.SetEnvironmentVariable(_keyVariable, null);
            Timeout.Dispose();
            _directory.Dispose();
        }
    }
}
