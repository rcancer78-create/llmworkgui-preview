using LLMWorkGUI.Application.Providers;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Providers;

public class ProviderUrlValidatorTests
{
    [Theory]
    [InlineData("https://username:password@example.com/v1")]
    [InlineData("http://username:password@localhost:8000/v1")]
    [InlineData("https://username@example.com/v1")]
    [InlineData("https://example.com/v1#fragment")]
    public void Validate_CredentialsOrFragment_RejectsWithoutEchoingSensitiveUrl(string url)
    {
        var result = ProviderUrlValidator.Validate(url);

        Assert.False(result.IsValid);
        Assert.Equal(UrlClassification.InvalidFormat, result.Classification);
        Assert.Null(result.NormalizedUrl);
        Assert.DoesNotContain("password", result.ErrorMessage ?? string.Empty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyOrNull_ReturnsInvalidFormat(string? url)
    {
        var result = ProviderUrlValidator.Validate(url);

        Assert.False(result.IsValid);
        Assert.Equal(UrlClassification.InvalidFormat, result.Classification);
        Assert.NotNull(result.ErrorMessage);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://localhost:21")]
    [InlineData("file:///C:/path/file.txt")]
    [InlineData("ws://127.0.0.1:8080")]
    public void Validate_InvalidSchemeOrFormat_ReturnsInvalidFormat(string url)
    {
        var result = ProviderUrlValidator.Validate(url);

        Assert.False(result.IsValid);
        Assert.Equal(UrlClassification.InvalidFormat, result.Classification);
    }

    [Theory]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("http://localhost:8080/", "http://localhost:8080")]
    [InlineData("http://127.0.0.1:11434/v1/", "http://127.0.0.1:11434/v1")]
    [InlineData("https://localhost:8443/v1", "https://localhost:8443/v1")]
    [InlineData("http://127.0.0.1:8000", "http://127.0.0.1:8000")]
    public void Validate_LoopbackUrls_ReturnsValidLoopbackHttp_AndNormalizes(string url, string expectedNormalized)
    {
        var result = ProviderUrlValidator.Validate(url);

        Assert.True(result.IsValid);
        Assert.Equal(UrlClassification.ValidLoopbackHttp, result.Classification);
        Assert.Equal(expectedNormalized, result.NormalizedUrl);
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1")]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com/v1")]
    [InlineData("https://my-proxy.company.org/ai/", "https://my-proxy.company.org/ai")]
    public void Validate_RemoteHttps_ReturnsValidRemoteHttps_AndNormalizes(string url, string expectedNormalized)
    {
        var result = ProviderUrlValidator.Validate(url);

        Assert.True(result.IsValid);
        Assert.Equal(UrlClassification.ValidRemoteHttps, result.Classification);
        Assert.Equal(expectedNormalized, result.NormalizedUrl);
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData("http://api.openai.com/v1")]
    [InlineData("http://remote-server.com:8080/v1/")]
    [InlineData("http://192.168.1.50:8000/v1")]
    public void Validate_RemoteInsecureHttp_ReturnsInsecureRemoteHttp_Disallowed(string url)
    {
        var result = ProviderUrlValidator.Validate(url);

        Assert.False(result.IsValid);
        Assert.Equal(UrlClassification.InsecureRemoteHttp, result.Classification);
        Assert.Contains("Insecure HTTP is disallowed", result.ErrorMessage);
    }
}
