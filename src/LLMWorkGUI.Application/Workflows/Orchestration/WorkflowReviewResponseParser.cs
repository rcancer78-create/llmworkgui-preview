using System.Text.Json;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>Parses one bounded, hash-checked JSON answer against the immutable review target. Native origin is a separate gate.</summary>
public static class WorkflowReviewResponseParser
{
    public const int SchemaVersion = 1;
    public static ParsedWorkflowReviewResponse? Parse(WorkflowModelResponse response, string runId, string stageId,
        string reviewerRole, string artifactId, string artifactSha256)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stageId) || string.IsNullOrWhiteSpace(reviewerRole)
            || string.IsNullOrWhiteSpace(artifactId) || artifactSha256 is not { Length: 71 }
            || !artifactSha256.StartsWith("sha256:", StringComparison.Ordinal)
            || artifactSha256.AsSpan(7).ContainsAnyExcept("0123456789abcdef")) return null;
        try
        {
            using var document = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!properties.TryAdd(property.Name, property.Value)) return null;
            if (properties.Count != 8 || !properties.TryGetValue("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != SchemaVersion)
                return null;
            string? Text(string key) => properties.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (Text("runId") != runId || Text("stageId") != stageId || Text("reviewerRole") != reviewerRole
                || Text("artifactId") != artifactId || Text("artifactSha256") != artifactSha256)
                return null;
            var verdict = Text("verdict") switch
            {
                "Approve" => WorkflowReviewVerdict.Approve,
                "Reject" => WorkflowReviewVerdict.Reject,
                "RequestChanges" => WorkflowReviewVerdict.RequestChanges,
                _ => WorkflowReviewVerdict.Missing
            };
            var summary = Text("summary");
            if (verdict == WorkflowReviewVerdict.Missing || string.IsNullOrWhiteSpace(summary) || summary.Length > 4096)
                return null;
            return new(verdict, summary, response.Sha256);
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Parser output tied to the stored answer hash; it carries no native identity claim.</summary>
public sealed class ParsedWorkflowReviewResponse
{
    internal ParsedWorkflowReviewResponse(WorkflowReviewVerdict verdict, string summary, string responseSha256)
    { Verdict = verdict; Summary = summary; ResponseSha256 = responseSha256; }
    public WorkflowReviewVerdict Verdict { get; }
    public string Summary { get; }
    public string ResponseSha256 { get; }
}
