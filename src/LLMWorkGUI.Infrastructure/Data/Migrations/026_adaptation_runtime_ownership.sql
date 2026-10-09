-- Explicit configured credential/provider binding; this is not observed native account origin.
CREATE TABLE OpenCodeAdaptationAccountMappings (
    AccountId TEXT PRIMARY KEY REFERENCES Accounts(Id) ON DELETE CASCADE,
    NativeProviderId TEXT NOT NULL CHECK(length(NativeProviderId) BETWEEN 1 AND 128 AND NativeProviderId NOT GLOB '*[^a-zA-Z0-9_-]*'),
    SecretReference TEXT NOT NULL REFERENCES SecretReferences(Reference),
    ConfiguredByInstanceId TEXT NOT NULL,
    ConfiguredAtUtc TEXT NOT NULL
);
CREATE TRIGGER TR_AdaptationMapping_Insert BEFORE INSERT ON OpenCodeAdaptationAccountMappings
WHEN NOT EXISTS(SELECT 1 FROM Accounts a JOIN ProviderProfiles p ON p.Id=a.ProviderProfileId
    JOIN SecretReferences k ON k.Reference=a.SecretReference
    WHERE a.Id=NEW.AccountId AND p.Backend='OpenCode' AND a.SecretReference=NEW.SecretReference
      AND k.Kind='ProviderApiKey' AND k.State='Active')
BEGIN SELECT RAISE(ABORT,'Adaptation requires the selected account API-key reference.'); END;

CREATE TABLE WorkflowAdaptationRuntimeOwners (
    AdmissionId TEXT PRIMARY KEY REFERENCES WorkflowAdaptationPromptAdmissions(Id),
    SessionId TEXT NOT NULL UNIQUE REFERENCES Sessions(Id),
    ExecutionId TEXT NOT NULL UNIQUE REFERENCES Executions(Id),
    NativeExecutionId TEXT NOT NULL UNIQUE,
    PrimaryInstanceId TEXT NOT NULL,
    WorkingDirectory TEXT NOT NULL,
    ConfigurationSha256 TEXT NOT NULL CHECK(length(ConfigurationSha256)=64 AND ConfigurationSha256 NOT GLOB '*[^0-9A-F]*'),
    ExecutableSha256 TEXT NOT NULL CHECK(length(ExecutableSha256)=64 AND ExecutableSha256 NOT GLOB '*[^0-9A-F]*'),
    CredentialReferenceSha256 TEXT NOT NULL CHECK(length(CredentialReferenceSha256)=64 AND CredentialReferenceSha256 NOT GLOB '*[^0-9A-F]*'),
    NativeProviderId TEXT NOT NULL,
    EndpointOrigin TEXT NULL,
    State TEXT NOT NULL CHECK(State IN ('Reserved','Ready','Terminated'))
);
CREATE TABLE WorkflowAdaptationRuntimeChecks (
    Id TEXT PRIMARY KEY,
    AdmissionId TEXT NOT NULL REFERENCES WorkflowAdaptationRuntimeOwners(AdmissionId),
    NativeExecutionId TEXT NOT NULL,
    PrimaryInstanceId TEXT NOT NULL,
    Phase TEXT NOT NULL CHECK(Phase IN ('Reserve','Ready','Stopped')),
    OccurredAtUtc TEXT NOT NULL,
    UNIQUE(AdmissionId,Phase)
);
CREATE TRIGGER TR_AdaptationRuntimeOwner_Insert BEFORE INSERT ON WorkflowAdaptationRuntimeOwners
WHEN NEW.State!='Reserved' OR NEW.EndpointOrigin IS NOT NULL OR NOT EXISTS(
    SELECT 1 FROM WorkflowAdaptationPromptAdmissions a JOIN Routes r ON r.Id=a.RouteId
    JOIN Sessions s ON s.Id=NEW.SessionId JOIN Executions e ON e.Id=NEW.ExecutionId AND e.SessionId=s.Id
    JOIN OpenCodeAdaptationAccountMappings m ON m.AccountId=s.AccountId
    WHERE a.Id=NEW.AdmissionId AND a.State='CreatePreflight' AND a.PrimaryInstanceId=NEW.PrimaryInstanceId
      AND s.ProjectId=a.ProjectId AND s.Backend='OpenCode' AND s.ProviderProfileId=r.ProviderProfileId
      AND s.AccountId=r.AccountId AND s.ModelId=r.ModelId AND s.Role='workflow-adaptation'
      AND s.State='Ambiguous' AND s.ActiveExecutionId=e.Id AND s.NativeSessionId IS NULL
      AND e.State='Ambiguous' AND e.EndedAtUtc IS NULL AND e.RequestedRouteId=a.RouteId
      AND m.NativeProviderId=NEW.NativeProviderId)
