CREATE TABLE Projects (
    Id TEXT NOT NULL PRIMARY KEY,
    DisplayName TEXT NOT NULL,
    RootPath TEXT NOT NULL,
    GitBranch TEXT NULL,
    IsDirty INTEGER NOT NULL DEFAULT 0 CHECK (IsDirty IN (0, 1)),
    HasRequiredInstructions INTEGER NOT NULL DEFAULT 0 CHECK (HasRequiredInstructions IN (0, 1)),
    DefaultWorkflowId TEXT NULL,
    DefaultRoutePolicyId TEXT NULL,
    DataClassification TEXT NOT NULL CHECK (DataClassification IN ('PublicSource', 'PrivateSource', 'Restricted')),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE UNIQUE INDEX IX_Projects_RootPath ON Projects (RootPath);

CREATE TABLE ProviderProfiles (
    Id TEXT NOT NULL PRIMARY KEY,
    DisplayName TEXT NOT NULL,
    Backend TEXT NOT NULL,
    BaseUrl TEXT NULL,
    ExecutablePath TEXT NULL,
    MaxDataClass TEXT NOT NULL CHECK (MaxDataClass IN ('PublicSource', 'PrivateSource', 'Restricted')),
    IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0, 1)),
    ApiKeySecretReference TEXT NULL CHECK (ApiKeySecretReference IS NULL OR ApiKeySecretReference GLOB 'urn:llmworkgui:secret:*'),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE BackendInstances (
    Id TEXT NOT NULL PRIMARY KEY,
    ProviderProfileId TEXT NOT NULL REFERENCES ProviderProfiles (Id) ON DELETE CASCADE,
    Backend TEXT NOT NULL,
    ProcessId INTEGER NULL CHECK (ProcessId IS NULL OR ProcessId > 0),
    Endpoint TEXT NULL,
    Version TEXT NULL,
    Health TEXT NOT NULL,
    StartedAtUtc TEXT NULL,
    StoppedAtUtc TEXT NULL
);

CREATE INDEX IX_BackendInstances_ProviderProfileId ON BackendInstances (ProviderProfileId);

CREATE TABLE Accounts (
    Id TEXT NOT NULL PRIMARY KEY,
    ProviderProfileId TEXT NOT NULL REFERENCES ProviderProfiles (Id) ON DELETE CASCADE,
    DisplayName TEXT NOT NULL,
    ProviderNativeId TEXT NULL,
    AuthState TEXT NOT NULL,
    ManualPriority INTEGER NOT NULL DEFAULT 0 CHECK (ManualPriority >= 0),
    IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0, 1)),
    Health TEXT NOT NULL,
    CooldownUntilUtc TEXT NULL,
    DisabledUntilUtc TEXT NULL,
    MaxConcurrentExecutions INTEGER NOT NULL DEFAULT 1 CHECK (MaxConcurrentExecutions >= 1),
    ReserveThreshold REAL NULL,
    SecretReference TEXT NULL CHECK (SecretReference IS NULL OR SecretReference GLOB 'urn:llmworkgui:secret:*'),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE INDEX IX_Accounts_ProviderProfileId ON Accounts (ProviderProfileId);

CREATE TABLE Models (
    Id TEXT NOT NULL PRIMARY KEY,
    Backend TEXT NOT NULL,
    ProviderProfileId TEXT NOT NULL REFERENCES ProviderProfiles (Id) ON DELETE CASCADE,
    ProviderModelId TEXT NOT NULL,
    DisplayName TEXT NOT NULL,
    CapabilityState TEXT NOT NULL,
    Provenance TEXT NOT NULL,
    IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0, 1)),
    Health TEXT NOT NULL,
    ContextLimit INTEGER NULL CHECK (ContextLimit IS NULL OR ContextLimit > 0),
    SupportsTools INTEGER NOT NULL DEFAULT 0 CHECK (SupportsTools IN (0, 1)),
    SupportsAttachments INTEGER NOT NULL DEFAULT 0 CHECK (SupportsAttachments IN (0, 1)),
    DiscoveredAtUtc TEXT NOT NULL,
    UNIQUE (ProviderProfileId, ProviderModelId)
);

CREATE INDEX IX_Models_ProviderProfileId ON Models (ProviderProfileId);

CREATE TABLE ModelCapabilities (
    Id TEXT NOT NULL PRIMARY KEY,
    ModelId TEXT NOT NULL REFERENCES Models (Id) ON DELETE CASCADE,
    CapabilityKey TEXT NOT NULL,
    CapabilityValue TEXT NULL,
    State TEXT NOT NULL,
    Provenance TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    UNIQUE (ModelId, CapabilityKey)
);

