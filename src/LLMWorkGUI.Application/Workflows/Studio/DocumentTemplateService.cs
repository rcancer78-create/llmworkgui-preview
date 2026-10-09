using System.Collections.Concurrent;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>
/// Default document template service: the seven standard definitions, an in-memory draft store with
/// version/hash tracking, manual edits that reset stale decisions and a fail-closed pre-send preview
/// backed by <see cref="IWorkflowSecretScanner"/>.
/// </summary>
public sealed class DocumentTemplateService : IDocumentTemplateService
{
    private readonly IWorkflowSecretScanner? _secretScanner;
    private readonly TimeProvider _timeProvider;
    private readonly IUserApprovalIdentity? _userApprovalIdentity;
    private readonly Func<string> _draftIdFactory;
    private readonly IReadOnlyDictionary<DocumentTemplateKind, DocumentTemplateDefinition> _templates;
    private readonly ConcurrentDictionary<string, WorkflowDocumentDraft> _drafts =
        new(StringComparer.Ordinal);

    public DocumentTemplateService(
        IWorkflowSecretScanner? secretScanner = null,
        TimeProvider? timeProvider = null,
        Func<string>? draftIdFactory = null,
        IUserApprovalIdentity? userApprovalIdentity = null)
    {
        _secretScanner = secretScanner;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _userApprovalIdentity = userApprovalIdentity;
        _draftIdFactory = draftIdFactory ?? (() => "draft-" + Guid.NewGuid().ToString("N"));

        var templates = CreateStandardTemplates();
        _templates = templates.ToDictionary(template => template.Kind);
    }

    public IReadOnlyList<DocumentTemplateDefinition> GetStandardTemplates() =>
        _templates.Values.OrderBy(template => template.Kind).ToArray();

    public DocumentTemplateDefinition GetRequiredTemplate(DocumentTemplateKind kind) =>
        _templates.TryGetValue(kind, out var template)
            ? template
            : throw new WorkflowValidationException($"The document template '{kind}' is not declared.");

    public IReadOnlyList<WorkflowDocumentDraft> ListDrafts() =>
        _drafts.Values.OrderBy(draft => draft.Kind).ThenBy(draft => draft.DraftId, StringComparer.Ordinal)
            .ToArray();

    public WorkflowDocumentDraft GetRequiredDraft(string draftId)
    {
        ApplicationGuard.NotBlank(draftId, nameof(draftId));

        return _drafts.TryGetValue(draftId, out var draft)
            ? draft
            : throw new WorkflowValidationException($"The document draft '{draftId}' does not exist.");
    }

    public Task<WorkflowDocumentDraft> GenerateDraftAsync(
        DocumentTemplateKind kind,
        string title,
        string? initialContent = null,
        CancellationToken cancellationToken = default)
    {
        ApplicationGuard.NotBlank(title, nameof(title));
        cancellationToken.ThrowIfCancellationRequested();

        var template = GetRequiredTemplate(kind);
        var now = _timeProvider.GetUtcNow();
        var draftId = _draftIdFactory();

        if (string.IsNullOrWhiteSpace(draftId))
        {
            throw new InvalidOperationException("The draft id factory returned an empty draft id.");
        }

        var draft = new WorkflowDocumentDraft(
            draftId,
            kind,
            title,
            initialContent ?? template.DefaultTemplateBody,
            version: 1,
            now,
            now);

        if (!_drafts.TryAdd(draftId, draft))
        {
            throw new InvalidOperationException($"The document draft '{draftId}' already exists.");
        }

        return Task.FromResult(draft);
    }

    public Task<WorkflowDocumentDraft> UpdateDraftAsync(
        string draftId,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        var draft = GetRequiredDraft(draftId);
        draft.UpdateContent(content, _timeProvider.GetUtcNow());

        return Task.FromResult(draft);
    }

    public DocumentCompletenessEvaluation EvaluateCompleteness(string draftId)
    {
        var draft = GetRequiredDraft(draftId);
        var template = GetRequiredTemplate(draft.Kind);
        var missing = template.RequiredSections
            .Where(section => !ContainsSection(draft.Content, section))
            .ToArray();

        return new DocumentCompletenessEvaluation(
            missing.Length == 0,
            missing,
            template.CompletenessCriteria,
            missing.Length == 0
                ? $"All {template.RequiredSections.Count} required sections are present in v{draft.Version}."
                : $"Missing required sections: {string.Join(", ", missing)}.");
    }

