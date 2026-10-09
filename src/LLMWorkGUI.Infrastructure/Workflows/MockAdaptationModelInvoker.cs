using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// Deterministic offline model invoker for tests and token-free dry runs. Responses can be queued,
/// produced by a factory, or replaced by a simulated failure; every request is recorded.
/// </summary>
public sealed class MockAdaptationModelInvoker : IAdaptationModelInvoker
{
    public const string DefaultResponseJson =
        """{"mappings":[],"rationale":"","warnings":[],"blockers":[]}""";

    private readonly object _sync = new();
    private readonly Queue<AdaptationModelResponse> _responses = new();
    private readonly List<AdaptationModelRequest> _requests = new();

    /// <summary>Invoked when the response queue is empty and no factory is set.</summary>
    public Func<AdaptationModelRequest, AdaptationModelResponse>? ResponseFactory { get; set; }

    /// <summary>When set, every invocation throws the produced exception after recording the request.</summary>
    public Func<AdaptationModelRequest, Exception>? FailureFactory { get; set; }

    public int InvocationCount
    {
        get
        {
            lock (_sync)
            {
                return _requests.Count;
            }
        }
    }

    public IReadOnlyList<AdaptationModelRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    public void EnqueueResponse(string rawText)
    {
        EnqueueResponse(new AdaptationModelResponse(rawText));
    }

    public void EnqueueResponse(AdaptationModelResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        lock (_sync)
        {
            _responses.Enqueue(response);
        }
    }

    public Task<AdaptationModelResponse> InvokeModelAsync(
        AdaptationModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Func<AdaptationModelRequest, Exception>? failureFactory;
        Func<AdaptationModelRequest, AdaptationModelResponse>? responseFactory = null;
        AdaptationModelResponse? queuedResponse = null;

        lock (_sync)
        {
            _requests.Add(request);
            failureFactory = FailureFactory;

            if (failureFactory is null)
            {
                if (_responses.Count > 0)
                {
                    queuedResponse = _responses.Dequeue();
                }
                else
                {
                    responseFactory = ResponseFactory;
                }
            }
            else
            {
                responseFactory = null;
            }
        }

        if (failureFactory is not null)
        {
            throw failureFactory(request);
        }

        var response = queuedResponse
            ?? responseFactory?.Invoke(request)
            ?? new AdaptationModelResponse(DefaultResponseJson);

        return Task.FromResult(response);
    }
}
