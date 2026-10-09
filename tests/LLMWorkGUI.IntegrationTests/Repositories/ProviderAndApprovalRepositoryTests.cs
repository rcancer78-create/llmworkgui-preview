using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed class ProviderAndApprovalRepositoryTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task ProviderProfileRepository_RoundTripsGatewayIdentity()
    {
        await _database.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(_database.Factory);
        await repo.UpsertAsync(new ProviderProfile("gateway", "Gateway", BackendType.NativeGateway,
            "http://127.0.0.1:8000", null, DataClassification.PrivateSource, true, gatewayNativeId: "native-provider"));
        Assert.Equal("native-provider", (await repo.GetByIdAsync("gateway"))!.GatewayNativeId);
        Assert.Equal("native-provider", Assert.Single(await repo.ListAsync()).GatewayNativeId);
    }

    [Fact]
    public async Task ProviderProfileRepository_PerformsFullCrudLifecycle()
    {
        await _database.InitializeAsync();

        var repo = new SqliteProviderProfileRepository(_database.Factory);

        var profile = new ProviderProfile(
            "custom-vllm",
            "Custom vLLM",
            BackendType.OpenCode,
            "http://127.0.0.1:8000/v1",
            null,
            DataClassification.PrivateSource,
            isEnabled: true);

        // 1. Insert
        await repo.UpsertAsync(profile, apiKeySecretReference: "urn:llmworkgui:secret:test-ref-123");

        // 2. Read
        var fetched = await repo.GetByIdAsync("custom-vllm");
        Assert.NotNull(fetched);
        Assert.Equal("custom-vllm", fetched!.Id);
        Assert.Equal("Custom vLLM", fetched.DisplayName);
        Assert.Equal("http://127.0.0.1:8000/v1", fetched.BaseUrl);
        Assert.True(fetched.IsEnabled);

        var secretRef = await repo.GetApiKeySecretReferenceAsync("custom-vllm");
        Assert.Equal("urn:llmworkgui:secret:test-ref-123", secretRef);

        // 3. Update (preserve secretRef when null passed in upsert)
        var updated = new ProviderProfile(
            "custom-vllm",
            "Custom vLLM Renamed",
            BackendType.OpenCode,
            "http://127.0.0.1:8001/v1",
            null,
            DataClassification.PrivateSource,
            isEnabled: false);

        await repo.UpsertAsync(updated);

        var fetchedUpdated = await repo.GetByIdAsync("custom-vllm");
        Assert.NotNull(fetchedUpdated);
        Assert.Equal("Custom vLLM Renamed", fetchedUpdated!.DisplayName);
        Assert.False(fetchedUpdated.IsEnabled);

        // Verify secret reference preserved
        var secretRefPreserved = await repo.GetApiKeySecretReferenceAsync("custom-vllm");
        Assert.Equal("urn:llmworkgui:secret:test-ref-123", secretRefPreserved);

        // 4. List
        var all = await repo.ListAsync();
        Assert.Contains(all, p => p.Id == "custom-vllm");

        // 5. Delete
        var deleted = await repo.DeleteAsync("custom-vllm");
        Assert.True(deleted);

        var afterDelete = await repo.GetByIdAsync("custom-vllm");
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task ApprovalRuleRepository_PerformsFullCrudLifecycle()
    {
        await _database.InitializeAsync();

        var projectRepo = new SqliteProjectRepository(_database.Factory);
        var providerRepo = new SqliteProviderProfileRepository(_database.Factory);
        var ruleRepo = new SqliteApprovalRuleRepository(_database.Factory);

        // Foreign keys require Project and ProviderProfile
        var rootPath = _database.GetWorkspacePath("test-project");
        var project = new Project("proj-1", "Test Project", rootPath, "main", false, false, null, null, DataClassification.PrivateSource);
        await projectRepo.UpsertAsync(project);

        var provider = new ProviderProfile("prov-1", "Provider 1", BackendType.OpenCode, null, null, DataClassification.PrivateSource, true);
        await providerRepo.UpsertAsync(provider);

        var rule = new ApprovalRule(
            "rule-101",
            BackendType.OpenCode,
            "prov-1",
            "proj-1",
            "src/**",
            NormalizedApprovalKind.ReadFile,
            "read",
            expiresAt: DateTimeOffset.UtcNow.AddDays(7),
            createdBy: "admin",
            createdAt: DateTimeOffset.UtcNow);

        // 1. Insert
        await ruleRepo.UpsertAsync(rule);

        // 2. Read
        var fetched = await ruleRepo.GetByIdAsync("rule-101");
        Assert.NotNull(fetched);
        Assert.Equal("rule-101", fetched!.Id);
        Assert.Equal(NormalizedApprovalKind.ReadFile, fetched.Kind);
        Assert.Equal("src/**", fetched.PathScope);

        var updatedRule = new ApprovalRule(
            "rule-101",
            BackendType.OpenCode,
            "prov-1",
            "proj-1",
            "docs/**",
            NormalizedApprovalKind.ReadFile,
            "read",
            expiresAt: DateTimeOffset.UtcNow.AddDays(3),
            createdBy: "admin",
            createdAt: rule.CreatedAt);
        await ruleRepo.UpsertAsync(updatedRule);
        Assert.Equal("docs/**", (await ruleRepo.GetByIdAsync("rule-101"))!.PathScope);

        // 3. List by Project
        var projectRules = await ruleRepo.ListByProjectIdAsync("proj-1");
        Assert.Single(projectRules);
        Assert.Equal("rule-101", projectRules[0].Id);

        // 4. Delete
        var deleted = await ruleRepo.DeleteAsync("rule-101");
        Assert.True(deleted);

        var afterDelete = await ruleRepo.GetByIdAsync("rule-101");
        Assert.Null(afterDelete);

        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT Action FROM ApprovalRuleAudit WHERE RuleId = 'rule-101' ORDER BY Sequence;";
        var actions = new List<string>();
        await using var reader = await audit.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(0));
        }

        Assert.Equal(new[] { "Created", "Updated", "Deleted" }, actions);
    }
}
