using LLMWorkGUI.App.ViewModels;
using Microsoft.Extensions.Hosting;

namespace LLMWorkGUI.App.Services;

/// <summary>Stops new composer work and waits for its actual operation, including native uncertainty
/// recording. This does not confirm native termination or release retained execution ownership.</summary>
public sealed class NativeGatewayComposerDrain : IHostedService
{
    private NativeGatewayWorkspaceViewModel? _composer;

    internal void Attach(NativeGatewayWorkspaceViewModel composer)
    {
        var previous = Interlocked.CompareExchange(ref _composer, composer, null);
        if (previous is not null && !ReferenceEquals(previous, composer))
            throw new InvalidOperationException("The composer lifetime already has an owner.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var composer = Volatile.Read(ref _composer);
        if (composer is null) return Task.CompletedTask;
        composer.BeginShutdown();
        return composer.WaitForIdleAsync(cancellationToken);
    }
}
