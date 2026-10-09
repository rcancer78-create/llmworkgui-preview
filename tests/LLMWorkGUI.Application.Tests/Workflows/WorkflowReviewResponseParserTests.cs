using System.Text.Json;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class WorkflowReviewResponseParserTests
{
    private const string Hash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static string Body(string verdict = "Approve") => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, runId = "run", stageId = "stage", reviewerRole = "reviewer",
        artifactId = "artifact", artifactSha256 = Hash, verdict, summary = "Findings"
    });
    private static ParsedWorkflowReviewResponse? Parse(string body) =>
        WorkflowReviewResponseParser.Parse(new WorkflowModelResponse(body), "run", "stage", "reviewer", "artifact", Hash);

    [Theory]
    [InlineData("Approve", WorkflowReviewVerdict.Approve)]
    [InlineData("Reject", WorkflowReviewVerdict.Reject)]
    [InlineData("RequestChanges", WorkflowReviewVerdict.RequestChanges)]
    public void ParsesBoundedAnswerAndBindsItsExactHash(string verdict, WorkflowReviewVerdict expected)
    {
        var response = new WorkflowModelResponse(Body(verdict));
        var parsed = Parse(response.Content);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.Verdict);
        Assert.Equal(response.Sha256, parsed.ResponseSha256);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("runId")]
    [InlineData("stageId")]
    [InlineData("reviewerRole")]
    [InlineData("artifactId")]
    [InlineData("artifactSha256")]
    [InlineData("verdict")]
    [InlineData("summary")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("fenced")]
    [InlineData("prose")]
    [InlineData("array")]
    public void RefusesDifferentTargetsAmbiguousJsonAndInvalidControlFields(string scenario)
    {
        var body = Body();
        body = scenario switch
        {
            "schemaVersion" => body.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            "runId" => body.Replace("\"run\"", "\"other\""),
            "stageId" => body.Replace("\"stage\"", "\"other\""),
            "reviewerRole" => body.Replace("\"reviewer\"", "\"other\""),
            "artifactId" => body.Replace("\"artifact\"", "\"other\""),
            "artifactSha256" => body.Replace(Hash, Hash.Replace('a', 'b')),
            "verdict" => body.Replace("\"Approve\"", "\"0\""),
            "summary" => body.Replace("\"Findings\"", "\"\""),
            "duplicate" => body.Insert(1, "\"verdict\":\"Reject\","),
            "extra" => body.Insert(1, "\"unknown\":true,"),
            "fenced" => "```json\n" + body + "\n```",
            "prose" => body + " Approved.",
            "array" => "[" + body + "]",
            _ => throw new InvalidOperationException()
        };
        Assert.Null(Parse(body));
    }
}
