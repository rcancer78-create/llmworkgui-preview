using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowAdaptationPromptBuilderTests
{
    private static readonly DateTimeOffset GeneratedAtUtc =
        new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly WorkflowAdaptationPromptBuilder _builder = new();

    [Fact]
    public void BuildSystemPrompt_ContainsVerbatimSpecObligations()
    {
        var prompt = _builder.BuildSystemPrompt(AdaptationGoal.Balanced, allowExpandedSemanticScope: false);

        Assert.Contains("Сохранить назначение и структуру workflow", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Менять только model/provider bindings и необходимые команды запуска/config artifacts",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Назначить доступные модели на существующие роли executor/reviewer/escalation с учётом capabilities и выбранной цели адаптации",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Не менять роли, этапы, правила качества и escalation semantics без отдельного разрешения",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("Объяснить каждую замену", prompt, StringComparison.Ordinal);
        Assert.Contains("Отметить невозможные сопоставления", prompt, StringComparison.Ordinal);
        Assert.Contains("Не активировать результат", prompt, StringComparison.Ordinal);
        Assert.Contains("Expanded Semantic Scope = false", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "isSemanticChange = false не отменяет блокировку DisallowedSemanticChange",
            prompt,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The scope the user actually selected is what the model is told. The previous builder always stated
    /// "false", so a confirmed expanded scope never reached the adapter and the guard could not be
    /// evaluated against the model's real obligations.
    /// </summary>
    [Fact]
    public void BuildSystemPrompt_ReflectsTheConfirmedExpandedScope()
    {
        var prompt = _builder.BuildSystemPrompt(AdaptationGoal.Balanced, allowExpandedSemanticScope: true);

        Assert.Contains("Expanded Semantic Scope = true", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Expanded Semantic Scope = false", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Expanded Semantic Scope подтверждён пользователем для этого запуска",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("isSemanticChange = true", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AdaptationGoal.CostSaving, "дешёвые модели")]
    [InlineData(AdaptationGoal.Quality, "reasoning capabilities")]
    [InlineData(AdaptationGoal.Speed, "высокоскоростные режимы")]
    [InlineData(AdaptationGoal.Balanced, "балансируй")]
    public void BuildSystemPrompt_ContainsGoalSpecificGuidance(AdaptationGoal goal, string expected)
    {
        var prompt = _builder.BuildSystemPrompt(goal, allowExpandedSemanticScope: false);

        Assert.Contains(expected, prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(goal.ToWireName(), prompt, StringComparison.Ordinal);
        Assert.Contains(goal.ToDisplayName(), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSystemPrompt_ThrowsForUndefinedGoal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _builder.BuildSystemPrompt((AdaptationGoal)42, allowExpandedSemanticScope: false));
    }

    [Fact]
    public void BuildUserPrompt_ContainsStrictJsonOutputSchema()
    {
        var context = CreateContext(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["README.md"] = "# Workflow"
            },
            Array.Empty<string>());

        var prompt = _builder.BuildUserPrompt(context);

        Assert.Contains("\"mappings\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"role\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"originalRoute\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"targetRoute\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"targetModelId\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"isSemanticChange\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"blockerKind\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"rationale\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"warnings\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"blockers\"", prompt, StringComparison.Ordinal);
        Assert.Contains("Executor", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserPrompt_IncludesSourceMetadataFileBodiesAndCatalog()
    {
        var context = CreateContext(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["README.md"] = "# Workflow",
                ["prompts/executor.md"] = "You are the executor."
            },
            Array.Empty<string>());

        var prompt = _builder.BuildUserPrompt(context);

        Assert.Contains("version-1", prompt, StringComparison.Ordinal);
        Assert.Contains("Release Workflow", prompt, StringComparison.Ordinal);
        Assert.Contains(AdaptationGoal.Balanced.ToWireName(), prompt, StringComparison.Ordinal);
        Assert.Contains("README.md", prompt, StringComparison.Ordinal);
        Assert.Contains("# Workflow", prompt, StringComparison.Ordinal);
        Assert.Contains("prompts/executor.md", prompt, StringComparison.Ordinal);
        Assert.Contains("You are the executor.", prompt, StringComparison.Ordinal);
        Assert.Contains("acct-r1", prompt, StringComparison.Ordinal);
        Assert.Contains("prov-1", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserPrompt_OmitsExcludedFileBodiesEvenWhenProvided()
    {
        const string secretValue = "sk-abcdefgh12345678";

        var context = CreateContext(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["README.md"] = "# Workflow",
                ["config/secrets.env"] = $"api_key={secretValue}"
            },
            new[] { "config/secrets.env" });

        var prompt = _builder.BuildUserPrompt(context);

        Assert.Contains("config/secrets.env", prompt, StringComparison.Ordinal);
        Assert.Contains("bodies omitted", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretValue, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain($"api_key={secretValue}", prompt, StringComparison.Ordinal);
        Assert.Contains("# Workflow", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserPrompt_ReportsNoExcludedFilesWhenListIsEmpty()
    {
        var context = CreateContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            Array.Empty<string>());

        var prompt = _builder.BuildUserPrompt(context);

        Assert.Contains("(none)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserPrompt_RejectsNullContext()
    {
        Assert.Throws<ArgumentNullException>(() => _builder.BuildUserPrompt(null!));
    }

    private static AdaptationPromptContext CreateContext(
        IReadOnlyDictionary<string, string> fileContents,
        IReadOnlyList<string> excludedFiles)
    {
        return new AdaptationPromptContext(
            "version-1",
            "Release Workflow",
            AdaptationGoal.Balanced,
            CreateCatalog(),
            fileContents,
            excludedFiles);
    }

    private static SanitizedCapabilityCatalog CreateCatalog()
    {
        return new SanitizedCapabilityCatalog(
            new[]
            {
                new SanitizedProviderInfo("prov-1", "OpenCode Local", BackendType.OpenCode, true)
            },
            new[]
            {
                new SanitizedModelInfo(
                    "acct-r1",
                    "Reasoning Model",
                    ModelCapabilityFlags.Chat | ModelCapabilityFlags.ReasoningVariants,
                    new[] { "low", "medium", "high" },
                    new[] { "balanced", "fast" },
                    200_000,
                    HealthState.Healthy,
                    true)
            },
            GeneratedAtUtc);
    }
}
