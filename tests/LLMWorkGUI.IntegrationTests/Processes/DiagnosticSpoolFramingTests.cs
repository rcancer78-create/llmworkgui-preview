using System.Text;
using LLMWorkGUI.Infrastructure.Processes;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class DiagnosticSpoolFramingTests
{
    [Fact]
    public void RedactedJsonKeepsItsRecordTerminatorAndFollowingLineSeparate()
    {
        foreach (var terminator in new[] { "\n", "\r\n" })
        {
            var input = "{\"password\":\"tiny\"}" + terminator + "next-record" + terminator;
            Assert.Equal("{\"password\":\"[REDACTED]\"}" + terminator + "next-record" + terminator, Frame(input, 1));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(4096)]
    public void StructuredAndPemCredentialsAreSafeUnderArbitraryCharacterPartitions(int partition)
    {
        var label = new string('A', 200) + " PRIVATE KEY";
        var input = "ordinary 😀\r\n{\n\"\\u0070assword\": \"tiny\",\n\"visible\": \"ok\"\n}\n"
            + "-----BEGIN " + label + "-----\nsyntheticLongLabelBody\n-----END " + label + "-----\nlast-line";
        var output = Frame(input, partition);
        Assert.DoesNotContain("tiny", output, StringComparison.Ordinal);
        Assert.DoesNotContain("syntheticLongLabelBody", output, StringComparison.Ordinal);
        Assert.Contains("ordinary 😀", output, StringComparison.Ordinal);
        Assert.Contains("ok", output, StringComparison.Ordinal);
        Assert.EndsWith("last-line", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\n\"password\":\"syntheticBrokenJson")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nsyntheticTruncatedPem")]
    public void EndOfStreamDoesNotPersistIncompleteSecretRecords(string input)
    {
        var output = Frame(input, 1);
        Assert.DoesNotContain("syntheticBrokenJson", output, StringComparison.Ordinal);
        Assert.DoesNotContain("syntheticTruncatedPem", output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverflowStaysSuppressedUntilTheStructuredOrPemRecordEnds(bool pem)
    {
        var padding = new string('x', DiagnosticSpoolRedactor.MaximumRecordCharacters * 3);
        var input = pem ? "-----BEGIN PRIVATE KEY-----\n" + padding + "\nsyntheticLateBody\n-----END PRIVATE KEY-----\nresumed\n"
            : "{\n\"ordinary\":\"" + padding + "\",\n\"password\":\"syntheticLateBody\"\n}\nresumed\n";
        var output = Frame(input, 7);
        Assert.DoesNotContain("syntheticLateBody", output, StringComparison.Ordinal);
        Assert.Contains("resumed", output, StringComparison.Ordinal);
        Assert.True(output.Length < 1000);
    }

    private static string Frame(string input, int partition)
    {
        var filter = new DiagnosticSpoolRedactor();
        var output = new StringBuilder();
        for (var i = 0; i < input.Length; i += partition)
            output.Append(filter.Append(input.AsSpan(i, Math.Min(partition, input.Length - i))));
        return output.Append(filter.Append([], complete: true)).ToString();
    }
}
