using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>
/// Studio implementation over an immutable template store (durable SQLite in the product composition).
/// The service never writes to
/// <see cref="IWorkflowRunRepository"/> or <see cref="IWorkflowPackageRepository"/>: they are read only to
/// prove in the assignment result that the active run and the imported package hashes were preserved.
/// </summary>
public sealed class WorkflowStudioService : IWorkflowStudioService
{
    public const string LegacyStandardTemplateId = "workflow-standard-development";
    public const string StandardTemplateId = "workflow-standard-development-bounded";
    public const int StandardTemplateVersion = 1;
    public const string DefaultPrimaryRouteId = "route-opencode";

    private readonly IWorkflowGraphValidator _graphValidator;
    private readonly IWorkflowTemplateStore _templateStore;
    private readonly IWorkflowRunRepository? _runRepository;
    private readonly IWorkflowPackageRepository? _packageRepository;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<WorkflowTemplateDefinition> _builtInTemplates;

    public WorkflowStudioService(
        IWorkflowGraphValidator? graphValidator = null,
        IWorkflowTemplateStore? templateStore = null,
        TimeProvider? timeProvider = null,
        IWorkflowRunRepository? runRepository = null,
        IWorkflowPackageRepository? packageRepository = null)
    {
        _graphValidator = graphValidator ?? new WorkflowGraphValidator();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _runRepository = runRepository;
        _packageRepository = packageRepository;
        _builtInTemplates = Array.AsReadOnly(new[] { CreateStandardTemplate() });
        _templateStore = templateStore
            ?? new InMemoryWorkflowTemplateStore(_builtInTemplates);
    }

    public IReadOnlyList<string> DocumentReviewerRoles => WorkflowStudioDocumentRules.RequiredReviewerRoles;

    public IReadOnlyList<WorkflowTemplateDefinition> GetBuiltInTemplates() => _builtInTemplates;

    public WorkflowGraphValidationReport ValidateTemplateGraph(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        try
        {
            _graphValidator.Validate(graph);

            return WorkflowGraphValidationReport.Valid;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return WorkflowGraphValidationReport.Invalid(new[] { exception.Message });
        }
    }

