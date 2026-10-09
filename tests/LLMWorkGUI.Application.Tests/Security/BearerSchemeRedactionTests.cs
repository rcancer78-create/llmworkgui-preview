using LLMWorkGUI.Application.Security;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Security;

public sealed class BearerSchemeRedactionTests
{
    [Theory]
    [InlineData("bearer")]
    [InlineData("bEaReR")]
    [InlineData("BEARER")]
    public void DiagnosticRedactsCaseInsensitiveStandaloneBearerScheme(string scheme)
    {
        const string credential = "synthetic-sensitive-token-0123456789";
        var redacted = new CredentialTextRedactor().RedactDiagnostic($"response detail {scheme} {credential}");
        Assert.DoesNotContain(credential, redacted, StringComparison.Ordinal);
        Assert.Contains(CredentialTextRedactor.Placeholder, redacted, StringComparison.Ordinal);
        Assert.Contains(scheme, redacted, StringComparison.Ordinal);
    }
}
