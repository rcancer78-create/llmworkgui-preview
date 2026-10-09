using LLMWorkGUI.Backends.Abstractions;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Backends.OpenCode;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public class BackendAdapterSmokeTests
{
    [Fact]
    public void OpenCodeAdapter_ExposesStableBackendId()
    {
        IBackendAdapter adapter = new OpenCodeBackendAdapter();

        Assert.Equal("opencode", adapter.BackendId);
    }

    [Fact]
    public void CursorAcpAdapter_ExposesStableBackendId()
    {
        IBackendAdapter adapter = new CursorAcpBackendAdapter();

        Assert.Equal("cursor-acp", adapter.BackendId);
    }
}
