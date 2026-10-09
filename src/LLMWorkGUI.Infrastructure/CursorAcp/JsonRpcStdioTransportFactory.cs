using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Infrastructure.CursorAcp;

/// <summary>
/// Creates <see cref="JsonRpcStdioTransport"/> instances over agent stdio streams using the shared
/// <see cref="CursorAcpOptions"/>.
/// </summary>
public sealed class JsonRpcStdioTransportFactory : IJsonRpcTransportFactory
{
    private readonly CursorAcpOptions _options;

    public JsonRpcStdioTransportFactory(IOptions<CursorAcpOptions>? options = null)
    {
        _options = options?.Value ?? new CursorAcpOptions();
        _options.Validate();
    }

    public IJsonRpcTransport Create(Stream agentStandardOutput, Stream agentStandardInput)
    {
        return new JsonRpcStdioTransport(agentStandardOutput, agentStandardInput, _options);
    }
}
