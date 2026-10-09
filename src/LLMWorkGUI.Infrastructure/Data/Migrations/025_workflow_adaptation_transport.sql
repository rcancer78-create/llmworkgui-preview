-- Local HTTP admission and uncertain native ownership. No claim of native permission or termination.
CREATE TABLE WorkflowAdaptationNativeBindings (
    AdmissionId TEXT PRIMARY KEY REFERENCES WorkflowAdaptationPromptAdmissions(Id),
    SessionId TEXT NOT NULL UNIQUE REFERENCES Sessions(Id),
    ExecutionId TEXT NOT NULL UNIQUE REFERENCES Executions(Id),
    EndpointOrigin TEXT NOT NULL,
    PrimaryInstanceId TEXT NOT NULL,
    State TEXT NOT NULL CHECK(State IN ('CreateAuthorized','Bound','PromptAuthorized')),
    NativeDirectoryQuerySha256 TEXT NOT NULL CHECK(length(NativeDirectoryQuerySha256)=64 AND NativeDirectoryQuerySha256 NOT GLOB '*[^0-9A-F]*')
);
CREATE TRIGGER TR_WorkflowAdaptationNativeBindings_Insert BEFORE INSERT ON WorkflowAdaptationNativeBindings
WHEN NEW.State!='CreateAuthorized' OR NOT EXISTS (
    SELECT 1 FROM WorkflowAdaptationPromptAdmissions a JOIN Sessions s ON s.Id=NEW.SessionId
    JOIN Executions e ON e.Id=NEW.ExecutionId AND e.SessionId=s.Id JOIN Routes r ON r.Id=a.RouteId
    WHERE a.Id=NEW.AdmissionId AND a.State='CreatePreflight' AND a.PrimaryInstanceId=NEW.PrimaryInstanceId
      AND s.ProjectId=a.ProjectId AND s.Backend='OpenCode' AND s.ProviderProfileId=r.ProviderProfileId
      AND s.AccountId=r.AccountId AND s.ModelId=r.ModelId AND s.Role='workflow-adaptation'
      AND s.NativeSessionId IS NULL AND s.ActiveExecutionId=e.Id AND s.State='Ambiguous'
      AND e.RequestedRouteId=a.RouteId AND e.State='Ambiguous' AND e.EndedAtUtc IS NULL)
BEGIN SELECT RAISE(ABORT,'Adaptation ownership requires its exact admitted execution.'); END;
CREATE TRIGGER TR_WorkflowAdaptationNativeBindings_Update BEFORE UPDATE ON WorkflowAdaptationNativeBindings
WHEN NEW.AdmissionId IS NOT OLD.AdmissionId OR NEW.SessionId IS NOT OLD.SessionId
  OR NEW.ExecutionId IS NOT OLD.ExecutionId OR NEW.EndpointOrigin IS NOT OLD.EndpointOrigin
  OR NEW.PrimaryInstanceId IS NOT OLD.PrimaryInstanceId
  OR NOT ((OLD.State='CreateAuthorized' AND NEW.State='Bound')
      OR (OLD.State='Bound' AND NEW.State='PromptAuthorized' AND NEW.NativeDirectoryQuerySha256=OLD.NativeDirectoryQuerySha256))
BEGIN SELECT RAISE(ABORT,'Adaptation transport binding is immutable and advances once.'); END;
CREATE TRIGGER TR_WorkflowAdaptationNativeBindings_NoDelete BEFORE DELETE ON WorkflowAdaptationNativeBindings
BEGIN SELECT RAISE(ABORT,'Uncertain adaptation ownership is retained.'); END;