CREATE TABLE Routes (
    Id TEXT NOT NULL PRIMARY KEY,
    Backend TEXT NOT NULL,
    ProviderProfileId TEXT NOT NULL REFERENCES ProviderProfiles (Id) ON DELETE CASCADE,
    AccountId TEXT NOT NULL REFERENCES Accounts (Id) ON DELETE CASCADE,
    ModelId TEXT NOT NULL REFERENCES Models (Id) ON DELETE CASCADE,
    ReasoningEffort TEXT NULL,
    SpeedMode TEXT NULL,
    ExecutionMode TEXT NULL,
    MaxDataClass TEXT NOT NULL CHECK (MaxDataClass IN ('PublicSource', 'PrivateSource', 'Restricted')),
    IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0, 1)),
    Health TEXT NOT NULL,
    ManualPriority INTEGER NOT NULL DEFAULT 0 CHECK (ManualPriority >= 0),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE INDEX IX_Routes_AccountId_ModelId ON Routes (AccountId, ModelId);

CREATE TABLE RoutingPolicies (
    Id TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    ProjectId TEXT NULL REFERENCES Projects (Id) ON DELETE CASCADE,
    Strategy TEXT NOT NULL,
    DefinitionJson TEXT NULL,
    IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0, 1)),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE INDEX IX_RoutingPolicies_ProjectId ON RoutingPolicies (ProjectId);

CREATE TABLE ProjectLocks (
    Id TEXT NOT NULL PRIMARY KEY,
    ProjectId TEXT NOT NULL REFERENCES Projects (Id) ON DELETE CASCADE,
    CanonicalRootPath TEXT NOT NULL,
    ExecutionId TEXT NOT NULL REFERENCES Executions (Id),
    ApplicationInstanceId TEXT NOT NULL,
    ProcessGeneration INTEGER NOT NULL CHECK (ProcessGeneration >= 0),
    AcquiredAtUtc TEXT NOT NULL,
    ReleasedAtUtc TEXT NULL,
    ReleaseReason TEXT NULL
);

CREATE UNIQUE INDEX UX_ProjectLocks_ActiveCanonicalRootPath
    ON ProjectLocks (CanonicalRootPath)
    WHERE ReleasedAtUtc IS NULL;

CREATE TABLE QuotaSnapshots (
    Id TEXT NOT NULL PRIMARY KEY,
    AccountId TEXT NOT NULL REFERENCES Accounts (Id) ON DELETE CASCADE,
    ModelId TEXT NULL REFERENCES Models (Id) ON DELETE SET NULL,
    Bucket TEXT NULL,
    LimitValue REAL NULL,
    RemainingValue REAL NULL,
    UsedValue REAL NULL,
    ResetAtUtc TEXT NULL,
    Freshness TEXT NOT NULL,
    Source TEXT NOT NULL,
    CapturedAtUtc TEXT NOT NULL,
    RawRedactedPayloadJson TEXT NULL
);

CREATE INDEX IX_QuotaSnapshots_AccountId_CapturedAtUtc ON QuotaSnapshots (AccountId, CapturedAtUtc);

CREATE TABLE HealthStates (
    Id TEXT NOT NULL PRIMARY KEY,
    ScopeType TEXT NOT NULL,
    ScopeId TEXT NOT NULL,
    State TEXT NOT NULL,
    ErrorClass TEXT NULL,
    FailureCount INTEGER NOT NULL DEFAULT 0 CHECK (FailureCount >= 0),
    WindowStartedAtUtc TEXT NULL,
    CooldownUntilUtc TEXT NULL,
    EvidenceRedactedJson TEXT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    UNIQUE (ScopeType, ScopeId)
);

CREATE TABLE HealthEvents (
    Id TEXT NOT NULL PRIMARY KEY,
    ScopeType TEXT NOT NULL,
    ScopeId TEXT NOT NULL,
    PreviousState TEXT NULL,
    NewState TEXT NOT NULL,
    ErrorClass TEXT NULL,
    Reason TEXT NULL,
    EvidenceRedactedJson TEXT NULL,
    OccurredAtUtc TEXT NOT NULL
);

CREATE INDEX IX_HealthEvents_ScopeId_OccurredAtUtc ON HealthEvents (ScopeId, OccurredAtUtc);