    public async Task<DocumentPreSendPreview> GeneratePreSendPreviewAsync(
        string draftId,
        CancellationToken cancellationToken = default)
    {
        var draft = GetRequiredDraft(draftId);
        var template = GetRequiredTemplate(draft.Kind);

        if (_secretScanner is null)
        {
            return new DocumentPreSendPreview(
                draft.DraftId,
                draft.Kind,
                draft.Title,
                draft.ContentHash,
                draft.Version,
                Content: string.Empty,
                ScanReport: WorkflowSecretScanReport.Empty,
                HasScanner: false,
                IsBlocked: true,
                RedactedLines: Array.Empty<string>(),
                Summary: "No secret scanner is configured, so the pre-send preview is blocked until the "
                    + "content can be scanned.");
        }

        var content = draft.Content;
        var contentHash = draft.ContentHash;
        var version = draft.Version;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"drafts/{draft.Kind}.md"] = System.Text.Encoding.UTF8.GetBytes(content)
        };

        var report = await _secretScanner
            .ScanFilesAsync(files, cancellationToken)
            .ConfigureAwait(false);

        if (draft.Version != version || !string.Equals(draft.ContentHash, contentHash, StringComparison.Ordinal))
        {
            throw new WorkflowValidationException(
                "The draft changed during the secret scan. Generate a new preview for the current content.");
        }

        var (redactedContent, redactedLines) = RedactFindings(content, report, $"drafts/{draft.Kind}.md");

        return new DocumentPreSendPreview(
            draft.DraftId,
            draft.Kind,
            draft.Title,
            contentHash,
            version,
            redactedContent,
            report,
            HasScanner: true,
            IsBlocked: report.HasFindings,
            redactedLines,
            report.HasFindings
                ? $"{report.Findings.Count} secret finding(s) detected; sending is blocked until the "
                    + "content is fixed. Flagged lines are redacted in this preview."
                : "No secret findings detected; the preview shows the payload that would be sent.");
    }

    public Task<WorkflowDocumentDraft> AddReviewVerdictAsync(
        string draftId,
        ReviewerVerdictRecord verdict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        cancellationToken.ThrowIfCancellationRequested();

        var draft = GetRequiredDraft(draftId);

        if (!string.Equals(verdict.DocumentHash, draft.ContentHash, StringComparison.Ordinal))
        {
            throw new WorkflowValidationException(
                $"Reviewer '{verdict.ReviewerRole}' reviewed '{verdict.DocumentHash}', but the current "
                + $"document hash is '{draft.ContentHash}'. A verdict for a superseded hash cannot be "
                + "attached to the draft.");
        }

        draft.RecordReviewerVerdict(verdict, _timeProvider.GetUtcNow());

        return Task.FromResult(draft);
    }

    public Task<WorkflowDocumentDraft> ApproveDraftAsync(
        string draftId,
        UserApprovalEvidence approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();

        var draft = GetRequiredDraft(draftId);

        if (!string.Equals(approval.ArtifactHash, draft.ContentHash, StringComparison.Ordinal))
        {
            throw new WorkflowValidationException(
                $"The user approval references '{approval.ArtifactHash}', but the current document hash "
                + $"is '{draft.ContentHash}'. Approve the current hash instead.");
        }

        var approver = _userApprovalIdentity?.GetCurrentApproverIdentity();
        if (string.IsNullOrWhiteSpace(approver))
        {
            throw new WorkflowValidationException(
                "The local Windows logon identity could not be established, so no draft approval is recorded.");
        }

        var now = _timeProvider.GetUtcNow();
        var stampedApproval = new UserApprovalEvidence(
            approval.ApprovalId, approver, approval.StageId, approval.ArtifactHash,
            approval.Decision, approval.Comment, now);
        draft.RecordUserApproval(stampedApproval, now);

        return Task.FromResult(draft);
    }

    private static bool ContainsSection(string content, string section) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Any(line => string.Equals(line.Trim(), $"## {section}", StringComparison.OrdinalIgnoreCase));

    private static (string Content, IReadOnlyList<string> RedactedLines) RedactFindings(
        string content,
        WorkflowSecretScanReport report,
        string expectedPath)
    {
        if (!report.HasFindings)
        {
            return (content, Array.Empty<string>());
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (report.Findings.Count == 0 || report.Findings.Any(finding =>
                !string.Equals(finding.RelativePath, expectedPath, StringComparison.Ordinal)
                || finding.LineNumber < 1 || finding.LineNumber > lines.Length))
            return ("[REDACTED:unmappable secret scan]", new[] { "unmappable secret scan" });
        var redacted = new List<string>();

        foreach (var finding in report.Findings)
        {
            var index = finding.LineNumber - 1;

            if (index < 0 || index >= lines.Length)
            {
                continue;
            }

            lines[index] = $"[REDACTED:{finding.RuleName}]";
            redacted.Add($"{finding.RuleName}@{finding.LineNumber}");
        }

        return (string.Join('\n', lines), redacted);
    }

    private static IReadOnlyList<DocumentTemplateDefinition> CreateStandardTemplates() =>
        new[]
        {
            CreateTemplate(
                DocumentTemplateKind.ProblemStatement,
                "Постановка задачи",
                "Формализует проблему, контекст и критерии успеха до проектирования.",
                new[] { "Контекст", "Проблема", "Целевой пользователь", "Критерии успеха" },
                new[]
                {
                    "Проблема сформулирована без решения.",
                    "Указан целевой пользователь и наблюдаемая боль.",
                    "Критерии успеха измеримы."
                },
                "Write a problem statement only: describe the context, the concrete problem, the target "
                + "user and measurable success criteria. Do not describe an implementation."),
            CreateTemplate(
                DocumentTemplateKind.Architecture,
                "Архитектура",
                "Описывает компоненты, потоки данных и ограничения решения.",
                new[] { "Обзор", "Компоненты", "Потоки данных", "Ограничения" },
                new[]
                {
                    "Перечислены компоненты и их ответственности.",
                    "Описаны основные потоки данных и отказы.",
                    "Зафиксированы ограничения и компромиссы."
                },
                "Describe the system architecture for the approved problem statement: components, "
                + "responsibilities, data flows, failure modes and constraints. Reference existing "
                + "repository modules by name."),
            CreateTemplate(
                DocumentTemplateKind.TechnicalSpecification,
                "Техническое задание",
                "Превращает архитектуру в проверяемые требования.",
                new[]
                {
                    "Область работ",
                    "Функциональные требования",
                    "Нефункциональные требования",
                    "Интерфейсы"
                },
                new[]
                {
                    "Каждое требование проверяемо и пронумеровано.",
                    "Указаны входы/выходы и контракты.",
                    "Явно перечислено, что не входит в объём."
                },
                "Produce a technical specification from the approved architecture: numbered functional and "
                + "non-functional requirements, interfaces, contracts, and an explicit out-of-scope list."),
            CreateTemplate(
                DocumentTemplateKind.Roadmap,
                "Дорожная карта",
                "Разбивает объём работ на этапы и вехи с зависимостями.",
                new[] { "Этапы", "Вехи", "Риски", "Зависимости" },
                new[]
                {
                    "Этапы упорядочены и проверяемы.",
                    "У каждой вехи есть критерий завершения.",
                    "Риски имеют план смягчения."
                },
                "Break the specification into ordered, independently verifiable milestones with completion "
                + "criteria, dependencies and mitigation for each risk."),
            CreateTemplate(
                DocumentTemplateKind.TaskPacket,
                "Пакет задачи",
                "Описывает конкретную задачу или исправление для кодера.",
                new[] { "Задача", "Критерии приёмки", "Затрагиваемые файлы", "Тестовый план" },
                new[]
                {
                    "Задача атомарна и однозначна.",
                    "Критерии приёмки проверяемы.",
                    "Указаны файлы и команды проверки."
                },
                "Write a task or fix packet: the exact change, acceptance criteria, files to touch, and the "
                + "commands that must pass. Never include credentials or production secrets."),
            CreateTemplate(
                DocumentTemplateKind.ReviewReport,
                "Отчёт ревью",
                "Фиксирует отдельные вердикты ревьюеров по хэшу документа.",
                new[] { "Проверенные артефакты", "Замечания", "Вердикт", "Риски" },
                new[]
                {
                    "Указан точный хэш проверенного документа.",
                    "Замечания отделены от вердикта.",
                    "Вердикт — один из approve/reject/request-changes."
                },
                "Produce a review report for a pinned document hash: artifact ids, findings, one explicit "
                + "verdict and residual risks. Do not edit the reviewed document."),
            CreateTemplate(
                DocumentTemplateKind.AcceptanceReport,
                "Отчёт приёмки",
                "Подтверждает выполнение критериев и визуальную приёмку.",
                new[] { "Выполненные проверки", "Визуальная приёмка", "Остаточные риски", "Итог" },
                new[]
                {
                    "Перечислены выполненные команды и результаты.",
                    "Визуальная приёмка подтверждена артефактом.",
                    "Остаточные риски и итог зафиксированы."
                },
                "Produce the acceptance report: executed checks with outcomes, visual acceptance evidence, "
                + "residual risks and the final outcome. Do not claim checks that were not executed.")
        };

    private static DocumentTemplateDefinition CreateTemplate(
        DocumentTemplateKind kind,
        string displayName,
        string description,
        IReadOnlyList<string> sections,
        IReadOnlyList<string> criteria,
        string modelInstructions) =>
        new(
            kind,
            displayName,
            description,
            sections,
            modelInstructions,
            criteria,
            BuildBody(sections));

    private static string BuildBody(IReadOnlyList<string> sections) =>
        string.Join(
            "\n\n",
            sections.Select(section => $"## {section}\n\n_Черновик: заполните раздел «{section}»._"));
}
