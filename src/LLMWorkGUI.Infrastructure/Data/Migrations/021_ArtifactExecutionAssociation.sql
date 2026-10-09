-- New run/execution associations must be real at insertion. Historical unlinked artifacts retain
-- their existing meaning; operator attachment is not native-response or reviewer authority.
CREATE TRIGGER TR_Artifacts_ExecutionBelongsToRun
BEFORE INSERT ON Artifacts
WHEN NEW.WorkflowRunId IS NOT NULL AND NEW.ExecutionId IS NOT NULL
 AND NOT EXISTS (
    SELECT 1 FROM Executions e JOIN Sessions s ON s.Id=e.SessionId
    JOIN WorkflowRuns w ON w.Id=NEW.WorkflowRunId
    WHERE e.Id=NEW.ExecutionId AND e.State='Succeeded' AND e.EndedAtUtc IS NOT NULL
      AND e.FailureReason='None' AND s.WorkflowRunId=w.Id AND s.ProjectId=w.ProjectId
      AND julianday(e.EndedAtUtc)>=julianday(e.CreatedAtUtc)
      AND julianday(e.CreatedAtUtc)>=julianday(w.StartedAtUtc)
 )
BEGIN
    SELECT RAISE(ABORT, 'Artifact execution must belong to the same run and project and have succeeded.');
END;

CREATE TRIGGER TR_Artifacts_ExecutionAssociationIsImmutable
BEFORE UPDATE ON Artifacts
WHEN OLD.WorkflowRunId IS NOT NULL AND NEW.ExecutionId IS NOT OLD.ExecutionId
BEGIN
    SELECT RAISE(ABORT, 'A recorded run artifact keeps its execution association.');
END;
