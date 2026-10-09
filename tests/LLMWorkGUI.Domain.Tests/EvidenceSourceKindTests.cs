using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class EvidenceSourceKindTests
{
    [Fact]
    public void EvidenceSourceKind_ExposesNormativeMembersInOrder()
    {
        Assert.Equal(
            new[] { "SyntheticFixture", "NativeProtocolEvent", "NotReported" },
            Enum.GetNames<EvidenceSourceKind>());
    }

    [Theory]
    [InlineData(EvidenceSourceKind.SyntheticFixture)]
    [InlineData(EvidenceSourceKind.NativeProtocolEvent)]
    [InlineData(EvidenceSourceKind.NotReported)]
    public void EvidenceSourceKind_DefinesEveryMember(EvidenceSourceKind evidenceSource)
    {
        Assert.True(Enum.IsDefined(evidenceSource));
    }
}
