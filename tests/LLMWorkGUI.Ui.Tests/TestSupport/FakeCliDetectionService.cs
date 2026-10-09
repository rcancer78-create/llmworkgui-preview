using LLMWorkGUI.Application.Cli;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal sealed class FakeCliDetectionService : ICliDetectionService
{
    private readonly Func<CliDetectionSnapshot> _snapshotFactory;

    public FakeCliDetectionService(Func<CliDetectionSnapshot> snapshotFactory)
    {
        ArgumentNullException.ThrowIfNull(snapshotFactory);

        _snapshotFactory = snapshotFactory;
    }

    public Exception? ExceptionToThrow { get; set; }

    public int DetectCallCount { get; private set; }

    public static FakeCliDetectionService Degraded(DateTimeOffset? detectedAtUtc = null)
    {
        var timestamp = detectedAtUtc ?? DateTimeOffset.UnixEpoch;

        return new FakeCliDetectionService(() => CliDetectionSnapshot.AllNotDetected(timestamp));
    }

    public static FakeCliDetectionService AllDetected(DateTimeOffset? detectedAtUtc = null)
    {
        var timestamp = detectedAtUtc ?? DateTimeOffset.UnixEpoch;

        return new FakeCliDetectionService(() => CliDetectionTestData.AllDetected(timestamp));
    }

    public Task<CliDetectionSnapshot> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DetectCallCount++;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(_snapshotFactory());
    }
}