CREATE TABLE Sessions (
    Id TEXT NOT NULL PRIMARY KEY,
    ProjectId TEXT NOT NULL REFERENCES Projects (Id) ON DELETE CASCADE,
    Backend TEXT NOT NULL,
    ProviderProfileId TEXT NOT NULL REFERENCES ProviderProfiles (Id),
    AccountId TEXT NOT NULL REFERENCES Accounts (Id),
    ModelId TEXT NOT NULL REFERENCES Models (Id),
    ReasoningEffort TEXT NULL,
    SpeedMode TEXT NULL,
    ExecutionMode TEXT NULL,
    WorkspaceRootPath TEXT NOT NULL,
    NativeSessionId TEXT NULL,
    State TEXT NOT NULL,
    ReconciliationOutcome TEXT NOT NULL,
    CloseReason TEXT NOT NULL,
    ContinuationOfSessionId TEXT NULL REFERENCES Sessions (Id),
    ForkedFromSessionId TEXT NULL REFERENCES Sessions (Id),
    WorkflowRunId TEXT NULL,
    Role TEXT NULL,
    ActiveExecutionId TEXT NULL,
    CreatedAtUtc TEXT NOT NULL,
    LastEventAtUtc TEXT NOT NULL
);

CREATE INDEX IX_Sessions_ProjectId_State ON Sessions (ProjectId, State);
CREATE INDEX IX_Sessions_NativeSessionId ON Sessions (NativeSessionId);

CREATE TABLE SessionLinks (
    Id TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL REFERENCES Sessions (Id) ON DELETE CASCADE,
    LinkedSessionId TEXT NOT NULL REFERENCES Sessions (Id) ON DELETE CASCADE,
    LinkType TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    UNIQUE (SessionId, LinkedSessionId, LinkType)
);

CREATE INDEX IX_SessionLinks_LinkedSessionId ON SessionLinks (LinkedSessionId);

CREATE TABLE Executions (
    Id TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL REFERENCES Sessions (Id) ON DELETE CASCADE,
    ClientRequestId TEXT NOT NULL,
    State TEXT NOT NULL,
    FailureReason TEXT NOT NULL,
    RequestedRouteId TEXT NOT NULL REFERENCES Routes (Id),
    ObservedRouteId TEXT NULL REFERENCES Routes (Id) ON DELETE SET NULL,
    RetryOfExecutionId TEXT NULL REFERENCES Executions (Id),
    ProcessState TEXT NULL,
    ExitCode INTEGER NULL,
    TerminationReason TEXT NULL,
    SourceHashBefore TEXT NULL,
    SourceHashAfter TEXT NULL,
    CreatedAtUtc TEXT NOT NULL,
    StartedAtUtc TEXT NULL,
    EndedAtUtc TEXT NULL
);

CREATE INDEX IX_Executions_SessionId_State ON Executions (SessionId, State);
CREATE INDEX IX_Executions_RequestedRouteId ON Executions (RequestedRouteId);

CREATE TABLE ClientRequests (
    Id TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL REFERENCES Sessions (Id) ON DELETE CASCADE,
    ExecutionId TEXT NOT NULL REFERENCES Executions (Id) ON DELETE CASCADE,
    PromptHash TEXT NOT NULL,
    RequestedRouteId TEXT NOT NULL REFERENCES Routes (Id),
    ObservedRouteId TEXT NULL REFERENCES Routes (Id) ON DELETE SET NULL,
    NativeRequestId TEXT NULL,
    CreatedAtUtc TEXT NOT NULL
);

CREATE INDEX IX_ClientRequests_ExecutionId ON ClientRequests (ExecutionId);

CREATE TABLE ExecutionEvents (
    Id TEXT NOT NULL PRIMARY KEY,
    ExecutionId TEXT NOT NULL REFERENCES Executions (Id) ON DELETE CASCADE,
    Sequence INTEGER NOT NULL CHECK (Sequence >= 0),
    EventKind TEXT NOT NULL,
    NormalizedRedactedPayloadJson TEXT NULL,
    RawRedactedPayloadText TEXT NULL,
    DataClassification TEXT NULL CHECK (DataClassification IS NULL OR DataClassification IN ('PublicSource', 'PrivateSource', 'Restricted')),
    OccurredAtUtc TEXT NOT NULL,
    UNIQUE (ExecutionId, Sequence)
);

CREATE TABLE Approvals (
    Id TEXT NOT NULL PRIMARY KEY,
    ExecutionId TEXT NOT NULL REFERENCES Executions (Id) ON DELETE CASCADE,
    Kind TEXT NOT NULL,
    Operation TEXT NOT NULL,
    PathScope TEXT NULL,
    State TEXT NOT NULL,
    RequestedAtUtc TEXT NOT NULL,
    DecidedAtUtc TEXT NULL,
    DecisionReason TEXT NULL,
    EvidenceRedactedJson TEXT NULL
);

CREATE INDEX IX_Approvals_ExecutionId_State ON Approvals (ExecutionId, State);

