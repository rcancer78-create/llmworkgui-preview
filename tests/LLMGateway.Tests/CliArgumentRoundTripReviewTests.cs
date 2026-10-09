using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class CliArgumentRoundTripReviewTests
{
    [Theory]
    [InlineData("")]
    [InlineData("C:\\some path\\")]
    [InlineData("\\\\server\\share with space\\")]
    [InlineData("one\\\"two")]
    [InlineData("one\\\\\"two")]
    [InlineData("\\\"")]
    [InlineData("plain\\path")]
    [InlineData("  padded  ")]
    public void FormattedArgumentRoundTripsWithoutChangingNativeArgv(string argument)
    {
        string[] arguments = ["--note", argument, "--next", "end"];
        Assert.Equal(arguments, CliArguments.Parse(CliArguments.Format(arguments)));
    }

    [Fact]
    public void ExplicitEmptyQuotedArgumentsAreRetained()
    {
        Assert.Equal(new[] { "first", "", "last", "" }, CliArguments.Parse("first \"\" last \"\""));
    }

}
