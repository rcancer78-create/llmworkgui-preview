using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// Deterministic adaptation prompt builder enforcing the ТЗ §6.14 obligations and the strict JSON
/// result schema. Excluded file bodies are never written into the prompt.
/// </summary>
public sealed class WorkflowAdaptationPromptBuilder : IAdaptationPromptBuilder
{
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        WriteIndented = true
    };

    public string BuildSystemPrompt(AdaptationGoal goal, bool allowExpandedSemanticScope)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Ты адаптируешь существующий workflow под подключённые модели.");
        builder.AppendLine("Обязательные требования:");
        builder.AppendLine("- Сохранить назначение и структуру workflow.");
        builder.AppendLine("- Менять только model/provider bindings и необходимые команды запуска/config artifacts.");
        builder.AppendLine("- Назначить доступные модели на существующие роли executor/reviewer/escalation с учётом capabilities и выбранной цели адаптации.");
        builder.AppendLine(
            $"- Не менять роли, этапы, правила качества и escalation semantics без отдельного разрешения (Expanded Semantic Scope = {FormatScopeFlag(allowExpandedSemanticScope)}).");
        builder.AppendLine(allowExpandedSemanticScope
            ? "- Expanded Semantic Scope подтверждён пользователем для этого запуска, но он не отменяет обязательного отчёта: любое изменение этапов, quality gates или escalation semantics всё равно должно быть помечено isSemanticChange = true и описано в rationale."
            : "- Изменение этапов, quality gates или escalation semantics обнаруживается сравнением реальных файлов пакета, а не твоим флагом: isSemanticChange = false не отменяет блокировку DisallowedSemanticChange.");
        builder.AppendLine("- Объяснить каждую замену.");
        builder.AppendLine("- Отметить невозможные сопоставления.");
        builder.AppendLine("- Не активировать результат.");
        builder.AppendLine($"Цель адаптации: {goal.ToWireName()} ({goal.ToDisplayName()}).");
        builder.AppendLine(GetGoalGuidance(goal));

        return builder.ToString();
    }

    public string BuildUserPrompt(AdaptationPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var excludedFiles = new HashSet<string>(context.ExcludedFiles, StringComparer.Ordinal);
        var builder = new StringBuilder();

        builder.AppendLine($"Source workflow version: {context.SourceVersionId}");
        builder.AppendLine($"Workflow package: {context.SourcePackageName}");
        builder.AppendLine($"Adaptation goal: {context.Goal.ToWireName()}");
        builder.AppendLine(
            $"Expanded Semantic Scope: {FormatScopeFlag(context.AllowExpandedSemanticScope)}");
        builder.AppendLine();

        builder.AppendLine("Sanitized provider/model capability catalog (credentials are never included):");
        builder.AppendLine(JsonSerializer.Serialize(context.Catalog, CatalogJsonOptions));
        builder.AppendLine();

        builder.AppendLine("Files included in the payload:");

        foreach (var pair in context.FileContents.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (excludedFiles.Contains(pair.Key))
            {
                continue;
            }

            builder.AppendLine($"--- {pair.Key} ---");
            builder.AppendLine(pair.Value);
        }

        builder.AppendLine();

        builder.AppendLine("Files excluded from the payload (bodies omitted; never reconstruct their contents):");

        if (context.ExcludedFiles.Count == 0)
        {
            builder.AppendLine("- (none)");
        }
        else
        {
            foreach (var path in context.ExcludedFiles.OrderBy(path => path, StringComparer.Ordinal))
            {
                builder.AppendLine($"- {path}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Верни ровно один JSON-объект строго по этой схеме:");
        builder.AppendLine("""
            {
              "mappings": [
                {
                  "role": "Executor",
                  "originalRoute": "string",
                  "targetRoute": "string",
                  "targetModelId": "string",
                  "rationale": "string",
                  "isSemanticChange": false,
                  "blockerKind": null
                }
              ],
              "rationale": "string",
              "warnings": ["string"],
              "blockers": ["string"],
              "fileModifications": {
                "prompts/executor.md": "полное новое содержимое файла"
              }
            }
            """);
        builder.AppendLine("Правила ответа:");
        builder.AppendLine("- mappings содержит только существующие роли executor/reviewer/escalation/coordinator.");
        builder.AppendLine("- isSemanticChange = true только при изменении ответственности ролей, этапов, quality gates или escalation semantics; такое изменение является blocker (DisallowedSemanticChange).");
        builder.AppendLine("- blockerKind принимает значения MissingModel, MissingCapability, DisallowedSemanticChange, DetectedSecret, InvalidSchema, Other или null.");
        builder.AppendLine("- fileModifications опционален: карта относительных путей к полному новому содержимому файла; не включай пути за пределы workflow и не восстанавливай содержимое excluded-файлов.");
        builder.AppendLine("- На каждом ходе кандидат заново создаётся из исходной версии: fileModifications должен содержать полный набор желаемых изменений относительно оригинала. Чтобы сохранить изменения предыдущего хода, включи эти файлы снова; отсутствующие в карте файлы остаются исходными.");
        builder.AppendLine("- Файлы, задающие этапы, quality gates, escalation semantics или ответственность ролей, меняй только при Expanded Semantic Scope = true и только с isSemanticChange = true; при Expanded Semantic Scope = false не изменяй их вовсе.");
        builder.AppendLine("- Не добавляй никакой текст вне JSON-объекта.");

        return builder.ToString();
    }

    private static string FormatScopeFlag(bool allowExpandedSemanticScope) =>
        allowExpandedSemanticScope ? "true" : "false";

    private static string GetGoalGuidance(AdaptationGoal goal)
    {
        return goal switch
        {
            AdaptationGoal.CostSaving =>
                "Экономия токенов: предпочитай более дешёвые модели для некритичных ролей и снижай reasoning effort там, где это допустимо.",
            AdaptationGoal.Quality =>
                "Качество: предпочитай модели с сильными reasoning capabilities для ролей reviewer и coordinator.",
            AdaptationGoal.Speed =>
                "Скорость: предпочитай высокоскоростные режимы и модели с низкой задержкой.",
            AdaptationGoal.Balanced =>
                "Баланс: балансируй стоимость, качество и скорость, не ухудшая критичные роли.",
            _ => throw new ArgumentOutOfRangeException(nameof(goal), goal, "Unknown adaptation goal.")
        };
    }
}