CREATE TABLE ApprovalRules (
    Id TEXT NOT NULL PRIMARY KEY,
    Backend TEXT NOT NULL,
    ProviderProfileId TEXT NOT NULL REFERENCES ProviderProfiles (Id) ON DELETE CASCADE,
    ProjectId TEXT NOT NULL REFERENCES Projects (Id) ON DELETE CASCADE,
    PathScope TEXT NULL,
    Kind TEXT NOT NULL,
    Operation TEXT NOT NULL,
    ExpiresAtUtc TEXT NULL,
    CreatedBy TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);

CREATE INDEX IX_ApprovalRules_ProjectId ON ApprovalRules (ProjectId);

CREATE TABLE WorkflowPackages (
    Id TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    Description TEXT NULL,
    TagsJson TEXT NULL,
    SourceType TEXT NOT NULL,
    OriginalHash TEXT NOT NULL,
    OriginalBlobId TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE UNIQUE INDEX IX_WorkflowPackages_OriginalHash ON WorkflowPackages (OriginalHash);

CREATE TABLE WorkflowVersions (
    Id TEXT NOT NULL PRIMARY KEY,
    WorkflowPackageId TEXT NOT NULL REFERENCES WorkflowPackages (Id) ON DELETE CASCADE,
    VersionNumber INTEGER NOT NULL CHECK (VersionNumber >= 1),
    BlobId TEXT NOT NULL,
    OriginalHash TEXT NOT NULL,
    SourceType TEXT NOT NULL,
    EntrypointsJson TEXT NULL,
    DeclaredRolesJson TEXT NULL,
    BindingsJson TEXT NULL,
    CompatibilityReportJson TEXT NULL,
    CreationMetadataJson TEXT NULL,
    CreatedAtUtc TEXT NOT NULL,
    ActivatedAtUtc TEXT NULL,
    UNIQUE (WorkflowPackageId, VersionNumber)
);

CREATE TABLE WorkflowBindings (
    Id TEXT NOT NULL PRIMARY KEY,
    ProjectId TEXT NOT NULL REFERENCES Projects (Id) ON DELETE CASCADE,
    WorkflowPackageId TEXT NOT NULL REFERENCES WorkflowPackages (Id) ON DELETE CASCADE,
    ActiveVersionId TEXT NOT NULL REFERENCES WorkflowVersions (Id),
    RoutePolicyId TEXT NULL REFERENCES RoutingPolicies (Id) ON DELETE SET NULL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    UNIQUE (ProjectId, WorkflowPackageId)
);

CREATE TABLE WorkflowRuns (
    Id TEXT NOT NULL PRIMARY KEY,
    ProjectId TEXT NOT NULL REFERENCES Projects (Id) ON DELETE CASCADE,
    WorkflowPackageId TEXT NOT NULL REFERENCES WorkflowPackages (Id),
    WorkflowVersionId TEXT NOT NULL REFERENCES WorkflowVersions (Id),
    SessionId TEXT NULL REFERENCES Sessions (Id) ON DELETE SET NULL,
    State TEXT NOT NULL,
    StartedAtUtc TEXT NOT NULL,
    EndedAtUtc TEXT NULL,
    TerminalOutcome TEXT NULL,
    EvidenceRedactedJson TEXT NULL
);

CREATE INDEX IX_WorkflowRuns_ProjectId_State ON WorkflowRuns (ProjectId, State);
CREATE INDEX IX_WorkflowRuns_WorkflowVersionId ON WorkflowRuns (WorkflowVersionId);

CREATE TABLE Artifacts (
    Id TEXT NOT NULL PRIMARY KEY,
    WorkflowRunId TEXT NULL REFERENCES WorkflowRuns (Id) ON DELETE CASCADE,
    ExecutionId TEXT NULL REFERENCES Executions (Id) ON DELETE CASCADE,
    Kind TEXT NOT NULL,
    BlobId TEXT NULL,
    RelativePath TEXT NULL,
    HashSha256 TEXT NULL,
    SizeBytes INTEGER NULL CHECK (SizeBytes IS NULL OR SizeBytes >= 0),
    DataClassification TEXT NOT NULL CHECK (DataClassification IN ('PublicSource', 'PrivateSource', 'Restricted')),
    CreatedAtUtc TEXT NOT NULL,
    CHECK (WorkflowRunId IS NOT NULL OR ExecutionId IS NOT NULL)
);

CREATE INDEX IX_Artifacts_WorkflowRunId ON Artifacts (WorkflowRunId);
CREATE INDEX IX_Artifacts_ExecutionId ON Artifacts (ExecutionId);

CREATE TABLE ApplicationSettings (
    Key TEXT NOT NULL PRIMARY KEY,
    Value TEXT NOT NULL,
    ValueType TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
