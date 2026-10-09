-- The durable binding between a reviewer execution and the run, stage, role and artifact it was about
-- (Phase 10 model-review provenance foundation).
--
-- A ReviewerVerdictRecord names a role, a route string, a hash and a verdict. Until now that record was the
-- whole of the evidence, which means any caller - a screen, a recovery scenario, a test - could write an
-- approval that the transition gate would accept, and a route label such as 'route-opencode' could be
-- presented as if a model had been observed on it. Neither is possible to detect afterwards: the row looks
-- exactly like a real one.
--
-- What was missing was the other half of the claim. Executions already persisted RequestedRouteId and
-- ObservedRouteId, but nothing recorded *which* run, stage, reviewer role and stored artifact a reviewer
-- turn was about, and nothing could say whether the turn was read-only. This table is that half:
--
--   * ExecutionId, SessionId, WorkflowRunId and the two route columns are foreign keys onto rows that
--     already exist, so a binding can only be written for a real execution of a real session on a real run
--     against a real Routes row. 'route-opencode' is not a Routes row and can never be written here.
--   * RequestedRouteId is written before the turn is dispatched and never changes. ObservedRouteId starts
--     NULL and is only ever set from what a backend reported. There is no default, no trigger and no column
--     default that could turn one into the other.
--   * ReviewedArtifactId and ReviewedArtifactHash bind the turn to the exact artifact row and the exact
--     bytes, so a verdict of a replaced artifact cannot be satisfied by the artifact that replaced it.
--   * IsReadOnly is stored rather than inferred: a reviewer turn that was permitted to write is not
--     evidence of a review, and nothing else in the schema records that distinction.
--
-- The uniqueness constraints are the storage-level half of the replay rules:
--
--   * One binding per execution - an execution is one reviewer turn about one thing, and re-binding it to
--     a second run, stage, role or artifact would make a single turn two pieces of evidence.
--   * One binding per run, stage, role, reviewed hash and read-only flag - a repeated request for the same
--     review of the same bytes is a duplicate and is refused by the store rather than left to a caller to
--     notice. Asking the same role to review the same artifact again therefore only becomes possible once
--     the artifact itself is replaced, which is also the only way a previous verdict of that role stops
--     authorizing the transition.
--
-- Nothing here is destructive and nothing that exists is re-validated: this is a new table, and a database
-- that has never recorded a model review simply stays empty. The verdicts already persisted without a
-- binding are not deleted or rewritten - they simply cannot satisfy a pinned model-review gate any more,
-- which is the intended effect and is applied by the domain, not by rewriting history.
CREATE TABLE WorkflowReviewExecutions (
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
    UpdatedAtUtc TEXT NOT NULL,
    UNIQUE (WorkflowRunId, StageId, ReviewerRole, ReviewedArtifactHash, IsReadOnly)
);

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
