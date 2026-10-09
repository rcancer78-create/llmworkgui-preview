using System.Text.Json;
using System.Text.RegularExpressions;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.DependencyInjection;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class OpenCodeSessionContractTests
{
    [Theory]
    [InlineData("openrouter/anthropic/synthetic-model", "openrouter", "anthropic/synthetic-model")]
    [InlineData("test-provider/family/version/model", "test-provider", "family/version/model")]
    public void ModelNamespaceKeepsTheFirstComponentAsProvider(string model, string provider, string nativeModel)
    {
        using var create = JsonDocument.Parse(OpenCodeSessionJson.SerializeCreateSessionRequest(new() { Model = model }));
        using var prompt = JsonDocument.Parse(OpenCodeSessionJson.SerializePromptRequest(new() { Model = model, Prompt = "fixture" }));
        Assert.Equal(provider, create.RootElement.GetProperty("model").GetProperty("providerID").GetString());
        Assert.Equal(nativeModel, create.RootElement.GetProperty("model").GetProperty("id").GetString());
        Assert.Equal(provider, prompt.RootElement.GetProperty("model").GetProperty("providerID").GetString());
        Assert.Equal(nativeModel, prompt.RootElement.GetProperty("model").GetProperty("modelID").GetString());
    }
    [Fact]
    public void SessionResponse_ParsesSessionExportSampleAgainstSchema()
    {
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(FixturePath("session-schema.json")));
        using var exportDocument = JsonDocument.Parse(File.ReadAllText(FixturePath("session-export-sample.json")));

        var info = exportDocument.RootElement.GetProperty("info");

        foreach (var required in schemaDocument.RootElement.GetProperty("required").EnumerateArray())
        {
            var name = required.GetString();
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.True(info.TryGetProperty(name!, out _), $"Exported session is missing required field '{name}'.");
        }

        var session = OpenCodeSessionResponse.Parse(info);

        Assert.Equal("ses_test01sanitized0000000000000001", session.Id);
        Assert.Equal("sanitized-demo-session", session.Slug);
        Assert.Equal("prj_test01sanitized00000000000001", session.ProjectId);
        Assert.Equal(@"C:\workspace\demo-app", session.Directory);
        Assert.Equal("Sanitized demo session", session.Title);
        Assert.Equal("1.18.31", session.Version);
        Assert.Null(session.ParentId);
        Assert.Equal(0m, session.Cost);
        Assert.Equal("test-model", session.Model);

        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1789948800000).UtcDateTime,
            session.CreatedAtUtc);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1789948812000).UtcDateTime,
            session.UpdatedAtUtc);

        Assert.NotNull(session.Tokens);
        Assert.Equal(1234, session.Tokens.Input);
        Assert.Equal(567, session.Tokens.Output);
        Assert.Equal(123, session.Tokens.Reasoning);
        Assert.Equal(800, session.Tokens.CacheRead);
        Assert.Equal(200, session.Tokens.CacheWrite);

        var idPattern = schemaDocument.RootElement
            .GetProperty("properties")
            .GetProperty("id")
            .GetProperty("pattern")
            .GetString();

        Assert.False(string.IsNullOrWhiteSpace(idPattern));
        Assert.Matches(new Regex(idPattern!, RegexOptions.CultureInvariant), session.Id);
    }

    [Fact]
    public void SessionResponse_ParsesForkedSessionParentAndModelVariants()
    {
        const string Json = """
        {
          "id": "ses_child000000000000000000000001",
          "projectID": "prj_1",
          "directory": "C:\\workspace\\demo-app",
          "title": "Child session",
          "version": "1.18.31",
          "parentID": "ses_parent0000000000000000000001",
          "model": { "providerID": "test-provider", "modelID": "test-model", "variant": null },
          "time": { "created": 1789948800000, "updated": 1789948812000 }
        }
        """;

        var session = OpenCodeSessionResponse.Parse(Json);

        Assert.Equal("ses_parent0000000000000000000001", session.ParentId);
        Assert.Equal("test-model", session.Model);
        Assert.Null(session.Tokens);
        Assert.Null(session.Cost);
        Assert.Null(session.Slug);
    }

    [Fact]
    public void SessionResponse_WhenNativeIdIsMissing_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => OpenCodeSessionResponse.Parse("""{"title":"no id"}"""));
        Assert.Throws<JsonException>(() => OpenCodeSessionResponse.Parse("[]"));
    }

    [Fact]
    public void CreateSessionRequest_SerializesNormalizedJson()
    {
        var request = new OpenCodeCreateSessionRequest
        {
            Directory = @"C:\workspace\demo-app",
            Title = "Demo session",
            Model = "test-provider/test-model",
            Agent = "build"
        };

        var json = OpenCodeSessionJson.SerializeCreateSessionRequest(request);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("directory", out _));
        Assert.Equal("Demo session", root.GetProperty("title").GetString());
        Assert.Equal("test-provider", root.GetProperty("model").GetProperty("providerID").GetString());
        Assert.Equal("test-model", root.GetProperty("model").GetProperty("id").GetString());
        Assert.Equal("build", root.GetProperty("agent").GetString());
    }

    [Fact]
    public void CreateSessionRequest_MatchesCapturedNativeSchema()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(FixturePath("session-create-schema-20261002.json")));
        using var body = JsonDocument.Parse(OpenCodeSessionJson.SerializeCreateSessionRequest(new OpenCodeCreateSessionRequest
        {
            Directory = @"D:\project with spaces", Title = "Native contract", Model = "provider/native-model", Agent = "plan"
        }));
        CheckObject(body.RootElement, schema.RootElement);

        static void CheckObject(JsonElement value, JsonElement definition)
        {
            Assert.Equal(JsonValueKind.Object, value.ValueKind);
            var properties = definition.GetProperty("properties");
            if (definition.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray()) Assert.True(value.TryGetProperty(name.GetString()!, out _));
            foreach (var field in value.EnumerateObject())
            {
                Assert.True(properties.TryGetProperty(field.Name, out var fieldDefinition), $"Unexpected native field: {field.Name}");
                if (fieldDefinition.GetProperty("type").GetString() == "object") CheckObject(field.Value, fieldDefinition);
                else Assert.Equal(JsonValueKind.String, field.Value.ValueKind);
            }
        }
    }

    [Fact]
    public void CreateSessionRequest_OmitsEmptyOptionalFields()
    {
        var json = OpenCodeSessionJson.SerializeCreateSessionRequest(new OpenCodeCreateSessionRequest());

        using var document = JsonDocument.Parse(json);

        Assert.Empty(document.RootElement.EnumerateObject());
    }

    [Fact]
    public void PromptRequest_SerializesTextPartAndModelDescriptor()
    {
        var request = new OpenCodePromptRequest
        {
            Prompt = "List the files in the demo-app folder.",
            Model = "test-provider/test-model",
            Agent = "build"
        };

        var json = OpenCodeSessionJson.SerializePromptRequest(request);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var parts = root.GetProperty("parts");
        Assert.Equal(request.MessageId, root.GetProperty("messageID").GetString());
        Assert.StartsWith("msg", request.MessageId);
        Assert.NotEqual(request.MessageId, new OpenCodePromptRequest { Prompt = "next" }.MessageId);
        Assert.Equal(JsonValueKind.Array, parts.ValueKind);

        var part = Assert.Single(parts.EnumerateArray());
        Assert.Equal("text", part.GetProperty("type").GetString());
        Assert.Equal("List the files in the demo-app folder.", part.GetProperty("text").GetString());

        var model = root.GetProperty("model");
        Assert.Equal("test-provider", model.GetProperty("providerID").GetString());
        Assert.Equal("test-model", model.GetProperty("modelID").GetString());
        Assert.Equal("build", root.GetProperty("agent").GetString());
    }

    [Fact]
    public void PromptRequest_WhenModelIsPlainLiteral_SerializesModelIdOnly()
    {
        var json = OpenCodeSessionJson.SerializePromptRequest(
            new OpenCodePromptRequest { Prompt = "Hello", Model = "test-model" });

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var model = root.GetProperty("model");
        Assert.False(model.TryGetProperty("providerID", out _));
        Assert.Equal("test-model", model.GetProperty("modelID").GetString());
        Assert.False(root.TryGetProperty("agent", out _));
    }

    [Fact]
    public void PromptRequest_SerializesEmptyPromptTextAsTextPart()
    {
        var request = new OpenCodePromptRequest { Prompt = string.Empty };

        var json = OpenCodeSessionJson.SerializePromptRequest(request);

        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            string.Empty,
            document.RootElement.GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void PermissionReply_DeclaresStandardResponsesAndSerializes()
    {
        Assert.Equal(
            new[] { "always", "once", "reject" },
            OpenCodePermissionReply.StandardResponses.OrderBy(value => value, StringComparer.Ordinal));

        var once = OpenCodePermissionReply.AllowOnce();
        Assert.Equal("once", once.Response);
        Assert.False(once.Remember);
        Assert.True(once.IsStandard);
        Assert.True(once.IsValid);

        var always = OpenCodePermissionReply.AllowAlways();
        Assert.Equal("always", always.Response);
        Assert.True(always.Remember);

        var deny = OpenCodePermissionReply.Deny();
        Assert.Equal("reject", deny.Response);

        var json = OpenCodeSessionJson.SerializePermissionReply(always);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("always", document.RootElement.GetProperty("reply").GetString());
        Assert.False(document.RootElement.TryGetProperty("response", out _));
        Assert.False(document.RootElement.TryGetProperty("remember", out _));
    }

    [Fact]
    public void PermissionReply_CustomLiteralIsAllowedButNotStandard()
    {
        var custom = new OpenCodePermissionReply { Response = "once-and-remember" };

        Assert.True(custom.IsValid);
        Assert.False(custom.IsStandard);
    }

    [Fact]
    public void SessionLifecycleOptions_DefaultsAndValidation()
    {
        var options = new OpenCodeSessionLifecycleOptions();

        Assert.Equal(TimeSpan.FromMinutes(5), options.TurnTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ConnectionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.CancellationTimeout);

        options.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenCodeSessionLifecycleOptions { TurnTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenCodeSessionLifecycleOptions { ConnectionTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenCodeSessionLifecycleOptions { CancellationTimeout = TimeSpan.Zero }.Validate());
    }

    [Fact]
    public void ApiPaths_BuildSessionPromptAbortForkAndPermissionEndpoints()
    {
        Assert.Equal("/session", OpenCodeApiPaths.Sessions);
        Assert.Equal("/session/ses_1", OpenCodeApiPaths.Session("ses_1"));
        Assert.Equal("/session/ses_1/fork", OpenCodeApiPaths.SessionFork("ses_1"));
        Assert.Equal("/session/ses_1/abort", OpenCodeApiPaths.SessionAbort("ses_1"));
        Assert.Equal("/session/ses_1/prompt_async", OpenCodeApiPaths.SessionPromptAsync("ses_1"));
        Assert.Equal("/permission/per_1/reply", OpenCodeApiPaths.PermissionReply("per_1"));
        Assert.Equal("/event", OpenCodeApiPaths.SessionEvent("ses_1"));
        Assert.Equal("/session/ses%2F1", OpenCodeApiPaths.Session("ses/1"));
    }

    [Fact]
    public void TurnResult_DeclaresClosedStatusSet()
    {
        var result = new TurnResult
        {
            SessionId = "ses_1",
            OutputText = "done",
            Status = TurnResult.CompletedStatus
        };

        Assert.Equal("Completed", TurnResult.CompletedStatus);
        Assert.Equal("Cancelled", TurnResult.CancelledStatus);
        Assert.Equal("Failed", TurnResult.FailedStatus);
        Assert.Empty(result.ToolCalls);
        Assert.Null(result.FinishReason);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public void AddOpenCodeBackend_RegistersSessionLifecycleService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessSupervisor, StubProcessSupervisor>();
        services.AddOpenCodeBackend();

        using var provider = services.BuildServiceProvider();

        var lifecycle = provider.GetRequiredService<IOpenCodeSessionLifecycleService>();

        Assert.IsType<OpenCodeSessionLifecycleService>(lifecycle);
        Assert.Same(lifecycle, provider.GetRequiredService<IOpenCodeSessionLifecycleService>());
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(FindRepositoryRoot(), "docs", "protocols", "opencode", fileName);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LLMWorkGUI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root containing 'LLMWorkGUI.sln' was not found.");
    }

    private sealed class StubProcessSupervisor : IProcessSupervisor
    {
        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
