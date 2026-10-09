using LLMWorkGUI.Application.Security;
using Xunit;
namespace LLMWorkGUI.Application.Tests.Security;
public sealed class DiagnosticCredentialReviewTests
{
    [Theory]
    [InlineData("accessToken")]
    [InlineData("refreshToken")]
    [InlineData("idToken")]
    [InlineData("sessionToken")]
    public void CamelCaseCredentialsAreRemovedFromPlainTextAndDecodedJson(string name)
    {
        const string value = "owned-review-canary";
        var redactor = new CredentialTextRedactor();
        foreach (var text in new[] { name + "=" + value, "{\"" + name + "\":\"" + value + "\"}" })
        {
            Assert.True(redactor.ContainsSensitiveData(text));
            Assert.DoesNotContain(value, redactor.RedactDiagnostic(text));
        }
    }
    [Theory]
    [InlineData("https://owned-user:owned-pass@example.test/v1")]
    [InlineData("https://owned%40user:owned%3Apass@example.test/v1")]
    public void UrlUserInfoDoesNotReachDiagnostics(string text)
    {
        var safe = new CredentialTextRedactor().RedactDiagnostic(text);
        Assert.DoesNotContain("owned", safe);
        Assert.Contains("example.test/v1", safe);
    }
    [Fact]
    public void UnquotedWhitespacePasswordDoesNotLeakItsSuffix()
    {
        var safe = new CredentialTextRedactor().RedactDiagnostic("password: owned first second\nnext=public");
        Assert.DoesNotContain("owned", safe);
        Assert.DoesNotContain("first second", safe);
        Assert.Contains("next=public", safe);
    }
    [Theory]
    [InlineData("{\"maxTokens\":8192,\"inputTokens\":17,\"tokenizer\":\"public\"}")]
    [InlineData("healthy endpoint https://example.test/v1")]
    public void PublicTextRemainsIdentical(string text) => Assert.Equal(text, new CredentialTextRedactor().RedactDiagnostic(text));
    [Theory]
    [InlineData("Authorization: Bearer ownedSyntheticAuthValue")]
    [InlineData("Authorization: Basic ownedSyntheticBasicValue")]
    [InlineData("apiKey: sk-ownedSyntheticValue12345")]
    [InlineData("password = \"owned whitespace password\"")]
    [InlineData("password = owned whitespace password")]
    [InlineData("accessToken = ownedSyntheticToken")]
    [InlineData("https://owned-user:owned-pass@example.test/v1")]
    public void RedactionIsIdempotentIncludingWhitespaceBeforeThePlaceholder(string input)
    {
        var redactor = new CredentialTextRedactor();
        var once = redactor.RedactDiagnostic(input);
        Assert.Equal(once,redactor.RedactDiagnostic(once));
        Assert.False(redactor.ContainsSensitiveData(once));
    }
    [Theory]
    [InlineData("user=alice password=owned first second")]
    [InlineData("host: x password: a b")]
    public void PublicAssignmentsDoNotHideALaterWhitespaceCredential(string input)
    {
        var redactor = new CredentialTextRedactor();
        var safe = redactor.RedactDiagnostic(input);
        Assert.EndsWith("password" + (input.Contains("password=") ? "=" : ": ") + CredentialTextRedactor.Placeholder,safe);
        Assert.StartsWith(input.StartsWith("user=") ? "user=alice " : "host: x ",safe);
        Assert.Equal(safe,redactor.RedactDiagnostic(safe));
    }
}
