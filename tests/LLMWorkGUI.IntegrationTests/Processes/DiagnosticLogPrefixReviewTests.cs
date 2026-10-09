using LLMWorkGUI.Infrastructure.Processes;
using Xunit;
namespace LLMWorkGUI.IntegrationTests.Processes;
public sealed class DiagnosticLogPrefixReviewTests
{
    [Theory]
    [InlineData("INFO")]
    [InlineData("WARN")]
    [InlineData("WARNING")]
    [InlineData("ERROR")]
    [InlineData("DEBUG")]
    [InlineData("TRACE")]
    public void PlainLogPrefixPreservesUsefulTextAndMasksCredentialsAcrossChunkBoundaries(string level)
    {
        var source = $"[{level}] ready accessToken=owned-log-canary\r\nclean-tail\r\n";
        var redactor = new DiagnosticSpoolRedactor();
        var safe = string.Concat(source.Select(c => redactor.Append(new[] { c }))) + redactor.Append([], complete: true);
        Assert.Contains($"[{level}] ready", safe);
        Assert.Contains("clean-tail", safe);
        Assert.DoesNotContain("owned-log-canary", safe);
        Assert.False(redactor.RecordsSuppressed);
    }
    [Fact]
    public void StructuredJsonStillRedactsAndMalformedArrayStaysSuppressed()
    {
        var redactor = new DiagnosticSpoolRedactor();
        var safe = redactor.Append("[{\"accessToken\":\"owned-array-canary\"}]\n".AsSpan(), true);
        Assert.DoesNotContain("owned-array-canary", safe);
        Assert.Contains("REDACTED", safe);
        var broken = new DiagnosticSpoolRedactor();
        Assert.Contains("incomplete", broken.Append("[\"owned-unclosed\"".AsSpan(), true));
        Assert.True(broken.RecordsSuppressed);
    }
    [Fact]
    public void RepeatedBalancedJsonFragmentsDoNotCopyTheGrowingRecordAtEachBracket()
    {
        // Warm the valid-array parsing path before measuring synchronous per-thread allocations.
        _ = new DiagnosticSpoolRedactor().Append("[0]\n".AsSpan(), true);
        var payload = string.Concat(Enumerable.Repeat("[0]", 4000));
        var redactor = new DiagnosticSpoolRedactor();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var output = redactor.Append(payload.AsSpan(), true);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(redactor.RecordsSuppressed); // multiple root arrays are malformed JSON
        Assert.Contains("incomplete", output);
        Assert.True(allocated < 2_000_000, $"Bounded record allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("WARN")]
    [InlineData("WARNING")]
    [InlineData("ERROR")]
    [InlineData("DEBUG")]
    [InlineData("TRACE")]
    public void TruncatedJsonAfterLogPrefixIsNeverPublishedAcrossChunks(string level)
    {
        var redactor = new DiagnosticSpoolRedactor();
        var input = $"[{level}] {{\"apiKey\":\"owned-truncated-canary\ncontinued-secret-tail";
        var output = string.Concat(input.Select(c => redactor.Append(new[] { c })))
            + redactor.Append([], complete: true);
        Assert.DoesNotContain("owned-truncated-canary", output);
        Assert.DoesNotContain("continued-secret-tail", output);
        Assert.True(redactor.RecordsSuppressed);
        Assert.Contains("incomplete", output);
    }

    [Fact]
    public void ValidMultilineJsonAfterLogPrefixPreservesPublicContentAndNextRecord()
    {
        var redactor = new DiagnosticSpoolRedactor();
        var input = "[INFO] {\n\"apiKey\":\"owned-prefixed-canary\",\n\"status\":\"ready\"}\n[INFO] normal ready\n";
        var output = string.Concat(input.Select(c => redactor.Append(new[] { c })))
            + redactor.Append([], complete: true);
        Assert.DoesNotContain("owned-prefixed-canary", output);
        Assert.Contains("[INFO] {", output);
        Assert.Contains("ready", output);
        Assert.Contains("\n[INFO] normal ready\n", output);
        Assert.False(redactor.RecordsSuppressed);
    }

    [Theory]
    [InlineData("[ERROR] message {\"apiKey\":\"owned-fragment-canary\ncontinued-secret-tail")]
    [InlineData("[INFO] response follows: {\"refreshToken\":\"owned-fragment-canary")]
    public void TextBeforeAnUnterminatedJsonObjectDoesNotExposeTheStructuredTail(string input)
    {
        var redactor = new DiagnosticSpoolRedactor();
        var output = string.Concat(input.Select(c => redactor.Append(new[] { c })))
            + redactor.Append([], complete: true);
        Assert.DoesNotContain("owned-fragment-canary", output);
        Assert.DoesNotContain("continued-secret-tail", output);
        Assert.True(redactor.RecordsSuppressed);
    }

    [Fact]
    public void PublicTextBeforeAValidJsonObjectIsRetained()
    {
        var redactor = new DiagnosticSpoolRedactor();
        var output = redactor.Append("[INFO] response follows: {\"apiKey\":\"owned-fragment-canary\",\"status\":\"ready\"}\n".AsSpan(), true);
        Assert.StartsWith("[INFO] response follows: {", output);
        Assert.Contains("ready", output);
        Assert.DoesNotContain("owned-fragment-canary", output);
        Assert.False(redactor.RecordsSuppressed);
    }

    [Fact]
    public void CredentialsInPublicProseBeforeValidJsonAreAlsoRedacted()
    {
        var redactor = new DiagnosticSpoolRedactor();
        var output = redactor.Append("[INFO] password=owned first second {\"status\":\"ok\"}\n".AsSpan(), true);
        Assert.DoesNotContain("owned first second", output);
        Assert.Contains("[INFO] password=[REDACTED]", output);
        Assert.Contains("\"status\":\"ok\"", output);
        Assert.False(redactor.RecordsSuppressed);
    }
}
