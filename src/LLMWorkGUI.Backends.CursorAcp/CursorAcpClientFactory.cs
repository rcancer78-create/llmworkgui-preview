using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>Default factory creating <see cref="CursorAcpClient"/> over a bound transport.</summary>
public sealed class CursorAcpClientFactory : ICursorAcpClientFactory
{
    private readonly CursorAcpOptions _options;
    private readonly ILogger<CursorAcpClient>? _logger;

    public CursorAcpClientFactory(
        IOptions<CursorAcpOptions>? options = null,
        ILogger<CursorAcpClient>? logger = null)
    {
        _options = options?.Value ?? new CursorAcpOptions();
        _options.Validate();
        _logger = logger;
    }

    public ICursorAcpClient Create(IJsonRpcTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        return new CursorAcpClient(transport, _options, validator: null, _logger);
    }
}
