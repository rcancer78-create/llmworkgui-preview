using LLMWorkGUI.Application.Security;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Security;

public sealed class StructuredSensitiveDetectionReviewTests
{
    [Theory]
    [InlineData("{\"api_key\":{\"opaque\":\"abc\"}}")]
    [InlineData("[{\"password\":[\"abc\",17]}]")]
    [InlineData("{\"\\u0061pi_key\":{\"opaque\":\"abc\"}}")]
    public void TypedSensitiveJsonCannotPassThePublicEgressDetector(string payload)
    {
        var redactor = new CredentialTextRedactor();
        Assert.True(redactor.ContainsSensitiveData(payload));
        Assert.Contains(CredentialTextRedactor.Placeholder, redactor.RedactDiagnostic(payload));
    }

    [Theory]
    [InlineData("  { \"max_tokens\" : 4096, \"notes\" : [\"ordinary text\"] }  ")]
    [InlineData("{\"api_key\":\"[REDACTED]\"}")]
    [InlineData("plain project notes")]
    public void CleanOrAlreadyMaskedInputIsNotRejectedBecauseOfFormatting(string payload)
    {
        var redactor = new CredentialTextRedactor();
        Assert.False(redactor.ContainsSensitiveData(payload));
        Assert.Equal(payload, redactor.RedactDiagnostic(payload));
    }
}
