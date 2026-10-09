-- The execution row is the authority for which route a reviewer turn actually used.
-- (Phase 10 model-review provenance foundation: additive correction of migration 008.)
--
-- Migration 008 bound a reviewer turn to a run, stage, role and artifact, and gave that binding its own
-- ObservedRouteId column. The column was readable, writable, and settable to whatever value the caller
-- happened to hold: the evidence store wrote `evidence.ObservedRouteId` verbatim and the transition gate
-- then believed the row. That made the observed route - the one field in this whole design that is supposed
-- to be something only a backend can produce - a value the same process could simply assert. Nothing in the
-- database stopped a caller from naming a Routes row the execution never went to, and nothing stopped a
-- caller from writing that row directly.
--
-- `Executions` has carried its own ObservedRouteId all along, and that is the row a backend's answer is
-- already written to by the turn that reported it. This trigger makes the binding agree with that row, so
-- the observed route can only ever be read back out of the execution:
--
--   * A binding may carry an observed route only when the execution it belongs to already carries exactly
--     that route. An execution with no observation and an execution that observed a different route are
--     both refused, so "no backend reported a route" and "the backend reported a different route" can never
--     be rewritten as "the backend reported the assigned route" by the same process that asked.
--   * Clearing the column stays legal, because the foreign key's ON DELETE SET NULL has to be able to do it
--     when the Routes row a completed turn observed is deleted. Clearing only ever withdraws evidence: a
--     binding whose observed route is null authorizes nothing, so this cannot manufacture an observation.
--   * Nothing that already exists is rewritten, altered or removed. Migration 008 keeps the exact text an
--     already migrated database recorded as its checksum, and this file is one further trigger.
CREATE TRIGGER TR_WorkflowReviewExecutions_ObservedRouteIsObserved
BEFORE UPDATE OF ObservedRouteId ON WorkflowReviewExecutions
WHEN NEW.ObservedRouteId IS NOT NULL
     AND NEW.ObservedRouteId IS NOT (
         SELECT e.ObservedRouteId
         FROM Executions e
         WHERE e.Id = NEW.ExecutionId)
BEGIN
    SELECT RAISE(ABORT, 'A reviewer execution binding may only carry the observed route its own execution row recorded.');
END;
