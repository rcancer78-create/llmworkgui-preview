using Microsoft.Extensions.Hosting;

namespace LLMWorkGUI.Infrastructure.Workflows;

internal sealed class AdaptationRuntimeHostedService(OpenCodeAdaptationRuntimeRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => registry.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => registry.StopAsync(cancellationToken);
}
