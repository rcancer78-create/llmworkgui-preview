using LLMGateway.Core;
namespace LLMGateway.Tests;
public sealed class EmbeddedCliFlagReviewTests
{
    [Theory]
    [InlineData("--trust")]
    [InlineData("--mcp-config=owned.json")]
    [InlineData("--plugin-dir", "owned")]
    [InlineData("--append-system-prompt-file=owned.txt")]
    [InlineData("--tools=all")]
    [InlineData("--strict-mcp-config")]
    public void LaterArgumentsCannotOverrideTheAdaptersControlledToolAndConfigurationPolicy(string first, string? second = null)
    {
        var args = second is null ? new[] { first } : new[] { first, second };
        Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.Throws<GatewayException>(() => CliArguments.RejectUnsafe(args)).Kind);
    }
    [Fact]
    public void SafeFlagsAndWindowsQuotedPathsRemainUsable()
    {
        CliArguments.RejectUnsafe(CliArguments.Parse("--sandbox read-only --color never"));
        Assert.Equal(new[] { "--note", @"C:\Program Files\owned" }, CliArguments.Parse(@"--note ""C:\Program Files\owned"""));
    }
    [Theory]
    [InlineData("accessToken")]
    [InlineData("refreshToken")]
    [InlineData("idToken")]
    [InlineData("sessionToken")]
    public void CamelCaseCredentialQueriesCannotBeStoredInPublicEnvironment(string name)
    {
        Assert.True(AccountEnvironment.IsSecret("PUBLIC_ENDPOINT", "https://example.test/v1?" + name + "=ownedQueryValue"));
        Assert.False(AccountEnvironment.IsSecret("PUBLIC_ENDPOINT", "https://example.test/v1?tokenBudget=8192"));
    }
}
