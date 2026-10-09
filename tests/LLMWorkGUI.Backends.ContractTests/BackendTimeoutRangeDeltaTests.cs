using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Backends.OpenCode;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class BackendTimeoutRangeDeltaTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublicTimeoutAdmissionRejectsValuesBeyondActualTimerRange(bool openCode)
    {
        var timeout = TimeSpan.FromMilliseconds(4294967295L);
        using var timer = new CancellationTokenSource();
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.CancelAfter(timeout));
        using var http = new HttpClient();
        var error = Record.Exception(() =>
        {
            if (openCode) _ = new OpenCodeClient(http, new Uri("http://127.0.0.1:4970"), timeout);
            else new MirasimOptions { RequestTimeout = timeout }.Validate();
        });
        Assert.IsType<ArgumentOutOfRangeException>(error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualTimerMaximumRemainsValidPublicConfiguration(bool openCode)
    {
        var timeout = TimeSpan.FromMilliseconds(4294967294L);
        using var timer = new CancellationTokenSource();
        timer.CancelAfter(timeout);
        using var http = new HttpClient();
        if (openCode) _ = new OpenCodeClient(http, new Uri("http://127.0.0.1:4970"), timeout);
        else new MirasimOptions { RequestTimeout = timeout }.Validate();
    }
}
