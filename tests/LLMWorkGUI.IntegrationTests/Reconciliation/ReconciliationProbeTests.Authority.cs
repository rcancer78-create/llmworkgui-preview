using System.Diagnostics;
using LLMWorkGUI.Infrastructure.Reconciliation;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Reconciliation;

public sealed partial class ReconciliationProbeTests
{
    [Fact]
    public async Task LivePidIsNotAnIndependentNativeSessionOrBindingObservation()
    {
        using var process = Process.GetCurrentProcess();
        var probe = new ReconciliationProbe(new FakeCliExecutableLocator(@"D:\tools\opencode.exe"));

        var result = await probe.ProbeAsync(CreateRequest($"pid:{process.Id};name:{process.ProcessName}"));

        Assert.True(result.ProcessAlive);
        Assert.Null(result.ObservedNativeSessionId);
        Assert.Null(result.ObservedBinding);
    }
}