BEGIN SELECT RAISE(ABORT,'Native runtime ownership requires its exact pre-launch admitted reservation.'); END;
CREATE TRIGGER TR_AdaptationRuntimeCheck_Insert BEFORE INSERT ON WorkflowAdaptationRuntimeChecks
WHEN NOT EXISTS(SELECT 1 FROM WorkflowAdaptationRuntimeOwners o WHERE o.AdmissionId=NEW.AdmissionId
    AND o.NativeExecutionId=NEW.NativeExecutionId AND o.PrimaryInstanceId=NEW.PrimaryInstanceId
    AND ((NEW.Phase='Reserve' AND o.State='Reserved') OR (NEW.Phase='Ready' AND o.State='Reserved')
      OR (NEW.Phase='Stopped' AND o.State IN ('Reserved','Ready'))))
BEGIN SELECT RAISE(ABORT,'Runtime audit requires its exact current owner.'); END;
CREATE TRIGGER TR_AdaptationRuntimeCheck_NoUpdate BEFORE UPDATE ON WorkflowAdaptationRuntimeChecks
BEGIN SELECT RAISE(ABORT,'Native runtime audit is immutable.'); END;
CREATE TRIGGER TR_AdaptationRuntimeCheck_NoDelete BEFORE DELETE ON WorkflowAdaptationRuntimeChecks
BEGIN SELECT RAISE(ABORT,'Native runtime audit is immutable.'); END;
CREATE TRIGGER TR_AdaptationRuntimeOwner_Update BEFORE UPDATE ON WorkflowAdaptationRuntimeOwners
WHEN NEW.AdmissionId IS NOT OLD.AdmissionId OR NEW.SessionId IS NOT OLD.SessionId OR NEW.ExecutionId IS NOT OLD.ExecutionId
 OR NEW.NativeExecutionId IS NOT OLD.NativeExecutionId OR NEW.PrimaryInstanceId IS NOT OLD.PrimaryInstanceId
 OR NEW.WorkingDirectory IS NOT OLD.WorkingDirectory OR NEW.ConfigurationSha256 IS NOT OLD.ConfigurationSha256
 OR NEW.ExecutableSha256 IS NOT OLD.ExecutableSha256 OR NEW.CredentialReferenceSha256 IS NOT OLD.CredentialReferenceSha256
 OR NEW.NativeProviderId IS NOT OLD.NativeProviderId
 OR NOT ((OLD.State='Reserved' AND NEW.State='Ready' AND OLD.EndpointOrigin IS NULL AND NEW.EndpointOrigin IS NOT NULL
       AND EXISTS(SELECT 1 FROM WorkflowAdaptationRuntimeChecks c WHERE c.AdmissionId=OLD.AdmissionId AND c.Phase='Ready'))
    OR (OLD.State IN ('Reserved','Ready') AND NEW.State='Terminated' AND NEW.EndpointOrigin IS OLD.EndpointOrigin
       AND EXISTS(SELECT 1 FROM WorkflowAdaptationRuntimeChecks c WHERE c.AdmissionId=OLD.AdmissionId AND c.Phase='Stopped')))
BEGIN SELECT RAISE(ABORT,'Runtime owner identity is immutable and advances with its exact audit.'); END;
CREATE TRIGGER TR_AdaptationRuntimeOwner_NoDelete BEFORE DELETE ON WorkflowAdaptationRuntimeOwners
BEGIN SELECT RAISE(ABORT,'Native runtime ownership is retained.'); END;
CREATE TRIGGER TR_AdaptationMapping_Update BEFORE UPDATE ON OpenCodeAdaptationAccountMappings
WHEN NEW.AccountId IS NOT OLD.AccountId OR NOT EXISTS(SELECT 1 FROM Accounts a JOIN ProviderProfiles p ON p.Id=a.ProviderProfileId
    JOIN SecretReferences k ON k.Reference=a.SecretReference WHERE a.Id=NEW.AccountId AND p.Backend='OpenCode'
      AND a.SecretReference=NEW.SecretReference AND k.Kind='ProviderApiKey' AND k.State='Active')
 OR EXISTS(SELECT 1 FROM WorkflowAdaptationRuntimeOwners o JOIN Sessions s ON s.Id=o.SessionId
    WHERE s.AccountId=OLD.AccountId AND o.State!='Terminated')
