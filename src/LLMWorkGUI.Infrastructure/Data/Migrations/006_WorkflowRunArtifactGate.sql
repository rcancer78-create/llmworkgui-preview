-- A run-scoped artifact that can authorize a gated stage transition (Phase 10A/10E durable artifact gate).
--
-- The Artifacts table already carries the two identities an artifact can belong to - a workflow run or an
-- execution - and the three values a stored document is addressed by: Kind, BlobId and HashSha256. What it
-- could not express is *which stage of which run* an artifact is evidence for, which is precisely what a
-- stage gate has to decide on: a reviewer approves a document, and the gate must be able to prove that the
-- document it approves is the newest stored artifact of this run, at this stage, of exactly the declared
-- kind. Without StageId a newer artifact of the same kind recorded anywhere - including one produced by a
-- failure loop through another stage - could be mistaken for the current one.
--
-- This migration is additive in the strict sense:
--
--   * StageId is nullable, so every row that exists today keeps its meaning. An execution-only row keeps a
--     NULL StageId and stays execution-only.
--   * The new trigger fires only for a row that names a WorkflowRunId. Nothing about the existing rows is
--     re-validated, rewritten or refused, and no existing row is deleted.
ALTER TABLE Artifacts ADD COLUMN StageId TEXT NULL;
--
-- The trigger is what makes a *new* run-scoped row complete rather than plausible. A row that names a run
-- has to name the stage it belongs to, the blob that holds its bytes and the hash those bytes produce, and
-- the blob id and the hash have to be the same value - because the blob id is the address the bytes are
-- stored under and is computed as their SHA-256, so a row where the two differ names content that cannot be
-- found. The hash form is checked in SQL as well: 'sha256:' followed by exactly 64 characters drawn from
-- 0-9a-f. GLOB is used rather than a comparison so that an upper-case digest is refused instead of being
-- accepted as the same value on a case-insensitive file system.
CREATE INDEX IX_Artifacts_WorkflowRunId_StageId_Kind
    ON Artifacts (WorkflowRunId, StageId, Kind, CreatedAtUtc);

CREATE TRIGGER TR_Artifacts_RunScopedArtifactIsComplete
BEFORE INSERT ON Artifacts
WHEN NEW.WorkflowRunId IS NOT NULL
     AND (
            NEW.StageId IS NULL
         OR length(trim(NEW.StageId)) = 0
         OR length(trim(NEW.Kind)) = 0
         OR NEW.BlobId IS NULL
         OR NEW.BlobId IS NOT NEW.HashSha256
         OR NEW.SizeBytes IS NULL
         OR length(NEW.BlobId) <> 71
         OR substr(NEW.BlobId, 1, 7) <> 'sha256:'
         OR substr(NEW.BlobId, 8) GLOB '*[^0-9a-f]*'
     )
BEGIN
    SELECT RAISE(ABORT, 'A run-scoped workflow artifact must name its stage, a stored blob and the hash of those bytes.');
END;

-- The evidence a transition is authorized by is what was stored, not what was claimed. A row whose blob id
-- or hash is rewritten after the fact would name a different document from the one the reviewers approved
-- while keeping the same artifact id, so an update that touches the four values that constitute the
-- evidence is refused. The same reasoning applies to a run-scoped row that gains or loses its stage, its
-- kind, or the run it belongs to: an execution-only row is unaffected, because none of the conditions below
-- can be met by a row that names no run.
CREATE TRIGGER TR_Artifacts_RunScopedArtifactIsImmutable
BEFORE UPDATE ON Artifacts
WHEN OLD.WorkflowRunId IS NOT NULL
     AND (
            NEW.Id IS NOT OLD.Id
         OR NEW.WorkflowRunId IS NOT OLD.WorkflowRunId
         OR NEW.StageId IS NOT OLD.StageId
         OR NEW.Kind IS NOT OLD.Kind
         OR NEW.BlobId IS NOT OLD.BlobId
         OR NEW.HashSha256 IS NOT OLD.HashSha256
         OR NEW.SizeBytes IS NOT OLD.SizeBytes
         OR NEW.DataClassification IS NOT OLD.DataClassification
         OR NEW.CreatedAtUtc IS NOT OLD.CreatedAtUtc
     )
BEGIN
    SELECT RAISE(ABORT, 'A recorded workflow artifact keeps the stage, kind, blob and hash it was stored with.');
END;
