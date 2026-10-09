using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpModePolicyTests
{
    private readonly CursorAcpModePolicy _policy = new();

    [Theory]
    [InlineData("ask", CursorAcpMode.Ask)]
    [InlineData("plan", CursorAcpMode.Plan)]
    [InlineData("agent", CursorAcpMode.Agent)]
    [InlineData("Unknown", CursorAcpMode.Unknown)]
    [InlineData("ASK", CursorAcpMode.Unknown)]
    [InlineData("yolo", CursorAcpMode.Unknown)]
    public void ParseModeId_MapsStrictIdentifiers(string modeId, CursorAcpMode expected)
    {
        Assert.Equal(expected, _policy.ParseModeId(modeId));
    }

    [Theory]
    [InlineData("read-only", CursorAcpModeAccess.ReadOnly)]
    [InlineData("write", CursorAcpModeAccess.Write)]
    [InlineData("READ-ONLY", CursorAcpModeAccess.Unknown)]
    [InlineData("sandbox", CursorAcpModeAccess.Unknown)]
    public void ParseAccess_MapsStrictIdentifiers(string access, CursorAcpModeAccess expected)
    {
        Assert.Equal(expected, _policy.ParseAccess(access));
    }

    [Theory]
    [InlineData(CursorAcpMode.Ask, "ask")]
    [InlineData(CursorAcpMode.Plan, "plan")]
    [InlineData(CursorAcpMode.Agent, "agent")]
    [InlineData(CursorAcpMode.Unknown, "Unknown")]
    public void GetModeId_ReturnsStrictIdentifiers(CursorAcpMode mode, string expected)
    {
        Assert.Equal(expected, CursorAcpModePolicy.GetModeId(mode));
    }

    [Fact]
    public void Evaluate_FixtureModes_AllUnknownStatesAreFailClosed()
    {
        using var document = JsonDocument.Parse(CursorAcpTestData.ReadCapabilitiesText());
        var modes = document.RootElement.GetProperty("modes").EnumerateArray().ToArray();

        Assert.Equal(3, modes.Length);

        foreach (var mode in modes)
        {
            var modeId = mode.GetProperty("id").GetString()!;
            var access = _policy.ParseAccess(mode.GetProperty("access").GetString()!);
            var state = ParseCapabilityState(mode.GetProperty("state").GetString()!);

            Assert.Equal(CapabilityState.Unknown, state);

            var decision = _policy.Evaluate(_policy.ParseModeId(modeId), access, state);

            Assert.False(decision.CanSend);
            Assert.True(decision.RequiresWriterLock);
            Assert.False(string.IsNullOrWhiteSpace(decision.Blocker));
            Assert.Equal(modeId, decision.ModeId);
        }
    }

    [Theory]
    [InlineData(CursorAcpMode.Ask)]
    [InlineData(CursorAcpMode.Plan)]
    public void Evaluate_UnknownReadOnlyModes_DoNotReceiveWriterLockExemption(CursorAcpMode mode)
    {
        var decision = _policy.Evaluate(mode, CursorAcpModeAccess.ReadOnly, CapabilityState.Unknown);

        Assert.False(decision.CanSend);
        Assert.True(decision.RequiresWriterLock);
        Assert.Contains("Unknown", decision.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_SupportedReadOnlyMode_AllowsSendAndExemptsWriterLock()
    {
        var decision = _policy.Evaluate(CursorAcpMode.Ask, CursorAcpModeAccess.ReadOnly, CapabilityState.Supported);

        Assert.True(decision.CanSend);
        Assert.False(decision.RequiresWriterLock);
        Assert.Null(decision.Blocker);
        Assert.Equal("ask", decision.ModeId);
    }

    [Fact]
    public void Evaluate_AgentMode_AlwaysRequiresWriterLock()
    {
        foreach (var state in new[] { CapabilityState.Supported, CapabilityState.Unknown })
        {
            foreach (var access in new[] { CursorAcpModeAccess.ReadOnly, CursorAcpModeAccess.Write })
            {
                var decision = _policy.Evaluate(CursorAcpMode.Agent, access, state);

                Assert.True(decision.RequiresWriterLock);
                Assert.Equal("agent", decision.ModeId);
            }
        }
    }

    [Fact]
    public void Evaluate_SupportedWriteMode_RequiresWriterLock()
    {
        var decision = _policy.Evaluate(CursorAcpMode.Agent, CursorAcpModeAccess.Write, CapabilityState.Supported);

        Assert.True(decision.CanSend);
        Assert.True(decision.RequiresWriterLock);
        Assert.Null(decision.Blocker);
    }

    [Fact]
    public void Evaluate_UnknownMode_IsNeverSendableAndRequiresWriterLock()
    {
        var decision = _policy.Evaluate(
            CursorAcpMode.Unknown,
            CursorAcpModeAccess.ReadOnly,
            CapabilityState.Supported);

        Assert.False(decision.CanSend);
        Assert.True(decision.RequiresWriterLock);
        Assert.Equal("Unknown", decision.ModeId);
        Assert.Contains("Unknown", decision.Blocker, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CapabilityState.Unsupported)]
    [InlineData(CapabilityState.Stale)]
    [InlineData(CapabilityState.Error)]
    public void Evaluate_NonSupportedState_IsNotSendable(CapabilityState state)
    {
        var decision = _policy.Evaluate(CursorAcpMode.Plan, CursorAcpModeAccess.ReadOnly, state);

        Assert.False(decision.CanSend);
        Assert.True(decision.RequiresWriterLock);
    }

    [Fact]
    public void Evaluate_UnknownAccess_IsNeverTreatedAsReadOnly()
    {
        var decision = _policy.Evaluate(CursorAcpMode.Plan, CursorAcpModeAccess.Unknown, CapabilityState.Supported);

        Assert.True(decision.CanSend);
        Assert.True(decision.RequiresWriterLock);
    }

    private static CapabilityState ParseCapabilityState(string state) => state switch
    {
        "Supported" => CapabilityState.Supported,
        "Unsupported" => CapabilityState.Unsupported,
        _ => CapabilityState.Unknown
    };
}
