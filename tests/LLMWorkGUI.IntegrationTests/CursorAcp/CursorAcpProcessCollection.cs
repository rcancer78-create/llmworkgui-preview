using Xunit;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CursorAcpProcessCollection
{
    public const string Name = "CursorAcpProcess";
}
