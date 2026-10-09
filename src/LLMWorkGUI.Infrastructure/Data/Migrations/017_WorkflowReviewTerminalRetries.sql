-- A new explicit request may retry a known Failed/Cancelled review of the same bytes.
-- Retain every prior binding, its execution uniqueness, immutable identity and observation guards.
-- Active, successful and uncertain attempts still exclude another admission. SQLite serializes
-- this trigger with the dispatch transaction, including callers that bypass the application gate.
CREATE TABLE WorkflowReviewExecutions_New (
    Id TEXT NOT NULL PRIMARY KEY,
    ExecutionId TEXT NOT NULL UNIQUE REFERENCES Executions (Id) ON DELETE CASCADE,
    SessionId TEXT NOT NULL REFERENCES Sessions (Id) ON DELETE CASCADE,
    WorkflowRunId TEXT NOT NULL REFERENCES WorkflowRuns (Id) ON DELETE CASCADE,
    ReviewerRole TEXT NOT NULL,
    StageId TEXT NOT NULL,
    ReviewedArtifactId TEXT NOT NULL,
    ReviewedArtifactHash TEXT NOT NULL,
    IsReadOnly INTEGER NOT NULL CHECK (IsReadOnly IN (0, 1)),
    RequestedRouteId TEXT NOT NULL REFERENCES Routes (Id),
    ObservedRouteId TEXT NULL REFERENCES Routes (Id) ON DELETE SET NULL,
    RequestedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

INSERT INTO WorkflowReviewExecutions_New SELECT * FROM WorkflowReviewExecutions;
DROP TABLE WorkflowReviewExecutions;
ALTER TABLE WorkflowReviewExecutions_New RENAME TO WorkflowReviewExecutions;

CREATE INDEX IX_WorkflowReviewExecutions_WorkflowRunId_StageId
    ON WorkflowReviewExecutions (WorkflowRunId, StageId, ReviewerRole);

CREATE INDEX IX_WorkflowReviewExecutions_ObservedRouteId
    ON WorkflowReviewExecutions (ObservedRouteId);

-- A binding has to name the four things it claims to be about. Every one of them can be empty under
-- 'TEXT NOT NULL', so the shape of the claim is checked here as well as in the reader, and nothing that
-- exists today is touched: the trigger fires on INSERT only.
CREATE TRIGGER TR_WorkflowReviewExecutions_IdentityIsComplete
BEFORE INSERT ON WorkflowReviewExecutions
WHEN length(trim(NEW.Id)) = 0
     OR length(trim(NEW.ExecutionId)) = 0
     OR length(trim(NEW.SessionId)) = 0
     OR length(trim(NEW.WorkflowRunId)) = 0
     OR length(trim(NEW.ReviewerRole)) = 0
     OR length(trim(NEW.StageId)) = 0
     OR length(trim(NEW.ReviewedArtifactId)) = 0
     OR length(trim(NEW.ReviewedArtifactHash)) = 0
     OR length(trim(NEW.RequestedRouteId)) = 0
     OR (NEW.ObservedRouteId IS NOT NULL AND length(trim(NEW.ObservedRouteId)) = 0)
BEGIN
    SELECT RAISE(ABORT, 'A reviewer execution binding must name its run, stage, role, artifact and requested route.');
END;

-- The binding of a reviewer turn is fixed at the moment it is written. Only the observed route and the
-- update timestamp may change afterwards, so a recorded turn cannot be re-used as evidence for something it
-- never did.
--
-- The observed route is deliberately absent from this list. It starts NULL, is set from what a backend
-- reported, may be corrected by a later report of the same turn, and may be cleared when a Routes row this
-- turn observed is deleted - the foreign key's ON DELETE SET NULL has to be able to do that, because
-- refusing a route deletion because a half-finished reviewer turn once named it would be a worse outcome
-- than losing the observation. Every one of those directions fails closed: a binding whose observed route is
-- null authorizes nothing, so clearing it can only withdraw evidence and never manufacture it.
CREATE TRIGGER TR_WorkflowReviewExecutions_BindingIsImmutable
BEFORE UPDATE ON WorkflowReviewExecutions
WHEN NEW.Id IS NOT OLD.Id
     OR NEW.ExecutionId IS NOT OLD.ExecutionId
     OR NEW.SessionId IS NOT OLD.SessionId
     OR NEW.WorkflowRunId IS NOT OLD.WorkflowRunId
     OR NEW.ReviewerRole IS NOT OLD.ReviewerRole
     OR NEW.StageId IS NOT OLD.StageId
     OR NEW.ReviewedArtifactId IS NOT OLD.ReviewedArtifactId
     OR NEW.ReviewedArtifactHash IS NOT OLD.ReviewedArtifactHash
     OR NEW.IsReadOnly IS NOT OLD.IsReadOnly
     OR NEW.RequestedRouteId IS NOT OLD.RequestedRouteId
     OR NEW.RequestedAtUtc IS NOT OLD.RequestedAtUtc
BEGIN
    SELECT RAISE(ABORT, 'A recorded reviewer execution keeps the run, stage, role, artifact and requested route it was written with.');
END;

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
--   * This guard from migration 009 is recreated after the table rebuild above. Existing rows were
--     copied before the rename; the original migration files and their recorded checksums stay intact.
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

-- INSERT must enforce the same execution authority as the UPDATE guard in migration 009.
CREATE TRIGGER TR_WorkflowReviewExecutions_InsertObservedRouteIsObserved
BEFORE INSERT ON WorkflowReviewExecutions
WHEN NEW.ObservedRouteId IS NOT NULL
     AND NEW.ObservedRouteId IS NOT (
         SELECT e.ObservedRouteId FROM Executions e WHERE e.Id = NEW.ExecutionId)
BEGIN
    SELECT RAISE(ABORT, 'A reviewer binding cannot insert an observation absent from its execution.');
END;

CREATE TRIGGER TR_WorkflowReviewExecutions_NoBlockingAttempt
BEFORE INSERT ON WorkflowReviewExecutions
WHEN EXISTS (
    SELECT 1 FROM WorkflowReviewExecutions b JOIN Executions e ON e.Id = b.ExecutionId
    WHERE b.WorkflowRunId = NEW.WorkflowRunId AND b.StageId = NEW.StageId
      AND b.ReviewerRole = NEW.ReviewerRole AND b.ReviewedArtifactHash = NEW.ReviewedArtifactHash
      AND b.IsReadOnly = NEW.IsReadOnly AND e.State NOT IN ('Failed', 'Cancelled'))
BEGIN
    SELECT RAISE(ABORT, 'A reviewer attempt is active, successful or uncertain; replay is refused.');
END;

CREATE TRIGGER TR_WorkflowReviewExecutions_RetryPredecessorMatches
BEFORE INSERT ON WorkflowReviewExecutions
WHEN (
    EXISTS (SELECT 1 FROM WorkflowReviewExecutions b
        WHERE b.WorkflowRunId=NEW.WorkflowRunId AND b.StageId=NEW.StageId AND b.ReviewerRole=NEW.ReviewerRole
          AND b.ReviewedArtifactHash=NEW.ReviewedArtifactHash AND b.IsReadOnly=NEW.IsReadOnly)
    AND NOT EXISTS (
        SELECT 1 FROM WorkflowReviewExecutions b JOIN Executions old ON old.Id=b.ExecutionId
        JOIN Executions next ON next.Id=NEW.ExecutionId AND next.RetryOfExecutionId=old.Id
        WHERE b.WorkflowRunId=NEW.WorkflowRunId AND b.StageId=NEW.StageId AND b.ReviewerRole=NEW.ReviewerRole
          AND b.ReviewedArtifactHash=NEW.ReviewedArtifactHash AND b.IsReadOnly=NEW.IsReadOnly
          AND old.State IN ('Failed','Cancelled'))
) OR (
    NOT EXISTS (SELECT 1 FROM WorkflowReviewExecutions b
        WHERE b.WorkflowRunId=NEW.WorkflowRunId AND b.StageId=NEW.StageId AND b.ReviewerRole=NEW.ReviewerRole
          AND b.ReviewedArtifactHash=NEW.ReviewedArtifactHash AND b.IsReadOnly=NEW.IsReadOnly)
    AND EXISTS (SELECT 1 FROM Executions WHERE Id=NEW.ExecutionId AND RetryOfExecutionId IS NOT NULL)
)
BEGIN
    SELECT RAISE(ABORT, 'Reviewer retry must name a matching known Failed/Cancelled predecessor.');
END;

-- Once a retry exists, its failed predecessor cannot be resurrected by a late completion.
CREATE TRIGGER TR_Executions_RetriedReviewTerminalIsImmutable
BEFORE UPDATE OF State ON Executions
WHEN OLD.State IN ('Failed', 'Cancelled') AND NEW.State IS NOT OLD.State
  AND EXISTS (
    SELECT 1 FROM WorkflowReviewExecutions prior JOIN WorkflowReviewExecutions other
      ON other.WorkflowRunId = prior.WorkflowRunId AND other.StageId = prior.StageId
      AND other.ReviewerRole = prior.ReviewerRole AND other.ReviewedArtifactHash = prior.ReviewedArtifactHash
      AND other.IsReadOnly = prior.IsReadOnly AND other.ExecutionId != prior.ExecutionId
    WHERE prior.ExecutionId = OLD.Id)
BEGIN
    SELECT RAISE(ABORT, 'A retried reviewer attempt keeps its recorded terminal state.');
END;

-- Admission validates lineage once; later completion/upserts must retain that admitted parent.
CREATE TRIGGER TR_Executions_ReviewerRetryLineageIsImmutable
BEFORE UPDATE OF RetryOfExecutionId ON Executions
WHEN NEW.RetryOfExecutionId IS NOT OLD.RetryOfExecutionId
  AND EXISTS (SELECT 1 FROM WorkflowReviewExecutions WHERE ExecutionId=OLD.Id)
BEGIN
    SELECT RAISE(ABORT, 'An admitted reviewer execution keeps its recorded retry predecessor.');
END;