    public async Task<IReadOnlyList<WorkflowTemplateDefinition>> ListTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        var stored = await _templateStore.ListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<WorkflowTemplateDefinition>(_builtInTemplates.Count + stored.Count);
        var builtInKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var builtIn in _builtInTemplates)
        {
            result.Add(builtIn);
            builtInKeys.Add(CreateKey(builtIn.TemplateId, builtIn.Version));
        }

        foreach (var template in stored)
        {
            if (builtInKeys.Add(CreateKey(template.TemplateId, template.Version)))
            {
                result.Add(template);
            }
        }

        return result;
    }

    public async Task<WorkflowTemplateDefinition> GetRequiredTemplateAsync(
        string templateId,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        ApplicationGuard.NotBlank(templateId, nameof(templateId));

        var builtIn = _builtInTemplates.FirstOrDefault(
            template => string.Equals(template.TemplateId, templateId, StringComparison.Ordinal)
                && (version is null || template.Version == version));

        if (version is not null && builtIn is not null)
        {
            return builtIn;
        }

        var stored = version is null
            ? await _templateStore.GetLatestAsync(templateId, cancellationToken).ConfigureAwait(false)
            : await _templateStore
                .GetAsync(templateId, version.Value, cancellationToken)
                .ConfigureAwait(false);

        // A built-in version remains immutable, but its identity can also have later user-owned
        // versions. An unversioned lookup must consider both sources before choosing the newest.
        var resolved = version is null && builtIn is not null
            && (stored is null || builtIn.Version >= stored.Version)
                ? builtIn
                : stored;

        return resolved ?? throw new WorkflowValidationException(
            $"The workflow template '{templateId}'"
            + (version is null ? string.Empty : $" version {version}")
            + " does not exist in the studio store.");
    }

    public async Task<WorkflowTemplateDefinition> SaveTemplateAsync(
        WorkflowTemplateDefinition template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        var report = ValidateTemplateGraph(template.Graph);

        if (!report.IsValid)
        {
            throw new WorkflowValidationException(report.Summary);
        }

        if (template.IsBuiltIn || _builtInTemplates.Any(builtIn =>
                string.Equals(builtIn.TemplateId, template.TemplateId, StringComparison.Ordinal)
                && builtIn.Version == template.Version))
        {
            throw new WorkflowValidationException(
                $"Template '{template.TemplateId}' is built in and cannot be saved. Clone it to create a "
                + "user-owned template, then save the clone.");
        }

        await _templateStore.SaveAsync(template, cancellationToken).ConfigureAwait(false);

        return template;
    }

    public async Task<WorkflowTemplateDefinition> CloneTemplateAsync(
        string templateId,
        string newTemplateId,
        string newDisplayName,
        int? sourceVersion = null,
        CancellationToken cancellationToken = default)
    {
        var source = await GetRequiredTemplateAsync(templateId, sourceVersion, cancellationToken)
            .ConfigureAwait(false);

        return await SaveTemplateAsync(source.Clone(newTemplateId, newDisplayName), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WorkflowTemplateDefinition> CreateTemplateVersionAsync(
        string templateId,
        int newVersion,
        int? sourceVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (newVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(newVersion), "A template version starts at 1.");
        }

        var source = await GetRequiredTemplateAsync(templateId, sourceVersion, cancellationToken)
            .ConfigureAwait(false);

        return await SaveTemplateAsync(source.CreateVersion(newVersion, isBuiltIn: false), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WorkflowTemplateAssignmentResult> AssignTemplateToProjectAsync(
        string projectId,
        string templateId,
        int templateVersion,
        CancellationToken cancellationToken = default)
    {
        ApplicationGuard.NotBlank(projectId, nameof(projectId));

        var template = await GetRequiredTemplateAsync(templateId, templateVersion, cancellationToken)
            .ConfigureAwait(false);

        var activeRun = _runRepository is null
            ? null
            : await _runRepository.GetActiveByProjectIdAsync(projectId, cancellationToken)
                .ConfigureAwait(false);

        var packageHashes = _packageRepository is null
            ? Array.Empty<string>()
            : (await _packageRepository.ListAsync(cancellationToken).ConfigureAwait(false))
                .Select(package => package.OriginalHash)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(hash => hash, StringComparer.Ordinal)
                .ToArray();

        var assignment = new WorkflowTemplateAssignment(
            Guid.NewGuid().ToString("N"),
            projectId,
            template.TemplateId,
            template.Version,
            _timeProvider.GetUtcNow());

        await _templateStore.SaveAssignmentAsync(assignment, cancellationToken).ConfigureAwait(false);

        return new WorkflowTemplateAssignmentResult(
            IsAssigned: true,
            projectId,
            template.TemplateId,
            template.Version,
            assignment.AssignmentId,
            activeRun?.Id,
            activeRun?.CurrentStageId,
            packageHashes,
            Blocker: null);
    }

    /// <summary>
    /// Refuses an AGY <c>agy-profile</c> or Codex <c>CODEX_HOME</c> account-context switch and names why.
    ///
    /// A real switch needs a backend that reports the account, the actual model, a unique route key and
    /// the new native session independently of the request, plus an executable to drive the profile or
    /// home switch. This host has neither: the star-cliproxy build produces no standalone
    /// <c>star-cliproxy.exe</c>, and its response echoes the requested model alias back and reports no
    /// provider, account or unique route key. A caller therefore cannot supply the missing evidence, and
    /// a session id generated here would be a local id rather than a native one.
    ///
    /// The refusal therefore happens before any <c>agy-profile</c> process switch, gateway start,
    /// session or execution write, role binding mutation or route-change event, and it preserves the
    /// active run, the existing session, the pinned role bindings, the account context and the Mirasim
    /// state. The three refusals are distinct: an unusable context, a cancellation, and a well-formed
    /// request that this host cannot prove.
    /// </summary>
    public Task<WorkflowAccountContextSwitchResult> SwitchAccountContextAsync(
        WorkflowAccountContextSwitchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _timeProvider.GetUtcNow();
        var accountId = request.AccountId ?? string.Empty;

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(WorkflowAccountContextSwitchResult.Cancelled(
                request.Kind,
                accountId,
                "The account-context switch was cancelled before it started; no profile, home, gateway, "
                + "session, execution or role binding was touched.",
                now,
                request.MirasimState));
        }

        var contextFailure = ValidateAccountContext(request);

        if (contextFailure is not null)
        {
            return Task.FromResult(WorkflowAccountContextSwitchResult.InvalidContext(
                request.Kind,
                accountId,
                contextFailure,
                now,
                request.MirasimState));
        }

        accountId = request.Kind == AccountContextKind.Codex
            ? accountId
            : request.AgyProfileName ?? accountId;

        var requestedRouteId = string.IsNullOrWhiteSpace(request.RequestedRouteId)
            ? ResolveDefaultRoute(request.Kind)
            : request.RequestedRouteId;

        return Task.FromResult(WorkflowAccountContextSwitchResult.MissingNativeSwitchProof(
            request.Kind,
            accountId,
            request.PreviousNativeSessionId,
            requestedRouteId,
            DescribeMissingNativeSwitchProof(request.Kind),
            now,
            request.MirasimState));
    }

    /// <summary>
    /// Checks that the request describes a usable account context and returns the path-free reason when
    /// it does not. The reason names the condition and never repeats the CODEX_HOME path or any other
    /// caller value that could carry a secret.
    /// </summary>
    private static string? ValidateAccountContext(WorkflowAccountContextSwitchRequest request)
    {
        if (!Enum.IsDefined(request.Kind))
        {
            return "The account-context kind is not supported.";
        }

        var accountId = request.AccountId ?? string.Empty;
        var isCodex = request.Kind == AccountContextKind.Codex;
        var contextFailure = isCodex ? CodexContextFailure : AgyContextFailure;

        try
        {
            if (isCodex)
            {
                var codexHome = request.CodexHomePath ?? string.Empty;

                if (string.IsNullOrWhiteSpace(accountId))
                {
                    return contextFailure;
                }

                if (string.IsNullOrWhiteSpace(codexHome) || !Path.IsPathFullyQualified(codexHome))
                {
                    return contextFailure;
                }

                _ = new CodexAccountContext(accountId, codexHome);
            }
            else
            {
                _ = new AgyAccountContext(request.AgyProfileName ?? accountId);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return contextFailure;
        }

        return null;
    }

    private const string AgyContextFailure =
        "The AGY account context is unusable: a profile name is required and only letters, digits, '-' "
        + "and '_' are allowed, because the profile is selected only through agy-profile (ТЗ §6.11a).";

    private const string CodexContextFailure =
        "The Codex account context is unusable: an account id and an absolute CODEX_HOME path are "
        + "required, and each Codex account uses a separate absolute directory (ТЗ §6.4, §6.11a). The "
        + "path itself is not repeated here.";

    /// <summary>
    /// The refusal text a shipped screen, a log or an acceptance scenario shows. It names the missing
    /// executable and the missing wire contract instead of implying a switch, and it names the state the
    /// refusal preserves.
    /// </summary>
    private static string DescribeMissingNativeSwitchProof(AccountContextKind kind)
    {
        var executable = kind == AccountContextKind.Codex
            ? "no installed Codex CLI bound to a verified CODEX_HOME is reachable through a resolvable "
                + "executable, and the star-cliproxy build ships no standalone star-cliproxy.exe"
            : "no installed agy-profile executable is reachable, and the star-cliproxy build ships no "
                + "standalone star-cliproxy.exe";

        var wireContract =
            "the gateway reports no provider, account, actual model or unique route key independently of "
            + "the request: its response echoes the requested model alias back and carries only a request "
            + "id, so an observed route, an observed account and a native session id cannot be proven";

        return $"The {kind} account-context switch to the requested route is refused, not applied: "
            + $"{executable}, and {wireContract}. A request-supplied route and a locally generated "
            + "session id are not evidence, so the switch is not performed, no native session is created, "
            + "and the active run, the existing session, the pinned role binding, the account context and "
            + "the Mirasim state are unchanged.";
    }

    private static string ResolveDefaultRoute(AccountContextKind kind) =>
        kind == AccountContextKind.Codex
            ? "route-star-cliproxy-codex"
            : "route-star-cliproxy-agy";

    private static string CreateKey(string templateId, int version) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{templateId}@{version}");

    /// <summary>
    /// The shipped standard development template. The service owns it in memory and never lets a user
    /// save over it; a composition that persists templates exposes the same definition to the store so
    /// a project can be pointed at the shipped version as well.
    ///
    /// Every node keeps the four gate fields of its source stage verbatim, so the persisted graph states
    /// the reviewer, approval and artifact requirements of the shipped chain instead of leaving a later
    /// resolver to recover them from a kind or a role name.
    /// </summary>
    public static WorkflowTemplateDefinition CreateStandardTemplate()
    {
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();
        var nodes = scheme.Stages.Select(CreateNode).ToArray();
        var graph = new WorkflowGraph(scheme.InitialStageId, nodes);

        var roleBindings = nodes
            .Select(node => node.RoleBinding)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(role => role, StringComparer.Ordinal)
            .Select(role => new RoleBindingDefinition(role, DefaultPrimaryRouteId))
            .ToArray();

        return new WorkflowTemplateDefinition(
            StandardTemplateId,
            version: StandardTemplateVersion,
            "Standard development workflow",
            "Задача → архитектура → ТЗ → roadmap → review → утверждение → код/UI → тесты → приёмка. "
            + "Старая встроенная версия снята с выбора из-за неограниченных повторов. Прежние назначения и запуски сохранены; новый шаблон выбирается явно. Автоматический возврат на доработку не выполняется.",
            graph,
            roleBindings,
            WorkflowStudioDocumentRules.RequiredDocumentKinds,
            isBuiltIn: true,
            DateTimeOffset.UnixEpoch);
    }

    /// <summary>Recognizes only the full historical RC9 graph, for read-only listing quarantine.</summary>
    public static bool IsRetiredStandardGraphSnapshot(string graphJson)
    {
        var stages = WorkflowScheme.CreateStandardDevelopmentScheme().Stages;
        var legacy = new WorkflowGraph(WorkflowScheme.TaskSpecificationStageId,
            stages.Select(stage => new WorkflowNodeDefinition(
                stage.StageId, MapNodeKind(stage.StageKind), stage.DisplayName, stage.RequiredRole,
                primaryRouteId: DefaultPrimaryRouteId,
                retryBudget: stage.StageId == WorkflowScheme.CodeAndUiStageId ? 1 : 0,
                successTargetNodeId: stage.NextStageId, failureTargetNodeId: stage.FailureStageId,
                gateMetadata: WorkflowNodeGateMetadata.CreateFrom(stage))).ToArray());
        try
        {
            var stored = WorkflowGraphSnapshot.ReadStoredTemplateGraph(graphJson, LegacyStandardTemplateId, 1);
            return string.Equals(WorkflowGraphSnapshot.Serialize(legacy), WorkflowGraphSnapshot.Serialize(stored),
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is WorkflowValidationException or ArgumentException)
        {
            return false;
        }
    }

    private static WorkflowNodeDefinition CreateNode(WorkflowStageDefinition stage) =>
        new(
            stage.StageId,
            MapNodeKind(stage.StageKind),
            stage.DisplayName,
            stage.RequiredRole,
            primaryRouteId: DefaultPrimaryRouteId,
            successTargetNodeId: stage.NextStageId,
            // The pinned stage runner cannot consume Retry budgets or execute backward failure routes.
            // A rejected gate stays blocked; the shipped template therefore declares no automatic rework loop.
            failureTargetNodeId: stage.FailureStageId == WorkflowScheme.CodeAndUiStageId
                ? null
                : stage.FailureStageId,
            gateMetadata: WorkflowNodeGateMetadata.CreateFrom(stage));

    private static WorkflowNodeKind MapNodeKind(WorkflowStageKind stageKind) => stageKind switch
    {
        WorkflowStageKind.TaskSpecification => WorkflowNodeKind.Prompt,
        WorkflowStageKind.Architecture => WorkflowNodeKind.Prompt,
        WorkflowStageKind.TechnicalSpecification => WorkflowNodeKind.Prompt,
        WorkflowStageKind.Roadmap => WorkflowNodeKind.Prompt,
        WorkflowStageKind.DocumentReview => WorkflowNodeKind.Review,
        WorkflowStageKind.UserApproval => WorkflowNodeKind.ApprovalGate,
        WorkflowStageKind.Implementation => WorkflowNodeKind.Writer,
        WorkflowStageKind.UiAcceptance => WorkflowNodeKind.ValidationCommand,
        WorkflowStageKind.FinalVerification => WorkflowNodeKind.TerminalOutcome,
        _ => WorkflowNodeKind.Prompt
    };
}
