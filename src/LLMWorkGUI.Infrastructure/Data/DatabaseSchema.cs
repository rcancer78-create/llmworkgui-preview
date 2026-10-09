namespace LLMWorkGUI.Infrastructure.Data;

public static class DatabaseSchema
{
    public const string MigrationsTableName = "_schema_migrations";

    public static IReadOnlyList<string> TableNames { get; } =
    [
        "Projects",
        "ProjectDataPolicyChanges",
        "WorkflowReviewResponses",
        "WorkflowMaterialPolicies",
        "WorkflowMaterialPolicyChanges",
        "WorkflowAdaptationPolicyChecks",
        "WorkflowAdaptationPromptAdmissions",
        "WorkflowAdaptationNativeBindings",
        "WorkflowAdaptationTransportChecks",
        "OpenCodeAdaptationAccountMappings",
        "WorkflowAdaptationRuntimeOwners",
        "WorkflowAdaptationRuntimeChecks",
        "ProviderProfiles",
        "BackendInstances",
        "Accounts",
        "Models",
        "ModelCapabilities",
        "Routes",
        "RoutingPolicies",
        "ProjectLocks",
        "QuotaSnapshots",
        "HealthStates",
        "HealthAuthenticationFanout",
        "HealthAuthenticationFanoutAccounts",
        "HealthEvents",
        "Sessions",
        "SessionLinks",
        "Executions",
        "ClientRequests",
        "ExecutionEvents",
        "Approvals",
        "ApprovalRules",
        "ApprovalRuleAudit",
        "SecretReferences",
        "SecretReferenceOwners",
        "PendingSecretDeletions",
        "ProviderProfileRevisions",
        "WorkflowPackages",
        "WorkflowVersions",
        "WorkflowBindings",
        "WorkflowTemplateVersions",
        "WorkflowTemplateAssignments",
        "WorkflowRuns",
        "Artifacts",
        "WorkflowReviewExecutions",
        "ApplicationSettings",
        "ActivityEvents",
        // Migration 011 adds an FTS5 virtual table for the durable Activity Center search, plus the five
        // shadow tables SQLite creates to back it. They are named here so the migration tests can assert
        // that the schema of a migrated database is exactly the set this list declares.
        "ActivityEventsSearch",
        "ActivityEventsSearch_data",
        "ActivityEventsSearch_idx",
        "ActivityEventsSearch_docsize",
        "ActivityEventsSearch_config",
        "ActivityEventsSearch_content"
    ];
}
