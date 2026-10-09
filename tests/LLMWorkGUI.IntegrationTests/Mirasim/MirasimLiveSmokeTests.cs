using System;
using System.Net.Http;
using System.Threading.Tasks;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Infrastructure.Mirasim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Mirasim;

/// <summary>
/// Minimal read-only live smoke against an installed and running Mirasim host (ROADMAP Phase 7M exit
/// criteria, ADR-0008, TECHNICAL_SPECIFICATION 6.11b). Only <c>GET /api/health</c> and the
/// unauthenticated <c>GET /api/state</c> challenge are exercised: no prompt is sent, no session is
/// created, the session lifecycle service is never touched, no credential or token file is read and
/// the host process is never started, restarted or stopped (ADR-0008 section 7), so no model quota is
/// spent.
/// </summary>
/// <remarks>
/// Both branches are honest. When Mirasim is not running the degraded probe must still be typed and
/// carry a blocker, so the suite stays green on a clean machine while producing real evidence where
/// the host answers on 127.0.0.1:4970.
/// </remarks>
public sealed class MirasimLiveSmokeTests
{
    [Fact]
    public async Task LiveMirasimHost_IsDiscovered_AndReportsObservedVersionAndCompatibility()
    {
        using var httpClient = CreateHttpClient();
        var client = CreateClient(httpClient);

        var status = await client.ProbeHealthAsync();

        if (!status.Ok)
        {
            // A clean machine without a running Mirasim host is a supported configuration: the
            // degraded probe must still be typed and explain that the loopback host is unreachable.
            Assert.False(string.IsNullOrWhiteSpace(status.ErrorMessage));
            Assert.True(
                status.ErrorMessage!.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
                status.ErrorMessage.Contains("refused", StringComparison.OrdinalIgnoreCase),
                status.ErrorMessage);
            return;
        }

        Assert.Equal("mirasim", status.Name);
        Assert.False(string.IsNullOrWhiteSpace(status.Version));
        // Discovery remains valid when the independently installed host has upgraded. A new version
        // must remain unsupported until its protocol has passed acceptance; this probe cannot grant it.
        if (string.Equals(status.Version, MirasimHealthStatus.SupportedVersion, StringComparison.OrdinalIgnoreCase))
            Assert.True(status.IsSupportedVersion);
        else
            Assert.False(status.IsSupportedVersion);
        Assert.False(string.IsNullOrWhiteSpace(status.InstanceId));
        Assert.True(status.Pid is > 0, $"The live Mirasim host did not report a pid: {status.Pid}.");

        // Observed host metadata must never leak the user profile path.
        var observedMetadata = string.Join(' ', status.Name, status.Version, status.InstanceId);

        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            observedMetadata,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveMirasimHost_RejectsUnauthenticatedStateProbe_WithoutSendingPrompt()
    {
        using var httpClient = CreateHttpClient();
        var client = CreateClient(httpClient);

        var status = await client.ProbeHealthAsync();

        if (!status.Ok)
        {
            // Without a host there is no state endpoint to challenge; the typed client must surface
            // the same unreachable/refused evidence instead of a silent success.
            var exception = await Assert.ThrowsAsync<MirasimClientException>(
                () => client.ProbeAuthenticatedEndpointAsync(testToken: null));

            Assert.False(string.IsNullOrWhiteSpace(exception.Message));
            Assert.True(
                exception.Message.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("refused", StringComparison.OrdinalIgnoreCase),
                exception.Message);
            return;
        }

        // A live host must answer the token-less probe with the HTTP 401 challenge, proving the
        // authenticated endpoint is protected without ever sending a prompt or a credential.
        var authenticated = await client.ProbeAuthenticatedEndpointAsync(testToken: null);

        Assert.False(authenticated);
    }

    [Fact]
    public async Task LiveMirasimHost_DI_ResolvesSingletonClient_AndCanProbeEndpoint()
    {
        var services = new ServiceCollection();
        services.AddMirasimBackend();

        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IMirasimClient>();

        Assert.Same(client, provider.GetRequiredService<IMirasimClient>());
        Assert.Equal(new Uri("http://127.0.0.1:4970"), client.BaseUrl);

        var options = provider.GetRequiredService<IOptions<MirasimOptions>>().Value;

        Assert.Equal("127.0.0.1", options.Hostname);
        Assert.Equal(4970, options.Port);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RequestTimeout);

        var status = await client.ProbeHealthAsync();

        // The probe must always answer with a typed status, whether the host is live or not.
        Assert.NotNull(status);
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static MirasimClient CreateClient(HttpClient httpClient)
    {
        return new MirasimClient(
            httpClient,
            Options.Create(new MirasimOptions()));
    }
}
