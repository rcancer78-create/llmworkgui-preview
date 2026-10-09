using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

internal sealed class FakeCursorAcpClient : ICursorAcpClient
{
    private readonly Channel<CursorAcpStreamEvent> _events = Channel.CreateUnbounded<CursorAcpStreamEvent>();
    private int _subscriptionCount;
    public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

    public CursorAcpHandshakeResult? CurrentReadiness { get; set; }

    public long SkippedStreamEventCount => 0;

    public List<CursorAcpPromptRequest> PromptRequests { get; } = new();

    public List<CursorAcpCancelRequest> CancelRequests { get; } = new();

    public List<CursorAcpPermissionReplyRequest> PermissionReplies { get; } = new();

    public TaskCompletionSource PromptDispatched { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource EventDelivered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource EventsCompleted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Func<CursorAcpPromptRequest, CancellationToken, Task<CursorAcpPromptResult>> PromptHandler { get; set; } =
        (_, _) => Task.FromResult(CursorAcpPromptResult.Completed(CursorAcpStopReasons.EndTurn));

    /// <summary>Handshake handler; the default refuses so tests must opt into a ready backend.</summary>
    public Func<CancellationToken, Task<CursorAcpHandshakeResult>>? InitializeHandler { get; set; }

    public Func<CursorAcpNewSessionRequest, CancellationToken, Task<CursorAcpSessionResult>>? CreateSessionHandler { get; set; }

    public Func<CursorAcpLoadSessionRequest, CancellationToken, Task<CursorAcpSessionResult>>? LoadSessionHandler { get; set; }

    public List<CursorAcpNewSessionRequest> CreateSessionRequests { get; } = new();

    public List<CursorAcpLoadSessionRequest> LoadSessionRequests { get; } = new();

    public Func<CursorAcpCancelRequest, CancellationToken, Task<CursorAcpCancelResult>> CancelHandler { get; set; } =
        (_, _) => Task.FromResult(CursorAcpCancelResult.Accepted());

    public Func<CursorAcpPermissionReplyRequest, CancellationToken, Task<CursorAcpPermissionReplyResult>> PermissionReplyHandler { get; set; } =
        (_, _) => Task.FromResult(CursorAcpPermissionReplyResult.Sent());

    public async Task<CursorAcpHandshakeResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (InitializeHandler is null)
        {
            throw new NotSupportedException("The fake client does not perform an ACP handshake.");
        }

        var result = await InitializeHandler(cancellationToken).ConfigureAwait(false);
        CurrentReadiness = result;
        return result;
    }

    public Task<CursorAcpSessionResult> CreateSessionAsync(
        CursorAcpNewSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        CreateSessionRequests.Add(request);

        return CreateSessionHandler is null
            ? throw new NotSupportedException("The fake client does not create ACP sessions.")
            : CreateSessionHandler(request, cancellationToken);
    }

    public Task<CursorAcpSessionResult> LoadSessionAsync(
        CursorAcpLoadSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        LoadSessionRequests.Add(request);

        return LoadSessionHandler is null
            ? throw new NotSupportedException("The fake client does not load ACP sessions.")
            : LoadSessionHandler(request, cancellationToken);
    }

    public Task<CursorAcpPromptResult> PromptAsync(
        CursorAcpPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        PromptRequests.Add(request);
        PromptDispatched.TrySetResult();

        return PromptHandler(request, cancellationToken);
    }

    public async IAsyncEnumerable<CursorAcpStreamEvent> SubscribeEventsAsync(
        string? sessionId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _subscriptionCount);
        while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_events.Reader.TryRead(out var streamEvent))
            {
                if (sessionId is null ||
                    string.Equals(streamEvent.SessionId, sessionId, StringComparison.Ordinal))
                {
                    EventDelivered.TrySetResult();
                    yield return streamEvent;
                }
            }
        }

        EventsCompleted.TrySetResult();
    }

    public Task<CursorAcpPermissionReplyResult> ReplyPermissionAsync(
        CursorAcpPermissionReplyRequest request,
        CancellationToken cancellationToken = default)
    {
        PermissionReplies.Add(request);

        return PermissionReplyHandler(request, cancellationToken);
    }

    public Task<CursorAcpCancelResult> CancelSessionAsync(
        CursorAcpCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        CancelRequests.Add(request);

        return CancelHandler(request, cancellationToken);
    }

    public void PushEvent(CursorAcpStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        _events.Writer.TryWrite(streamEvent);
    }

    public void CompleteEvents() => _events.Writer.TryComplete();
}
