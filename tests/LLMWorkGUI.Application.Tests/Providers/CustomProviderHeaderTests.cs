using LLMWorkGUI.Application.Providers;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Providers;

public sealed class CustomProviderHeaderTests
{
    [Theory]
    [InlineData("token\r\nX-Injected: value")]
    [InlineData("token\nvalue")]
    [InlineData("token\rvalue")]
    [InlineData("token\0value")]
    [InlineData("token\u007fvalue")]
    public void Constructor_RejectsControlCharactersWithoutEchoingValue(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => new CustomProviderHeader("X-Token", value, true));
        Assert.Equal("value", error.ParamName);
        Assert.DoesNotContain("token", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer normal-token")]
    [InlineData("one\ttwo")]
    public void Constructor_PreservesValidHeaderValues(string value)
    {
        Assert.Equal(value, new CustomProviderHeader("X-Token", value).Value);
    }
}