BEGIN SELECT RAISE(ABORT,'A running runtime retains its configured account binding.'); END;
CREATE TRIGGER TR_AdaptationMapping_Delete BEFORE DELETE ON OpenCodeAdaptationAccountMappings
WHEN EXISTS(SELECT 1 FROM WorkflowAdaptationRuntimeOwners o JOIN Sessions s ON s.Id=o.SessionId
    WHERE s.AccountId=OLD.AccountId AND o.State!='Terminated')
BEGIN SELECT RAISE(ABORT,'A running runtime retains its configured account binding.'); END;

-- Old HTTP-only reservations do not acquire termination authority from this migration.
DROP TRIGGER TR_AdaptationExecution_Retain;
DROP TRIGGER TR_AdaptationSession_Retain;
CREATE TRIGGER TR_AdaptationExecution_Retain BEFORE UPDATE ON Executions
WHEN (EXISTS(SELECT 1 FROM WorkflowAdaptationNativeBindings b WHERE b.ExecutionId=OLD.Id)
      OR EXISTS(SELECT 1 FROM WorkflowAdaptationRuntimeOwners o WHERE o.ExecutionId=OLD.Id))
 AND (NEW.Id IS NOT OLD.Id OR NEW.SessionId IS NOT OLD.SessionId OR NEW.RequestedRouteId IS NOT OLD.RequestedRouteId
      OR NEW.StartedAtUtc IS NOT OLD.StartedAtUtc
      OR ((NEW.State!='Ambiguous' OR NEW.EndedAtUtc IS NOT NULL) AND NOT EXISTS(
          SELECT 1 FROM WorkflowAdaptationRuntimeOwners o JOIN WorkflowAdaptationRuntimeChecks c ON c.AdmissionId=o.AdmissionId
          WHERE o.ExecutionId=OLD.Id AND o.State='Terminated' AND c.Phase='Stopped'
            AND c.NativeExecutionId=o.NativeExecutionId AND c.PrimaryInstanceId=o.PrimaryInstanceId)))
BEGIN SELECT RAISE(ABORT,'Native adaptation termination is unconfirmed; execution remains owned.'); END;
CREATE TRIGGER TR_AdaptationSession_Retain BEFORE UPDATE ON Sessions
WHEN (EXISTS(SELECT 1 FROM WorkflowAdaptationNativeBindings b WHERE b.SessionId=OLD.Id)
      OR EXISTS(SELECT 1 FROM WorkflowAdaptationRuntimeOwners o WHERE o.SessionId=OLD.Id))
 AND (NEW.Id IS NOT OLD.Id OR NEW.ProjectId IS NOT OLD.ProjectId OR NEW.Backend IS NOT OLD.Backend
      OR NEW.ProviderProfileId IS NOT OLD.ProviderProfileId OR NEW.AccountId IS NOT OLD.AccountId
      OR NEW.ModelId IS NOT OLD.ModelId OR NEW.WorkspaceRootPath IS NOT OLD.WorkspaceRootPath OR NEW.Role IS NOT OLD.Role
      OR ((NEW.ActiveExecutionId IS NOT OLD.ActiveExecutionId OR NEW.State!='Ambiguous') AND NOT EXISTS(
          SELECT 1 FROM WorkflowAdaptationRuntimeOwners o JOIN WorkflowAdaptationRuntimeChecks c ON c.AdmissionId=o.AdmissionId
          WHERE o.SessionId=OLD.Id AND o.State='Terminated' AND c.Phase='Stopped'
            AND c.NativeExecutionId=o.NativeExecutionId AND c.PrimaryInstanceId=o.PrimaryInstanceId))
      OR (NEW.NativeSessionId IS NOT OLD.NativeSessionId AND NOT (OLD.NativeSessionId IS NULL AND NEW.NativeSessionId IS NOT NULL
          AND EXISTS(SELECT 1 FROM WorkflowAdaptationNativeBindings b WHERE b.SessionId=OLD.Id AND b.State='CreateAuthorized'))))
BEGIN SELECT RAISE(ABORT,'Native adaptation termination is unconfirmed; session remains owned.'); END;
