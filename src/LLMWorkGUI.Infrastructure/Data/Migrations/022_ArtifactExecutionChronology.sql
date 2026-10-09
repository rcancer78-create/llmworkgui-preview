-- Strengthen new collection writes without rewriting historical evidence or migration 021.
DROP TRIGGER TR_Artifacts_ExecutionBelongsToRun;
CREATE TRIGGER TR_Artifacts_ExecutionBelongsToRun
BEFORE INSERT ON Artifacts
WHEN NEW.WorkflowRunId IS NOT NULL AND NEW.ExecutionId IS NOT NULL
 AND NOT EXISTS (
    SELECT 1 FROM Executions e JOIN Sessions s ON s.Id=e.SessionId
    JOIN WorkflowRuns w ON w.Id=NEW.WorkflowRunId
    WHERE e.Id=NEW.ExecutionId AND e.State='Succeeded' AND e.FailureReason='None'
      AND s.WorkflowRunId=w.Id AND s.ProjectId=w.ProjectId
      AND julianday(e.EndedAtUtc)>=julianday(e.StartedAtUtc)
      AND julianday(e.StartedAtUtc)>=julianday(e.CreatedAtUtc)
      AND julianday(e.CreatedAtUtc)>=julianday(w.StartedAtUtc)
      AND w.State IN ('Pending','Running','Suspended') AND w.EndedAtUtc IS NULL
      AND json_extract(w.EvidenceRedactedJson,'$.currentStageId')=NEW.StageId
 )
BEGIN
    SELECT RAISE(ABORT, 'Artifact execution must have valid run chronology and belong to the current nonterminal stage.');
END;
