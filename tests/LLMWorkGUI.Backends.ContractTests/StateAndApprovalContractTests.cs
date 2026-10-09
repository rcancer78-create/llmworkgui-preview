using System.Text.RegularExpressions;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class StateAndApprovalContractTests
{
    private const string InitialState = "—";

    private static readonly string[] ExpectedSessionStates =
    {
        "Draft",
        "Starting",
        "Active",
        "Idle",
        "Ambiguous",
        "Orphaned",
        "Closed"
    };

    private static readonly string[] ExpectedSessionTerminalStates =
    {
        "Closed"
    };

    private static readonly HashSet<(string From, string To)> ExpectedSessionTransitions = new()
    {
        (InitialState, "Draft"),
        ("Draft", "Starting"),
        ("Starting", "Active"),
        ("Active", "Idle"),
        ("Idle", "Active"),
        ("Starting", "Ambiguous"),
        ("Active", "Ambiguous"),
        ("Idle", "Ambiguous"),
        ("Starting", "Orphaned"),
        ("Active", "Orphaned"),
        ("Idle", "Orphaned"),
        ("Draft", "Closed"),
        ("Starting", "Closed"),
        ("Active", "Closed"),
        ("Idle", "Closed"),
        ("Ambiguous", "Closed"),
        ("Orphaned", "Closed"),
        ("Ambiguous", "Active"),
        ("Ambiguous", "Idle"),
        ("Orphaned", "Active"),
        ("Orphaned", "Idle")
    };

    private static readonly (string From, string To)[] ForbiddenSessionTransitions =
    {
        ("Draft", "Active"),
        ("Draft", "Idle"),
        ("Draft", "Ambiguous"),
        ("Draft", "Orphaned"),
        ("Starting", "Idle"),
        ("Active", "Starting"),
        ("Active", "Draft"),
        ("Idle", "Draft"),
        ("Closed", "Draft"),
        ("Closed", "Active")
    };

    private static readonly string[] ExpectedExecutionStates =
    {
        "Queued",
        "Starting",
        "SessionConfirmed",
        "Running",
        "WaitingApproval",
        "Cancelling",
        "Succeeded",
        "Failed",
        "TimedOut",
        "Cancelled",
        "Ambiguous",
        "RouteMismatch"
    };

    private static readonly string[] ExpectedExecutionTerminalStates =
    {
        "Succeeded",
        "Failed",
        "TimedOut",
        "Cancelled",
        "Ambiguous",
        "RouteMismatch"
    };

    private static readonly string[] ExpectedExecutionNonTerminalStates =
    {
        "Queued",
        "Starting",
        "SessionConfirmed",
        "Running",
        "WaitingApproval",
        "Cancelling"
    };

    private static readonly string[] ExpectedExecutionTerminalOutcomes =
    {
        "Succeeded",
        "Failed",
        "TimedOut",
        "Ambiguous",
        "RouteMismatch"
    };

    private static readonly (string From, string To)[] ForbiddenExecutionTransitions =
    {
        ("Queued", "Cancelling"),
        ("Queued", "SessionConfirmed"),
        ("Succeeded", "Running"),
        ("Cancelled", "Succeeded"),
        ("Failed", "Running"),
        ("TimedOut", "Running"),
        ("RouteMismatch", "Succeeded")
    };

    private static readonly string[] ExpectedHealthStates =
    {
        "Healthy",
        "Degraded",
        "CoolingDown",
        "QuarantinedAuto",
        "DisabledManual",
        "ProbeRequired",
        "Recovering",
        "ForcedEnabled"
    };

    private static readonly HashSet<(string From, string To)> ExpectedHealthTransitions = new()
    {
        ("Healthy", "Degraded"),
        ("Healthy", "CoolingDown"),
        ("Degraded", "CoolingDown"),
        ("CoolingDown", "ProbeRequired"),
        ("ProbeRequired", "Recovering"),
        ("Recovering", "Healthy"),
        ("ProbeRequired", "QuarantinedAuto"),
        ("Recovering", "QuarantinedAuto"),
        ("Healthy", "DisabledManual"),
        ("Degraded", "DisabledManual"),
        ("CoolingDown", "DisabledManual"),
        ("ProbeRequired", "DisabledManual"),
        ("Recovering", "DisabledManual"),
        ("QuarantinedAuto", "DisabledManual"),
        ("ForcedEnabled", "DisabledManual"),
        ("DisabledManual", "ProbeRequired"),
        ("CoolingDown", "ForcedEnabled"),
        ("ProbeRequired", "ForcedEnabled"),
        ("QuarantinedAuto", "ForcedEnabled"),
        ("ForcedEnabled", "ProbeRequired"),
        ("ForcedEnabled", "CoolingDown"),
        ("ForcedEnabled", "Recovering"),
        ("ForcedEnabled", "Healthy")
    };

    private static readonly (string From, string To)[] ForbiddenHealthTransitions =
    {
        ("CoolingDown", "Healthy"),
        ("ProbeRequired", "Healthy"),
        ("QuarantinedAuto", "Healthy"),
        ("DisabledManual", "Healthy"),
        ("Healthy", "Recovering"),
        ("DisabledManual", "Recovering"),
        ("QuarantinedAuto", "Recovering")
    };

    private static readonly string[] RequiredNativeEvidenceTokens =
    {
        "server.connected",
        "session.updated",
        "message.updated",
        "message.part.updated",
        "session.idle",
        "prompt_async",
        "POST /session/{sessionID}/abort",
        "POST /permission/{requestID}/reply",
        "acp.initialize",
        "acp.session.new",
        "acp.streaming.updates",
        "acp.approvals",
        "acp.cancel",
        "acp.session.load"
    };

    private static readonly string[] ExpectedCanonicalKinds = Enum.GetNames<NormalizedApprovalKind>();

    private static readonly string[] ExpectedOpenCodeMapping =
    {
        "read file=ReadFile",
        "write/edit file=WriteFile",
        "shell command=ShellCommand",
        "network/MCP/tool=NetworkTool",
        "расширение workspace=WorkspaceExpansion",
        "destructive/high-risk=HighRiskDestructive",
        "любой неизвестный native kind=UnknownHighRisk"
    };

    private static readonly string[] ExpectedCursorMapping =
    {
        "любой native запрос=UnknownHighRisk"
    };

    private static readonly string[] ExpectedNotReportedFields =
    {
        "nativeSessionId",
        "requestedRoute",
        "observedRoute",
        "role",
        "stage",
        "usage",
        "quota",
        "approvals"
    };

    private static readonly string[] RequiredTerminalOutcomeFields =
    {
        "processState",
        "exitCode",
        "terminationReason",
        "startedAt",
        "endedAt",
        "artifacts",
        "sourceHashBefore",
        "sourceHashAfter"
    };

    private static readonly string[] RequiredAdrReferences =
    {
        "ADR-0001",
        "ADR-0002",
        "ADR-0003",
        "ADR-0004",
        "ADR-0005",
        "ADR-0006"
    };

    private static readonly (string Name, string Pattern)[] ForbiddenPatterns =
    {
        ("OpenAI-style API key", @"\bsk-[A-Za-z0-9_-]{12,}\b"),
        ("GitHub token", @"\bgh[pousr]_[A-Za-z0-9]{20,}\b"),
        ("AWS access key", @"\bAKIA[0-9A-Z]{16}\b"),
        ("Slack token", @"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"),
        ("Cursor API key", @"\bcrsr_[A-Za-z0-9_-]{12,}\b"),
        ("Generic credential assignment", @"\b(api[_-]?key|token|secret|password)\b\s*[:=]\s*""[A-Za-z0-9._\-]{8,}"""),
        ("Bearer credential", @"\bBearer\s+[A-Za-z0-9._~+/-]{16,}"),
        ("Private key block", @"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
        ("Windows user profile path", @"[A-Za-z]:\\+Users\\+"),
        ("POSIX home path", @"/home/[A-Za-z0-9._-]+/"),
        ("Non-placeholder email", @"[A-Za-z0-9._%+-]+@(?!example\.com)[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
    };

    private static readonly Regex StateInventoryRowPattern = new(
        @"^\|\s*`(?<state>[A-Za-z][A-Za-z0-9]*)`\s*\|\s*(?<terminal>нет|да)\s*\|\s*(?<description>[^|]+?)\s*\|\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex TransitionRowPattern = new(
        @"^\|\s*(?<from>[^|]*?)\s*\|\s*`(?<to>[A-Za-z][A-Za-z0-9]*)`\s*\|\s*(?<trigger>[^|]+?)\s*\|\s*(?<sideEffects>[^|]+?)\s*\|\s*(?<evidence>[^|]+?)\s*\|\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex BacktickedTokenPattern = new(
        @"`(?<token>[A-Za-z][A-Za-z0-9]*)`",
        RegexOptions.CultureInvariant);

    private static readonly Regex ReconciliationRowPattern = new(
        @"^\|\s*`(?<outcome>[A-Za-z][A-Za-z0-9]*)`\s*\|\s*(?<guard>[^|]+?)\s*\|\s*(?<sessionResult>[^|]+?)\s*\|\s*(?<executionResult>[^|]+?)\s*\|\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex CanonicalKindRowPattern = new(
        @"^\|\s*`(?<kind>[A-Za-z][A-Za-z0-9]*)`\s*\|\s*(?<description>[^|]+?)\s*\|\s*(?<answers>[^|]+?)\s*\|\s*(?<persistent>[^|]+?)\s*\|\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex NativeMappingRowPattern = new(
        @"^\|\s*(?<native>[^|]+?)\s*\|\s*`(?<kind>[A-Za-z][A-Za-z0-9]*)`\s*\|\s*(?<evidence>[^|]+?)\s*\|\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex NotReportedRowPattern = new(
        @"^\|\s*`(?<field>[A-Za-z][A-Za-z0-9]*)`\s*\|\s*`Not reported`\s*\|",
        RegexOptions.CultureInvariant);

    private static readonly Regex CriteriaRowPattern = new(
        @"^\|\s*`(?<id>C(?<number>\d+))`\s*\|\s*(?<criterion>[^|]+?)\s*\|\s*(?<status>[^|]+?)\s*\|\s*(?<evidence>[^|]+?)\s*\|\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex EvidencePathPattern = new(
        @"`(?<path>[A-Za-z0-9._/-]+\.(?:md|json|jsonl|cs|sln|props))`",
        RegexOptions.CultureInvariant);

    [Fact]
    public void SessionStateTable_DeclaresExactStatesAndCompleteTransitions()
    {
        var section = ReadSection(
            StateTablesPath,
            "## 2. Session state machine",
            "## 3. Execution state machine");

        var states = ParseStateInventory(section);

        Assert.Equal(
            ExpectedSessionStates.OrderBy(state => state, StringComparer.Ordinal),
            states.Keys.OrderBy(state => state, StringComparer.Ordinal));

        foreach (var state in ExpectedSessionStates)
        {
            Assert.Equal(ExpectedSessionTerminalStates.Contains(state), states[state]);
        }

        var nonTerminal = states
            .Where(pair => !pair.Value)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        var transitions = ParseTransitions(section, nonTerminal);

        AssertTransitionSet(ExpectedSessionTransitions, transitions, "Session");

        foreach (var forbidden in ForbiddenSessionTransitions)
        {
            Assert.DoesNotContain(forbidden, transitions);
        }

        Assert.DoesNotContain(transitions, transition => transition.From == "Closed");
    }

    [Fact]
    public void ExecutionStateTable_DeclaresExactStatesAndCompleteTransitions()
    {
        var section = ReadSection(
            StateTablesPath,
            "## 3. Execution state machine",
            "## 4. Health state machine");

        var states = ParseStateInventory(section);

        Assert.Equal(
            ExpectedExecutionStates.OrderBy(state => state, StringComparer.Ordinal),
            states.Keys.OrderBy(state => state, StringComparer.Ordinal));

        foreach (var state in ExpectedExecutionStates)
        {
            Assert.Equal(ExpectedExecutionTerminalStates.Contains(state), states[state]);
        }

        var nonTerminal = states
            .Where(pair => !pair.Value)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            nonTerminal.SetEquals(ExpectedExecutionNonTerminalStates),
            "Execution non-terminal state set does not match the normative table.");

        var expected = new HashSet<(string From, string To)> { (InitialState, "Queued") };

        Add(expected, "Queued", "Starting");
        Add(expected, "Starting", "SessionConfirmed");
        Add(expected, "SessionConfirmed", "Running");
        Add(expected, "Running", "WaitingApproval");
        Add(expected, "WaitingApproval", "Running");
        Add(expected, "Starting", "Cancelling");
        Add(expected, "SessionConfirmed", "Cancelling");
        Add(expected, "Running", "Cancelling");
        Add(expected, "WaitingApproval", "Cancelling");
        Add(expected, "Cancelling", "Cancelled");

        foreach (var from in ExpectedExecutionNonTerminalStates)
        {
            foreach (var to in ExpectedExecutionTerminalOutcomes)
            {
                Add(expected, from, to);
            }
        }

        var transitions = ParseTransitions(section, nonTerminal);

        AssertTransitionSet(expected, transitions, "Execution");

        foreach (var forbidden in ForbiddenExecutionTransitions)
        {
            Assert.DoesNotContain(forbidden, transitions);
        }

        foreach (var terminal in ExpectedExecutionTerminalStates)
        {
            Assert.DoesNotContain(transitions, transition => transition.From == terminal);
        }
    }

    [Fact]
    public void HealthStateTable_DeclaresExactStatesAndCompleteTransitions()
    {
        var section = ReadSection(
            StateTablesPath,
            "## 4. Health state machine",
            "## 5. Привязка к реальным событиям backend");

        var states = ParseStateInventory(section);

        Assert.Equal(
            ExpectedHealthStates.OrderBy(state => state, StringComparer.Ordinal),
            states.Keys.OrderBy(state => state, StringComparer.Ordinal));

        foreach (var state in ExpectedHealthStates)
        {
            Assert.False(states[state], $"Health state '{state}' must not be terminal.");
        }

        var transitions = ParseTransitions(section, states.Keys.ToHashSet(StringComparer.Ordinal));

        AssertTransitionSet(ExpectedHealthTransitions, transitions, "Health");

        foreach (var forbidden in ForbiddenHealthTransitions)
        {
            Assert.DoesNotContain(forbidden, transitions);
        }

        var content = File.ReadAllText(StateTablesPath);

        foreach (var token in new[] { "N=3", "W=15 минут", "default 5 минут", "не увеличивают breaker" })
        {
            Assert.Contains(token, content, StringComparison.Ordinal);
        }

        Assert.Contains(
            "CoolingDown` никогда не переходит напрямую в `Healthy",
            content,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransitionTables_BindTransitionsToNativeProtocolEvidence()
    {
        var content = File.ReadAllText(StateTablesPath);

        foreach (var token in RequiredNativeEvidenceTokens)
        {
            Assert.Contains(token, content, StringComparison.Ordinal);
        }

        var reconciliationSection = ReadSection(
            StateTablesPath,
            "### 2.3. Reconciliation outcomes",
            "### 2.4.");

        var outcomes = ParseReconciliationOutcomes(reconciliationSection);

        Assert.Equal(
            new[] { "Ambiguous", "BackendMissing", "Orphaned", "Reattached" },
            outcomes.Keys.OrderBy(outcome => outcome, StringComparer.Ordinal));

        Assert.Contains("immutable binding", outcomes["Reattached"].Guard, StringComparison.Ordinal);
        Assert.Contains("Active", outcomes["Reattached"].SessionResult, StringComparison.Ordinal);
        Assert.Contains("Idle", outcomes["Reattached"].SessionResult, StringComparison.Ordinal);
        Assert.Contains("Ambiguous", outcomes["Ambiguous"].ExecutionResult, StringComparison.Ordinal);
        Assert.Contains("Orphaned", outcomes["BackendMissing"].SessionResult, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalMapping_DeclaresSevenCanonicalKindsAndAnswerContract()
    {
        var section = ReadSection(
            ApprovalMappingPath,
            "## 2. Канонические нормализованные категории",
            "## 3. Mapping OpenCode");

        var kinds = ParseCanonicalKinds(section);

        Assert.Equal(
            ExpectedCanonicalKinds.OrderBy(kind => kind, StringComparer.Ordinal),
            kinds.Keys.OrderBy(kind => kind, StringComparer.Ordinal));

        foreach (var pair in kinds)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(pair.Value.Description),
                $"Canonical kind '{pair.Key}' has no description.");
            Assert.Contains("allow once", pair.Value.Answers, StringComparison.Ordinal);
            Assert.Contains("deny", pair.Value.Answers, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("allow for execution", kinds[nameof(NormalizedApprovalKind.HighRiskDestructive)].Answers, StringComparison.Ordinal);
        Assert.Contains("разовый explicit allow", kinds["UnknownHighRisk"].Answers, StringComparison.Ordinal);
        Assert.Contains("нет", kinds["UnknownHighRisk"].PersistentRule, StringComparison.Ordinal);
        Assert.Contains("Approval Rules", kinds["ReadFile"].PersistentRule, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalMapping_NormalizesOpenCodeAndCursorKindsWithUnknownFallback()
    {
        var content = File.ReadAllText(ApprovalMappingPath);

        Assert.Contains(
            "любой unknown native kind всегда маппится на `UnknownHighRisk`",
            content,
            StringComparison.Ordinal);

        var openCodeSection = ReadSection(
            ApprovalMappingPath,
            "## 3. Mapping OpenCode",
            "## 4. Mapping Cursor ACP");

        var openCodeMapping = ParseNativeMapping(openCodeSection);
        AssertMapping(ExpectedOpenCodeMapping, openCodeMapping, "OpenCode");

        var cursorSection = ReadSection(
            ApprovalMappingPath,
            "## 4. Mapping Cursor ACP",
            "## 5. Ответы, persistent rules и audit");

        var cursorMapping = ParseNativeMapping(cursorSection);
        AssertMapping(ExpectedCursorMapping, cursorMapping, "Cursor");

        Assert.Single(cursorMapping);
        Assert.Equal("UnknownHighRisk", cursorMapping.Values.Single().Kind);
        Assert.Contains("writer-lock exemption", cursorSection, StringComparison.Ordinal);
        Assert.Contains("`allow for execution`", cursorSection, StringComparison.Ordinal);

        foreach (var pair in cursorMapping)
        {
            Assert.Contains(pair.Value.Kind, ExpectedCanonicalKinds);
        }

        Assert.Equal(
            "UnknownHighRisk",
            NormalizeNativeKind(openCodeMapping, "definitely-unknown-native-kind"));
        Assert.Equal(
            "UnknownHighRisk",
            NormalizeNativeKind(cursorMapping, "definitely-unknown-native-kind"));
        Assert.Equal(
            "UnknownHighRisk",
            NormalizeNativeKind(openCodeMapping, string.Empty));
        Assert.Equal(
            "UnknownHighRisk",
            NormalizeNativeKind(cursorMapping, string.Empty));
    }

    [Fact]
    public void LegacyEntrypointBoundary_FixesScratchLockSupervisorAndNotReportedContract()
    {
        var content = File.ReadAllText(LegacyBoundaryPath);

        Assert.Contains("**Статус:** Accepted", content, StringComparison.Ordinal);

        foreach (var token in new[]
                 {
                     "<app-data>/scratch/run/<id>",
                     "checkout writer lock",
                     "второй writer",
                     "Process Supervisor",
                     "Process Supervisor reconciliation",
                     "post-operation source hash check (SHA-256)",
                     "read-only",
                     "supervised opaque process",
                     "Codex CLI/AGY CLI",
                     "UNVERIFIED_PHASE_8",
                     "Phase 8"
                 })
        {
            Assert.Contains(token, content, StringComparison.Ordinal);
        }

        foreach (var outcome in new[] { "Reattached", "Orphaned", "Ambiguous", "BackendMissing" })
        {
            Assert.Contains(outcome, content, StringComparison.Ordinal);
        }

        foreach (var field in RequiredTerminalOutcomeFields)
        {
            Assert.Contains(field, content, StringComparison.Ordinal);
        }

        var notReportedSection = ReadSection(
            LegacyBoundaryPath,
            "## 5. Поля `Not reported`",
            "## 6. Process Supervisor reconciliation");

        var notReportedFields = ParseNotReportedFields(notReportedSection);

        foreach (var field in ExpectedNotReportedFields)
        {
            Assert.Contains(field, notReportedFields);
        }

        Assert.Equal(ExpectedNotReportedFields.Length, notReportedFields.Count);
    }

    [Fact]
    public void Phase0ExitReport_AuditsAllFourteenCriteriaWithExistingEvidence()
    {
        var content = File.ReadAllText(ExitReportPath);

        Assert.Contains("**Статус:** Accepted", content, StringComparison.Ordinal);
        Assert.Contains("Gate Phase 0 → Phase 1: OPEN", content, StringComparison.Ordinal);

        var criteria = new Dictionary<string, (string Criterion, string Status, string Evidence)>(StringComparer.Ordinal);

        foreach (var rawLine in File.ReadAllLines(ExitReportPath))
        {
            var match = CriteriaRowPattern.Match(rawLine.TrimEnd());
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value;
            Assert.True(
                criteria.TryAdd(
                    id,
                    (match.Groups["criterion"].Value.Trim(), match.Groups["status"].Value.Trim(), match.Groups["evidence"].Value.Trim())),
                $"Duplicate Phase 0 criterion id '{id}'.");
        }

        Assert.Equal(14, criteria.Count);

        for (var number = 1; number <= 14; number++)
        {
            var id = $"C{number}";
            Assert.True(criteria.ContainsKey(id), $"Phase 0 exit report is missing criterion '{id}'.");
        }

        foreach (var pair in criteria)
        {
            Assert.Equal("MET", pair.Value.Status);
            Assert.False(string.IsNullOrWhiteSpace(pair.Value.Criterion));
            Assert.False(string.IsNullOrWhiteSpace(pair.Value.Evidence));

            var paths = EvidencePathPattern.Matches(pair.Value.Evidence)
                .Select(match => match.Groups["path"].Value)
                .ToArray();

            Assert.True(paths.Length > 0, $"Criterion '{pair.Key}' has no evidence artifact reference.");

            foreach (var path in paths)
            {
                Assert.True(
                    File.Exists(RepositoryPath(path.Split('/'))),
                    $"Criterion '{pair.Key}' references missing artifact '{path}'.");
            }
        }

        foreach (var adr in RequiredAdrReferences)
        {
            Assert.Contains(adr, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Phase0Artifacts_ContainNoSecretsOrUserProfilePaths()
    {
        foreach (var path in new[]
                 {
                     StateTablesPath,
                     ApprovalMappingPath,
                     LegacyBoundaryPath,
                     ExitReportPath,
                     TestSourcePath
                 })
        {
            var content = File.ReadAllText(path);

            foreach (var (name, pattern) in ForbiddenPatterns)
            {
                Assert.False(
                    Regex.IsMatch(content, pattern, RegexOptions.CultureInvariant),
                    $"Artifact '{Path.GetFileName(path)}' matches forbidden pattern '{name}'.");
            }
        }
    }

    private static void AssertTransitionSet(
        HashSet<(string From, string To)> expected,
        HashSet<(string From, string To)> actual,
        string machineName)
    {
        var missing = expected.Except(actual).ToArray();
        var unexpected = actual.Except(expected).ToArray();

        Assert.True(
            missing.Length == 0 && unexpected.Length == 0,
            $"{machineName} transitions mismatch. Missing: [{string.Join(", ", missing.Select(pair => pair.From + "->" + pair.To))}]. " +
            $"Unexpected: [{string.Join(", ", unexpected.Select(pair => pair.From + "->" + pair.To))}].");
    }

    private static void Add(HashSet<(string From, string To)> transitions, string from, string to)
    {
        Assert.True(transitions.Add((from, to)), $"Duplicate expected transition '{from}' -> '{to}'.");
    }

    private static Dictionary<string, bool> ParseStateInventory(string section)
    {
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = StateInventoryRowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var state = match.Groups["state"].Value;
            var terminal = string.Equals(match.Groups["terminal"].Value, "да", StringComparison.Ordinal);
            var description = match.Groups["description"].Value.Trim();

            Assert.False(string.IsNullOrWhiteSpace(description), $"State '{state}' has no description.");
            Assert.True(states.TryAdd(state, terminal), $"Duplicate state '{state}'.");
        }

        Assert.NotEmpty(states);
        return states;
    }

    private static HashSet<(string From, string To)> ParseTransitions(
        string section,
        IReadOnlySet<string> nonTerminalStates)
    {
        var transitions = new HashSet<(string From, string To)>();

        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = TransitionRowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var to = match.Groups["to"].Value;
            var fromCell = match.Groups["from"].Value.Trim();
            var trigger = match.Groups["trigger"].Value.Trim();
            var sideEffects = match.Groups["sideEffects"].Value.Trim();
            var evidence = match.Groups["evidence"].Value.Trim();

            Assert.False(string.IsNullOrWhiteSpace(trigger), $"Transition to '{to}' has no trigger or guard.");
            Assert.False(string.IsNullOrWhiteSpace(sideEffects), $"Transition to '{to}' has no side effects.");
            Assert.False(string.IsNullOrWhiteSpace(evidence), $"Transition to '{to}' has no native evidence.");

            List<string> fromStates;

            if (fromCell.Contains("любое нетерминальное", StringComparison.Ordinal))
            {
                fromStates = nonTerminalStates.ToList();
                Assert.NotEmpty(fromStates);
            }
            else if (string.Equals(fromCell, InitialState, StringComparison.Ordinal) ||
                     string.Equals(fromCell, "-", StringComparison.Ordinal))
            {
                fromStates = new List<string> { InitialState };
            }
            else
            {
                fromStates = BacktickedTokenPattern.Matches(fromCell)
                    .Select(token => token.Groups["token"].Value)
                    .ToList();
                Assert.True(fromStates.Count > 0, $"Transition row has no from-state: {line}");
            }

            foreach (var from in fromStates)
            {
                Assert.True(
                    transitions.Add((from, to)),
                    $"Duplicate transition '{from}' -> '{to}'.");
            }
        }

        Assert.NotEmpty(transitions);
        return transitions;
    }

    private static Dictionary<string, (string Guard, string SessionResult, string ExecutionResult)> ParseReconciliationOutcomes(
        string section)
    {
        var outcomes = new Dictionary<string, (string Guard, string SessionResult, string ExecutionResult)>(StringComparer.Ordinal);

        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = ReconciliationRowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var outcome = match.Groups["outcome"].Value;
            var guard = match.Groups["guard"].Value.Trim();
            var sessionResult = match.Groups["sessionResult"].Value.Trim();
            var executionResult = match.Groups["executionResult"].Value.Trim();

            Assert.False(string.IsNullOrWhiteSpace(guard), $"Reconciliation outcome '{outcome}' has no guard.");
            Assert.True(
                outcomes.TryAdd(outcome, (guard, sessionResult, executionResult)),
                $"Duplicate reconciliation outcome '{outcome}'.");
        }

        Assert.NotEmpty(outcomes);
        return outcomes;
    }

    private static Dictionary<string, (string Description, string Answers, string PersistentRule)> ParseCanonicalKinds(
        string section)
    {
        var kinds = new Dictionary<string, (string Description, string Answers, string PersistentRule)>(StringComparer.Ordinal);

        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = CanonicalKindRowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var kind = match.Groups["kind"].Value;
            Assert.True(
                kinds.TryAdd(
                    kind,
                    (match.Groups["description"].Value.Trim(), match.Groups["answers"].Value.Trim(), match.Groups["persistent"].Value.Trim())),
                $"Duplicate canonical kind '{kind}'.");
        }

        Assert.NotEmpty(kinds);
        return kinds;
    }

    private static Dictionary<string, (string Kind, string Evidence)> ParseNativeMapping(string section)
    {
        var mapping = new Dictionary<string, (string Kind, string Evidence)>(StringComparer.Ordinal);

        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = NativeMappingRowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var native = match.Groups["native"].Value.Trim().Trim('`', ' ');
            var kind = match.Groups["kind"].Value;
            var evidence = match.Groups["evidence"].Value.Trim();

            Assert.False(string.IsNullOrWhiteSpace(native), $"Native mapping row has no native kind: {line}");
            Assert.False(string.IsNullOrWhiteSpace(evidence), $"Native kind '{native}' has no evidence.");
            Assert.True(
                mapping.TryAdd(native, (kind, evidence)),
                $"Duplicate native kind '{native}'.");
        }

        Assert.NotEmpty(mapping);
        return mapping;
    }

    private static HashSet<string> ParseNotReportedFields(string section)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = NotReportedRowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            Assert.True(
                fields.Add(match.Groups["field"].Value),
                $"Duplicate Not reported field '{match.Groups["field"].Value}'.");
        }

        Assert.NotEmpty(fields);
        return fields;
    }

    private static void AssertMapping(
        string[] expected,
        IReadOnlyDictionary<string, (string Kind, string Evidence)> actual,
        string backend)
    {
        foreach (var entry in expected)
        {
            var separator = entry.IndexOf('=');
            var native = entry[..separator];
            var kind = entry[(separator + 1)..];

            Assert.True(
                actual.TryGetValue(native, out var mapping),
                $"{backend} approval mapping is missing native kind '{native}'.");
            Assert.Equal(kind, mapping.Kind);
        }
    }

    private static string NormalizeNativeKind(
        IReadOnlyDictionary<string, (string Kind, string Evidence)> mappings,
        string nativeKind)
    {
        var key = nativeKind.Trim().ToLowerInvariant();
        return mappings.TryGetValue(key, out var mapping) ? mapping.Kind : "UnknownHighRisk";
    }

    private static string ReadSection(string path, string startMarker, string endMarker)
    {
        var content = File.ReadAllText(path);
        var startIndex = content.IndexOf(startMarker, StringComparison.Ordinal);

        Assert.True(startIndex >= 0, $"'{Path.GetFileName(path)}' is missing section '{startMarker}'.");

        var endIndex = content.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"'{Path.GetFileName(path)}' is missing section boundary '{endMarker}'.");

        return content[startIndex..endIndex];
    }

    private static string RepositoryPath(params string[] segments)
    {
        return Path.Combine(FindRepositoryRoot(), Path.Combine(segments));
    }

    private static string StateTablesPath =>
        RepositoryPath("docs", "architecture", "STATE_TRANSITION_TABLES.md");

    private static string ApprovalMappingPath =>
        RepositoryPath("docs", "protocols", "capabilities", "approval-mapping.md");

    private static string LegacyBoundaryPath =>
        RepositoryPath("docs", "protocols", "capabilities", "legacy-entrypoint-boundary.md");

    private static string ExitReportPath =>
        RepositoryPath("docs", "acceptance", "PHASE_0_EXIT_REPORT.md");

    private static string TestSourcePath =>
        RepositoryPath("tests", "LLMWorkGUI.Backends.ContractTests", "StateAndApprovalContractTests.cs");

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
}
