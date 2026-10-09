using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class CapabilityMatrixContractTests
{
    private static readonly string[] AllowedCapabilityStates =
    {
        "Supported",
        "Unsupported",
        "Unknown"
    };

    private static readonly string[] ExpectedQuotaValueStates =
    {
        "ExactProviderReported",
        "PluginReported",
        "LocallyCalculated",
        "Estimated",
        "Stale",
        "Unsupported",
        "Unknown",
        "Error"
    };

    private static readonly string[] ExpectedQuotaApiStates =
    {
        "Supported",
        "Unsupported",
        "Unknown",
        "Error"
    };

    private static readonly string[] RequiredRoutingModes =
    {
        "Pinned",
        "SessionSticky",
        "QuotaFirst",
        "PriorityFirst",
        "Balanced",
        "ManualOnly"
    };

    private static readonly string[] RequiredEvidencePaths =
    {
        "TECHNICAL_SPECIFICATION.md",
        "ROADMAP.md",
        "docs/adr/ADR-0001-runtime-and-cli-baseline.md",
        "docs/adr/ADR-0002-opencode-serve-topology-and-api.md",
        "docs/adr/ADR-0003-cursor-acp-lifecycle.md",
        "docs/adr/ADR-0004-multi-account-and-quota-capabilities.md",
        "docs/protocols/opencode/server-api-inventory.json",
        "docs/protocols/opencode/session-schema.json",
        "docs/protocols/cursor/acp-capabilities.json",
        "docs/protocols/cursor/acp-handshake-response.json",
        "docs/protocols/cursor/acp-model-catalog.json",
        "docs/protocols/cursor/acp-session-new-schema.json",
        "docs/protocols/capabilities/quota-capability-inventory.json",
        "docs/protocols/capabilities/multi-account-routing-contract.json"
    };

    private static readonly Dictionary<string, string> ExpectedCapabilityStates = new(StringComparer.Ordinal)
    {
        ["opencode.transport.serve"] = "Supported",
        ["opencode.session.create"] = "Supported",
        ["opencode.session.stream"] = "Supported",
        ["opencode.session.cancel"] = "Supported",
        ["opencode.session.resume"] = "Supported",
        ["opencode.session.fork"] = "Supported",
        ["opencode.session.close"] = "Unknown",
        ["opencode.approvals.reply"] = "Supported",
        ["opencode.model.discovery"] = "Supported",
        ["opencode.model.parameterizedOverrides"] = "Unknown",
        ["opencode.account.pin"] = "Unsupported",
        ["opencode.account.observedRoute"] = "Unsupported",
        ["opencode.account.autoRotation.disable"] = "Unsupported",
        ["opencode.account.multiAccount"] = "Unsupported",
        ["opencode.quota.api"] = "Unsupported",
        ["opencode.quota.providerReported"] = "Unsupported",
        ["opencode.quota.localCounter"] = "Supported",
        ["opencode.health.probe"] = "Supported",
        ["opencode.customBaseUrl"] = "Supported",
        ["opencode.plugins.inventory"] = "Unknown",
        ["opencode.diagnostic.cliFallback"] = "Supported",
        ["cursor.transport.acp"] = "Supported",
        ["cursor.session.create"] = "Supported",
        ["cursor.session.stream"] = "Unknown",
        ["cursor.session.cancel"] = "Unknown",
        ["cursor.session.resume"] = "Supported",
        ["cursor.session.fork"] = "Unknown",
        ["cursor.approvals.reply"] = "Supported",
        ["cursor.model.discovery"] = "Supported",
        ["cursor.model.parameterizedOverrides"] = "Supported",
        ["cursor.account.pin"] = "Unsupported",
        ["cursor.account.observedRoute"] = "Unsupported",
        ["cursor.account.autoRotation.disable"] = "Unsupported",
        ["cursor.account.multiAccount"] = "Unsupported",
        ["cursor.quota.api"] = "Unsupported",
        ["cursor.quota.providerReported"] = "Unsupported",
        ["cursor.quota.localCounter"] = "Unknown",
        ["cursor.health.probe"] = "Supported",
        ["cursor.customBaseUrl"] = "Unsupported",
        ["cursor.diagnostic.cliFallback"] = "Supported",
        ["codex.backend.native"] = "Unsupported",
        ["codex.integration.viaOpenCode"] = "Unsupported",
        ["codex.integration.viaStarCliProxy"] = "Unknown",
        ["codex.account.multiAccount.profiles"] = "Unknown",
        ["codex.account.pin"] = "Unknown",
        ["codex.account.observedRoute"] = "Unknown",
        ["codex.account.autoRotation.disable"] = "Unknown",
        ["codex.quota.api"] = "Unknown",
        ["codex.quota.providerReported"] = "Unknown",
        ["codex.quota.localCounter"] = "Unknown",
        ["codex.session.resume"] = "Unknown",
        ["codex.session.fork"] = "Unknown",
        ["agy.backend.native"] = "Unsupported",
        ["agy.integration.viaOpenCode"] = "Unsupported",
        ["agy.integration.viaStarCliProxy"] = "Unknown",
        ["agy.model.discovery"] = "Supported",
        ["agy.modes"] = "Supported",
        ["agy.session.continue"] = "Supported",
        ["agy.account.pin"] = "Unknown",
        ["agy.account.observedRoute"] = "Unknown",
        ["agy.quota.api"] = "Unsupported",
        ["agy.quota.providerReported"] = "Unsupported",
        ["agy.quota.localCounter"] = "Unknown",
        ["agy.diagnostic.jsonOutput"] = "Supported",
        ["bridge.isolation.providerProfile"] = "Supported",
        ["bridge.account.pin"] = "Supported",
        ["bridge.account.observedRoute"] = "Supported",
        ["bridge.autoRotation.disable"] = "Supported",
        ["bridge.routeMismatch"] = "Supported",
        ["bridge.quota.honestStates"] = "Supported"
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

    private static readonly Regex CapabilityDataRowPattern = new(
        @"^\|\s*`(?<id>[a-z][A-Za-z0-9.-]*)`",
        RegexOptions.CultureInvariant);

    private static readonly Regex CapabilityFullRowPattern = new(
        @"^\|\s*`(?<id>[a-z][A-Za-z0-9.-]*)`\s*\|\s*(?<capability>[^|]+?)\s*\|\s*(?<state>Supported|Unsupported|Unknown)\s*\|\s*(?<evidence>[^|]+?)\s*\|\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex EvidencePathPattern = new(
        @"`(?<path>(?:docs/)?[A-Za-z0-9._/-]+\.(?:md|json))`",
        RegexOptions.CultureInvariant);

    private static readonly Regex QuotaPercentPattern = new(
        @"\d+\s*%",
        RegexOptions.CultureInvariant);

    [Fact]
    public void CapabilityMatrix_DeclaresKeyCapabilitiesWithAllowedStates()
    {
        var rows = ParseCapabilityMatrixRows();

        foreach (var expected in ExpectedCapabilityStates)
        {
            Assert.True(
                rows.TryGetValue(expected.Key, out var actual),
                $"Capability matrix is missing capability '{expected.Key}'.");

            Assert.Equal(expected.Value, actual.State);
            Assert.False(string.IsNullOrWhiteSpace(actual.Evidence));
        }

        Assert.True(rows.Count >= ExpectedCapabilityStates.Count);

        foreach (var prefix in new[] { "opencode.", "cursor.", "codex.", "agy.", "bridge." })
        {
            Assert.True(
                rows.Keys.Count(id => id.StartsWith(prefix, StringComparison.Ordinal)) >= 5,
                $"Capability matrix section '{prefix}' is unexpectedly small.");
        }
    }

    [Fact]
    public void CapabilityMatrix_EvidenceIndexReferencesExistingArtifacts()
    {
        var content = File.ReadAllText(CapabilityMatrixPath);
        var indexStart = content.IndexOf("## 8. Evidence index", StringComparison.Ordinal);
        Assert.True(indexStart >= 0, "Capability matrix has no '## 8. Evidence index' section.");

        var section = content[indexStart..];
        var paths = EvidencePathPattern.Matches(section)
            .Select(match => match.Groups["path"].Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var requiredPath in RequiredEvidencePaths)
        {
            Assert.True(paths.Contains(requiredPath), $"Evidence index is missing '{requiredPath}'.");
        }

        foreach (var path in paths)
        {
            var normalized = path.Replace('/', Path.DirectorySeparatorChar);
            Assert.True(
                File.Exists(Path.Combine(FindRepositoryRoot(), normalized)),
                $"Evidence index references missing file '{path}'.");
        }
    }

    [Fact]
    public void AcpCapabilities_DeclareUnprovenExchangesAndModesAsUnknown()
    {
        var root = ParseJson(AcpCapabilitiesPath);

        var capabilityStates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var capability in root.GetProperty("capabilities").EnumerateArray())
        {
            capabilityStates.Add(
                capability.GetProperty("id").GetString()!,
                capability.GetProperty("state").GetString()!);
        }

        foreach (var id in new[] { "acp.prompt", "acp.streaming.updates", "acp.approvals", "acp.cancel" })
        {
            Assert.True(
                capabilityStates.TryGetValue(id, out var state),
                $"acp-capabilities.json is missing capability '{id}'.");
            Assert.Equal("Unknown", state);
        }

        var modeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mode in root.GetProperty("modes").EnumerateArray())
        {
            var modeId = mode.GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(modeId));
            Assert.True(modeIds.Add(modeId!), $"Duplicate mode id '{modeId}'.");
            Assert.Equal("Unknown", mode.GetProperty("state").GetString());
            Assert.Contains(
                "writer-lock exemption",
                mode.GetProperty("note").GetString()!,
                StringComparison.Ordinal);
        }

        Assert.Equal(
            new[] { "agent", "ask", "plan" },
            modeIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void QuotaCapabilityInventory_ParsesAndDeclaresHonestStates()
    {
        var root = ParseJson(QuotaInventoryPath);

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("quota-capability-inventory", root.GetProperty("artifactId").GetString());
        Assert.Equal("Accepted", root.GetProperty("status").GetString());
        Assert.Equal("TASK-004", root.GetProperty("taskId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("capturedAt").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("evidence").GetString()));

        var rules = root.GetProperty("normativeRules");
        var valueStates = GetStringSet(rules.GetProperty("quotaValueStates"));
        var numericStates = GetStringSet(rules.GetProperty("numericValueStates"));
        var nonNumericStates = GetStringSet(rules.GetProperty("nonNumericValueStates"));
        var trustedStates = GetStringSet(rules.GetProperty("trustedValueStates"));

        foreach (var expectedState in ExpectedQuotaValueStates)
        {
            Assert.Contains(expectedState, valueStates);
        }

        Assert.Equal(valueStates.Count, numericStates.Union(nonNumericStates, StringComparer.Ordinal).Count());
        Assert.Empty(numericStates.Intersect(nonNumericStates, StringComparer.Ordinal));
        Assert.False(rules.GetProperty("fabricatedValuesAllowed").GetBoolean());
        Assert.True(rules.GetProperty("forbiddenRepresentations").GetArrayLength() >= 3);
        Assert.True(rules.GetProperty("defaultTtlMinutes").GetInt32() > 0);

        var providers = root.GetProperty("providers");
        Assert.True(providers.GetArrayLength() >= 4);

        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        var fieldIds = new HashSet<string>(StringComparer.Ordinal);
        var sawUnsupportedQuotaApi = false;
        var sawLocalCounter = false;

        foreach (var provider in providers.EnumerateArray())
        {
            var providerId = provider.GetProperty("providerId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(providerId));
            Assert.True(providerIds.Add(providerId!), $"Duplicate provider id '{providerId}'.");
            Assert.False(string.IsNullOrWhiteSpace(provider.GetProperty("backend").GetString()));
            Assert.True(provider.GetProperty("providerProfileIsolated").GetBoolean());

            var quotaApi = provider.GetProperty("quotaApi");
            var apiState = quotaApi.GetProperty("state").GetString();
            Assert.False(string.IsNullOrWhiteSpace(apiState));
            Assert.Contains(apiState!, ExpectedQuotaApiStates);
            Assert.False(string.IsNullOrWhiteSpace(quotaApi.GetProperty("evidence").GetString()));

            if (string.Equals(apiState, "Unsupported", StringComparison.Ordinal))
            {
                sawUnsupportedQuotaApi = true;
            }

            var fields = provider.GetProperty("quotaFields");
            Assert.True(fields.GetArrayLength() >= 1, $"Provider '{providerId}' declares no quota fields.");

            foreach (var field in fields.EnumerateArray())
            {
                var fieldId = field.GetProperty("fieldId").GetString();
                Assert.False(string.IsNullOrWhiteSpace(fieldId));
                Assert.True(fieldIds.Add(fieldId!), $"Duplicate quota field id '{fieldId}'.");
                Assert.False(string.IsNullOrWhiteSpace(field.GetProperty("bucket").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(field.GetProperty("unit").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(field.GetProperty("resetWindow").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(field.GetProperty("resetWindowState").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(field.GetProperty("source").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(field.GetProperty("confidence").GetString()));

                var valueState = field.GetProperty("valueState").GetString();
                Assert.False(string.IsNullOrWhiteSpace(valueState));
                Assert.Contains(valueState!, ExpectedQuotaValueStates);

                var numeric = field.GetProperty("numeric").GetBoolean();
                if (numeric)
                {
                    Assert.Contains(valueState!, numericStates);
                }
                else
                {
                    Assert.Contains(valueState!, nonNumericStates);
                }

                if (string.Equals(valueState, "LocallyCalculated", StringComparison.Ordinal))
                {
                    Assert.True(
                        field.GetProperty("explicitOptInRequired").GetBoolean(),
                        "LocallyCalculated quota fields require explicit user opt-in.");
                    sawLocalCounter = true;
                }

                if (!string.Equals(apiState, "Supported", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain(valueState!, trustedStates);
                    if (numeric)
                    {
                        Assert.True(
                            field.GetProperty("explicitOptInRequired").GetBoolean(),
                            $"Numeric field '{fieldId}' without a supported quota API requires explicit opt-in.");
                    }
                }
            }
        }

        Assert.True(sawUnsupportedQuotaApi, "At least one provider must honestly report an Unsupported quota API.");
        Assert.True(sawLocalCounter, "OpenCode local session counter must be recorded as LocallyCalculated.");
        Assert.Contains("opencode-go", providerIds);
        Assert.Contains("cursor-agent", providerIds);
        Assert.Contains("codex-cli", providerIds);
        Assert.Contains("agy-cli", providerIds);
    }

    [Fact]
    public void QuotaCapabilityInventory_ContainsNoFabricatedPercentages()
    {
        var content = File.ReadAllText(QuotaInventoryPath);

        Assert.DoesNotContain("%", content, StringComparison.Ordinal);
        Assert.False(
            QuotaPercentPattern.IsMatch(content),
            "Quota inventory must not contain fabricated percentage values.");
    }

    [Fact]
    public void MultiAccountRoutingContract_DeclaresIsolationPinAndRouteMismatch()
    {
        var root = ParseJson(RoutingContractPath);

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("multi-account-routing-contract", root.GetProperty("artifactId").GetString());
        Assert.Equal("Accepted", root.GetProperty("status").GetString());
        Assert.Equal("TASK-004", root.GetProperty("taskId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("evidence").GetString()));

        Assert.Equal("RoutingEngine", root.GetProperty("routingAuthority").GetProperty("component").GetString());
        Assert.True(root.GetProperty("routingAuthority").GetProperty("singleWriter").GetBoolean());

        var isolation = root.GetProperty("isolationModel");
        Assert.Equal("ProviderProfile", isolation.GetProperty("unit").GetString());
        Assert.True(isolation.GetProperty("perProfileInstance").GetBoolean());
        Assert.False(isolation.GetProperty("sharedServerAllowed").GetBoolean());
        Assert.True(isolation.GetProperty("forbidden").GetArrayLength() >= 2);

        var bridgeCapabilities = root.GetProperty("bridgeCapabilities");
        var requiredCapabilities = GetStringArray(bridgeCapabilities.GetProperty("required"));
        Assert.Contains("account.pin", requiredCapabilities);
        Assert.Contains("account.observedRoute", requiredCapabilities);
        Assert.Contains("autoRotation.disable", requiredCapabilities);
        Assert.False(bridgeCapabilities.GetProperty("silentFallbackAllowed").GetBoolean());

        var pin = root.GetProperty("pin");
        Assert.True(pin.GetProperty("required").GetBoolean());
        Assert.True(pin.GetProperty("beforePrompt").GetBoolean());
        Assert.False(pin.GetProperty("shadowPinAllowed").GetBoolean());

        var autoRotation = root.GetProperty("autoRotation");
        Assert.Equal("Disabled", autoRotation.GetProperty("forGuiManagedSessions").GetString());
        Assert.False(autoRotation.GetProperty("pluginRotationMayOverridePin").GetBoolean());

        var bindingFields = GetStringArray(root.GetProperty("binding").GetProperty("fields"));
        Assert.Equal(7, bindingFields.Length);
        Assert.Equal(bindingFields, GetStringArray(pin.GetProperty("pinTarget")));

        Assert.True(root.GetProperty("binding").GetProperty("immutableAfterNativeSession").GetBoolean());

        var routeEvidence = root.GetProperty("routeEvidence");
        Assert.Equal(bindingFields, GetStringArray(routeEvidence.GetProperty("requestedRoute").GetProperty("fields")));
        Assert.Equal(bindingFields, GetStringArray(routeEvidence.GetProperty("observedRoute").GetProperty("fields")));
        Assert.Equal(bindingFields, GetStringArray(routeEvidence.GetProperty("comparisonFields")));
        Assert.True(routeEvidence.GetProperty("comparisonRequired").GetBoolean());
        Assert.False(routeEvidence.GetProperty("treatRequestedAsObservedAllowed").GetBoolean());
        Assert.Equal("RouteMismatch", routeEvidence.GetProperty("missingObservedRouteOutcome").GetString());

        var routeMismatch = root.GetProperty("routeMismatch");
        Assert.Equal("RouteMismatch", routeMismatch.GetProperty("code").GetString());
        var mismatchConditions = GetStringArray(routeMismatch.GetProperty("conditions"));
        Assert.True(mismatchConditions.Length >= 2);
        Assert.Contains(mismatchConditions, condition => condition.Contains("observedRoute", StringComparison.Ordinal));
        var mismatchOutcomes = GetStringArray(routeMismatch.GetProperty("outcomes"));
        Assert.True(mismatchOutcomes.Length >= 3);
        Assert.Contains(mismatchOutcomes, outcome => outcome.Contains("RouteMismatch", StringComparison.Ordinal));
        Assert.False(routeMismatch.GetProperty("successCounted").GetBoolean());
        Assert.False(routeMismatch.GetProperty("retryBudgetConsumed").GetBoolean());

        var opaqueRoutePolicy = root.GetProperty("opaqueRoutePolicy");
        Assert.Equal(new[] { "ManualOnly" }, GetStringArray(opaqueRoutePolicy.GetProperty("allowedInModes")));
        Assert.False(opaqueRoutePolicy.GetProperty("countsAsVerifiedRoute").GetBoolean());

        var modes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var mode in root.GetProperty("routingModes").EnumerateArray())
        {
            var modeId = mode.GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(modeId));
            Assert.True(modes.TryAdd(modeId!, mode), $"Duplicate routing mode '{modeId}'.");
        }

        Assert.Equal(RequiredRoutingModes.Length, modes.Count);
        foreach (var requiredMode in RequiredRoutingModes)
        {
            Assert.Contains(requiredMode, modes.Keys);
        }

        Assert.False(modes["Pinned"].GetProperty("autoFailover").GetBoolean());
        Assert.True(modes["Pinned"].GetProperty("requiresObservedRoute").GetBoolean());
        Assert.False(modes["ManualOnly"].GetProperty("autoFailover").GetBoolean());
        Assert.False(modes["ManualOnly"].GetProperty("requiresObservedRoute").GetBoolean());
        Assert.True(modes["ManualOnly"].GetProperty("opaqueRouteAllowed").GetBoolean());

        foreach (var automaticMode in new[] { "SessionSticky", "QuotaFirst", "PriorityFirst", "Balanced" })
        {
            Assert.True(
                modes[automaticMode].GetProperty("requiresObservedRoute").GetBoolean(),
                $"Automatic mode '{automaticMode}' must require observed route evidence.");
        }

        foreach (var failoverMode in new[] { "QuotaFirst", "PriorityFirst", "Balanced" })
        {
            Assert.True(modes[failoverMode].GetProperty("autoFailoverBeforeFirstPrompt").GetBoolean());
            Assert.False(modes[failoverMode].GetProperty("autoFailoverAfterSessionConfirmed").GetBoolean());
        }

        Assert.True(modes["QuotaFirst"].GetProperty("requiresFreshTrustedQuota").GetBoolean());
        Assert.True(modes["Balanced"].GetProperty("requiresFreshTrustedQuota").GetBoolean());
        Assert.False(modes["Pinned"].GetProperty("requiresFreshTrustedQuota").GetBoolean());

        Assert.True(root.GetProperty("eligibilityGates").GetArrayLength() >= 6);
        Assert.True(root.GetProperty("failoverRules").GetArrayLength() >= 4);
        Assert.Equal(4, root.GetProperty("bindingPriority").GetArrayLength());
        Assert.True(root.GetProperty("dataSensitivity").GetProperty("levels").GetArrayLength() >= 3);
        Assert.False(root.GetProperty("quotaPolicy").GetProperty("fabricatedValuesAllowed").GetBoolean());
        Assert.True(root.GetProperty("quotaPolicy").GetProperty("nonNumericHaveNoNumericValue").GetBoolean());
    }

    [Fact]
    public void Adr0004_IsAcceptedAndReferencesCapabilityArtifacts()
    {
        var content = File.ReadAllText(Adr0004Path);

        Assert.Contains("**Статус:** Accepted", content, StringComparison.Ordinal);
        Assert.Contains("capability-matrix.md", content, StringComparison.Ordinal);
        Assert.Contains("quota-capability-inventory.json", content, StringComparison.Ordinal);
        Assert.Contains("multi-account-routing-contract.json", content, StringComparison.Ordinal);
        Assert.Contains("ProviderProfile", content, StringComparison.Ordinal);
        Assert.Contains("requestedRoute", content, StringComparison.Ordinal);
        Assert.Contains("observedRoute", content, StringComparison.Ordinal);
        Assert.Contains("RouteMismatch", content, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityArtifacts_ContainNoSecretsOrUserProfilePaths()
    {
        foreach (var path in new[] { CapabilityMatrixPath, QuotaInventoryPath, RoutingContractPath, Adr0004Path })
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

    private static Dictionary<string, (string State, string Evidence)> ParseCapabilityMatrixRows()
    {
        var rows = new Dictionary<string, (string State, string Evidence)>(StringComparer.Ordinal);

        foreach (var rawLine in File.ReadAllLines(CapabilityMatrixPath))
        {
            var line = rawLine.TrimEnd();
            if (!CapabilityDataRowPattern.IsMatch(line))
            {
                continue;
            }

            var match = CapabilityFullRowPattern.Match(line);
            Assert.True(match.Success, $"Malformed capability matrix row: {line}");

            var id = match.Groups["id"].Value;
            var state = match.Groups["state"].Value;
            var evidence = match.Groups["evidence"].Value.Trim();

            Assert.Contains(state, AllowedCapabilityStates);
            Assert.False(string.IsNullOrWhiteSpace(evidence), $"Capability '{id}' has no evidence.");
            Assert.True(rows.TryAdd(id, (state, evidence)), $"Duplicate capability id '{id}'.");
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    private static JsonElement ParseJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string[] GetStringArray(JsonElement element)
    {
        return element.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static HashSet<string> GetStringSet(JsonElement element)
    {
        return GetStringArray(element).ToHashSet(StringComparer.Ordinal);
    }

    private static string RepositoryPath(params string[] segments)
    {
        return Path.Combine(FindRepositoryRoot(), Path.Combine(segments));
    }

    private static string CapabilityMatrixPath =>
        RepositoryPath("docs", "protocols", "capabilities", "capability-matrix.md");

    private static string AcpCapabilitiesPath =>
        RepositoryPath("docs", "protocols", "cursor", "acp-capabilities.json");

    private static string QuotaInventoryPath =>
        RepositoryPath("docs", "protocols", "capabilities", "quota-capability-inventory.json");

    private static string RoutingContractPath =>
        RepositoryPath("docs", "protocols", "capabilities", "multi-account-routing-contract.json");

    private static string Adr0004Path =>
        RepositoryPath("docs", "adr", "ADR-0004-multi-account-and-quota-capabilities.md");

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
