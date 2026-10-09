-- Durable, immutable workflow template versions and the per-project assignment pointer (Phase 10E).
-- A template version is the Studio's own identity: it is not an imported package, not an imported
-- version blob and not a workflow run, so this migration adds no row to WorkflowPackages,
-- WorkflowVersions or WorkflowRuns and rewrites none of the rows migration 001 created.
--
-- The composite primary key is the whole immutability contract: a (TemplateId, Version) pair can be
-- written exactly once, so saving a version that already exists is refused instead of overwriting the
-- graph an earlier save - and possibly an active run - already depends on.
CREATE TABLE WorkflowTemplateVersions (
    TemplateId TEXT NOT NULL,
    Version INTEGER NOT NULL CHECK (Version >= 1),
    DisplayName TEXT NOT NULL,
    Description TEXT NOT NULL,
    IsBuiltIn INTEGER NOT NULL CHECK (IsBuiltIn IN (0, 1)),
    GraphJson TEXT NOT NULL,
    RoleBindingsJson TEXT NOT NULL,
    RequiredDocumentTemplatesJson TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (TemplateId, Version)
);

-- The stored graph must be a graph, not an empty document, so a row that lost its entry node or all
-- of its nodes is rejected by the database and not only by the store. The CASE expression is
-- evaluated left to right and stops at the first true branch, so the path expressions below are only
-- ever applied to a document json_valid already accepted.
CREATE TRIGGER TR_WorkflowTemplateVersions_GraphIsUsable
BEFORE INSERT ON WorkflowTemplateVersions
WHEN CASE
    WHEN json_valid(NEW.GraphJson) <> 1 THEN 1
    WHEN json_type(NEW.GraphJson) IS NOT 'object' THEN 1
    WHEN json_type(NEW.GraphJson, '$.entryNodeId') IS NOT 'text' THEN 1
    WHEN length(trim(json_extract(NEW.GraphJson, '$.entryNodeId'))) = 0 THEN 1
    WHEN json_array_length(NEW.GraphJson, '$.nodes') IS NULL THEN 1
    WHEN json_array_length(NEW.GraphJson, '$.nodes') = 0 THEN 1
    ELSE 0
END = 1
BEGIN
    SELECT RAISE(ABORT, 'A workflow template version must store a graph object with an entry node and at least one node.');
END;

-- Immutability is enforced by the database, not only by the store: an UPDATE or DELETE that reached a
-- saved version through any other code path is aborted. Editing a template always means writing a new
-- version, exactly like 002 does for the approval-rule audit.
CREATE TRIGGER TR_WorkflowTemplateVersions_NoUpdate BEFORE UPDATE ON WorkflowTemplateVersions
BEGIN
    SELECT RAISE(ABORT, 'Workflow template versions are immutable.');
END;

CREATE TRIGGER TR_WorkflowTemplateVersions_NoDelete BEFORE DELETE ON WorkflowTemplateVersions
BEGIN
    SELECT RAISE(ABORT, 'Workflow template versions are immutable.');
END;

-- One pointer per project: a project has at most one assigned template version, and two projects never
-- share a pointer. ProjectId is the primary key rather than a foreign key to Projects, because this
-- slice must not fabricate a project row in order to store a pointer: the template store owns its own
-- registry and the Studio assigns a template independently of the project registry.
CREATE TABLE WorkflowTemplateAssignments (
    ProjectId TEXT NOT NULL PRIMARY KEY,
    AssignmentId TEXT NOT NULL,
    TemplateId TEXT NOT NULL,
    TemplateVersion INTEGER NOT NULL CHECK (TemplateVersion >= 1),
    AssignedAtUtc TEXT NOT NULL,
    UNIQUE (AssignmentId),
    FOREIGN KEY (TemplateId, TemplateVersion)
        REFERENCES WorkflowTemplateVersions (TemplateId, Version)
        ON UPDATE RESTRICT
        ON DELETE RESTRICT
);

-- The composite foreign key is the guarantee behind "an assignment may only name an existing
-- version": an insert, or a move to a version that was never saved, fails with a constraint violation
-- instead of leaving a dangling pointer. The pointer row itself stays mutable, because moving a
-- project to another existing version is the one assignment operation the Studio is allowed to do.
CREATE INDEX IX_WorkflowTemplateAssignments_TemplateId_TemplateVersion
    ON WorkflowTemplateAssignments (TemplateId, TemplateVersion);
