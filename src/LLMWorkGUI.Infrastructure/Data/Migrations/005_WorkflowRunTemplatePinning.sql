-- A workflow run pinned to an explicitly assigned workflow template version (Phase 10F).
--
-- A run already carries two identities: WorkflowPackageId/WorkflowVersionId name the *source* workflow
-- version the run was started from, and they keep the existing foreign keys, the console version match
-- and the WorkflowRuns indexes intact. This migration adds a *second*, separate identity - the assigned
-- execution template TemplateId/TemplateVersion - together with the two immutable snapshots the run needs
-- in order to advance without re-reading the template store later:
--
--   TemplateGraphSnapshotJson    the validated graph of exactly that template version
--   TemplateSchemeSnapshotJson   the execution scheme derived from that graph
--
-- The snapshots deliberately do NOT live in EvidenceRedactedJson: that column is rewritten on every
-- state transition, so a template snapshot stored there would be rewritten while the run advances and
-- would stop being evidence of what the run was actually pinned to.
--
-- Every column is nullable so that a row created before this migration keeps working unchanged: a legacy
-- run has all four columns NULL and continues to follow the process-wide standard scheme. The triggers
-- below keep the two situations apart - a run either records the whole identity or none of it, and it can
-- never gain, lose or rewrite it afterwards.
ALTER TABLE WorkflowRuns ADD COLUMN TemplateId TEXT NULL;
ALTER TABLE WorkflowRuns ADD COLUMN TemplateVersion INTEGER NULL;
ALTER TABLE WorkflowRuns ADD COLUMN TemplateGraphSnapshotJson TEXT NULL;
ALTER TABLE WorkflowRuns ADD COLUMN TemplateSchemeSnapshotJson TEXT NULL;

-- The identity is written as one unit inside the same INSERT that creates the run, so a partially pinned
-- run can never become visible. `(expr IS NULL)` yields 1 or 0 in SQLite, so the four flags are summed and
-- only 0 (a legacy run) or 4 (a pinned run) are accepted.
CREATE TRIGGER TR_WorkflowRuns_TemplateIdentityIsWhole
BEFORE INSERT ON WorkflowRuns
WHEN (
        (NEW.TemplateId IS NULL)
      + (NEW.TemplateVersion IS NULL)
      + (NEW.TemplateGraphSnapshotJson IS NULL)
      + (NEW.TemplateSchemeSnapshotJson IS NULL)
    ) NOT IN (0, 4)
BEGIN
    SELECT RAISE(ABORT, 'A workflow run must record its assigned template identity and both snapshots together, or none of them.');
END;

-- A pinned run must name a real version and carry two readable snapshots; the values themselves are
-- validated by the application that derives them, and the store's version check keeps TemplateVersion a
-- declared, non-negative version number. json_valid is only applied to a value the whole-identity trigger
-- has already established is present, and the explicit IS NULL arms keep the comparison non-null.
CREATE TRIGGER TR_WorkflowRuns_TemplateIdentityIsUsable
BEFORE INSERT ON WorkflowRuns
WHEN NEW.TemplateId IS NOT NULL
     AND (
            length(trim(NEW.TemplateId)) = 0
         OR NEW.TemplateVersion IS NULL
         OR NEW.TemplateVersion < 1
         OR NEW.TemplateGraphSnapshotJson IS NULL
         OR json_valid(NEW.TemplateGraphSnapshotJson) <> 1
         OR NEW.TemplateSchemeSnapshotJson IS NULL
         OR json_valid(NEW.TemplateSchemeSnapshotJson) <> 1
     )
BEGIN
    SELECT RAISE(ABORT, 'A pinned workflow run must name a template version and store two readable template snapshots.');
END;

-- The snapshots are pinned for the whole lifetime of the run, so an UPDATE that changed any of the four
-- columns is aborted by the database itself and not only by the repository omitting them from the UPSERT
-- SET list. `IS NOT` is the null-safe comparison in SQLite, so a legacy run is equally unable to acquire a
-- template identity after the fact. This is what makes "editing the template or moving the assignment
-- later must not change an existing run" a storage guarantee instead of a convention.
CREATE TRIGGER TR_WorkflowRuns_TemplateIdentityIsImmutable
BEFORE UPDATE ON WorkflowRuns
WHEN NEW.TemplateId IS NOT OLD.TemplateId
  OR NEW.TemplateVersion IS NOT OLD.TemplateVersion
  OR NEW.TemplateGraphSnapshotJson IS NOT OLD.TemplateGraphSnapshotJson
  OR NEW.TemplateSchemeSnapshotJson IS NOT OLD.TemplateSchemeSnapshotJson
BEGIN
    SELECT RAISE(ABORT, 'A workflow run keeps the template identity and both snapshots it was pinned to.');
END;
