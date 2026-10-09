-- INSERT must enforce the same execution authority as the UPDATE guard in migration 009.
CREATE TRIGGER TR_WorkflowReviewExecutions_InsertObservedRouteIsObserved
BEFORE INSERT ON WorkflowReviewExecutions
WHEN NEW.ObservedRouteId IS NOT NULL
     AND NEW.ObservedRouteId IS NOT (
         SELECT e.ObservedRouteId FROM Executions e WHERE e.Id = NEW.ExecutionId)
BEGIN
    SELECT RAISE(ABORT, 'A reviewer binding cannot insert an observation absent from its execution.');
END;