CREATE TABLE WorkflowAdaptationTransportChecks (
    Id TEXT PRIMARY KEY,
    AdmissionId TEXT NOT NULL REFERENCES WorkflowAdaptationNativeBindings(AdmissionId),
    ExecutionId TEXT NOT NULL REFERENCES Executions(Id),
    Phase TEXT NOT NULL CHECK(Phase IN ('Create','Bind','Prompt','Abort')),
    PayloadSha256 TEXT NOT NULL CHECK(length(PayloadSha256)=64 AND PayloadSha256 NOT GLOB '*[^0-9A-F]*'),
    EndpointSha256 TEXT NOT NULL CHECK(length(EndpointSha256)=64 AND EndpointSha256 NOT GLOB '*[^0-9A-F]*'),
    OccurredAtUtc TEXT NOT NULL
);
CREATE TRIGGER TR_WorkflowAdaptationTransportChecks_Binding BEFORE INSERT ON WorkflowAdaptationTransportChecks
WHEN NOT EXISTS (SELECT 1 FROM WorkflowAdaptationNativeBindings b WHERE b.AdmissionId=NEW.AdmissionId AND b.ExecutionId=NEW.ExecutionId)
BEGIN SELECT RAISE(ABORT,'Adaptation audit requires its exact execution binding.'); END;
CREATE TRIGGER TR_WorkflowAdaptationTransportChecks_NoUpdate BEFORE UPDATE ON WorkflowAdaptationTransportChecks
BEGIN SELECT RAISE(ABORT,'Adaptation transport audit is immutable.'); END;
CREATE TRIGGER TR_WorkflowAdaptationTransportChecks_NoDelete BEFORE DELETE ON WorkflowAdaptationTransportChecks
BEGIN SELECT RAISE(ABORT,'Adaptation transport audit is immutable.'); END;

-- Neither an HTTP acknowledgement nor a generic idle/recovery projection proves native termination.
-- There is intentionally no release transition until a native termination authority is implemented.
CREATE TRIGGER TR_AdaptationExecution_Retain BEFORE UPDATE ON Executions
WHEN EXISTS(SELECT 1 FROM WorkflowAdaptationNativeBindings b WHERE b.ExecutionId=OLD.Id)
 AND (NEW.Id IS NOT OLD.Id OR NEW.SessionId IS NOT OLD.SessionId OR NEW.RequestedRouteId IS NOT OLD.RequestedRouteId
      OR NEW.State!='Ambiguous' OR NEW.EndedAtUtc IS NOT NULL OR NEW.StartedAtUtc IS NOT OLD.StartedAtUtc)
BEGIN SELECT RAISE(ABORT,'Native adaptation termination is unconfirmed; execution remains owned.'); END;
CREATE TRIGGER TR_AdaptationSession_Retain BEFORE UPDATE ON Sessions
WHEN EXISTS(SELECT 1 FROM WorkflowAdaptationNativeBindings b WHERE b.SessionId=OLD.Id)
 AND (NEW.Id IS NOT OLD.Id OR NEW.ProjectId IS NOT OLD.ProjectId OR NEW.Backend IS NOT OLD.Backend
      OR NEW.ProviderProfileId IS NOT OLD.ProviderProfileId OR NEW.AccountId IS NOT OLD.AccountId
      OR NEW.ModelId IS NOT OLD.ModelId OR NEW.WorkspaceRootPath IS NOT OLD.WorkspaceRootPath
      OR NEW.ActiveExecutionId IS NOT OLD.ActiveExecutionId OR NEW.State!='Ambiguous' OR NEW.Role IS NOT OLD.Role
      OR (NEW.NativeSessionId IS NOT OLD.NativeSessionId AND NOT (OLD.NativeSessionId IS NULL AND NEW.NativeSessionId IS NOT NULL
          AND EXISTS(SELECT 1 FROM WorkflowAdaptationNativeBindings b WHERE b.SessionId=OLD.Id AND b.State='CreateAuthorized'))))
BEGIN SELECT RAISE(ABORT,'Native adaptation termination is unconfirmed; session remains owned.'); END;
CREATE TRIGGER TR_AdaptationAdmission_Identity BEFORE UPDATE ON WorkflowAdaptationPromptAdmissions
WHEN NEW.Id IS NOT OLD.Id OR NEW.ProjectId IS NOT OLD.ProjectId OR NEW.VersionId IS NOT OLD.VersionId
 OR NEW.RouteId IS NOT OLD.RouteId OR NEW.PromptSha256 IS NOT OLD.PromptSha256 OR NEW.PolicySha256 IS NOT OLD.PolicySha256
 OR NEW.PrimaryInstanceId IS NOT OLD.PrimaryInstanceId OR NEW.ExpiresAtUtc IS NOT OLD.ExpiresAtUtc
 OR NOT ((OLD.State='Prepared' AND NEW.State='CreatePreflight') OR (OLD.State='CreatePreflight' AND NEW.State='PromptPreflight'))
BEGIN SELECT RAISE(ABORT,'Adaptation admission identity is immutable and advances once.'); END;
CREATE TRIGGER TR_AdaptationAdmission_NoDelete BEFORE DELETE ON WorkflowAdaptationPromptAdmissions
BEGIN SELECT RAISE(ABORT,'Adaptation admissions are retained.'); END;
