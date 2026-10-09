CREATE TABLE WorkflowMaterialPolicies (
    VersionId TEXT PRIMARY KEY REFERENCES WorkflowVersions(Id),
    BlobId TEXT NOT NULL,
    Classification TEXT NOT NULL CHECK(Classification IN ('PublicSource','PrivateSource','Restricted')),
    Revision INTEGER NOT NULL CHECK(Revision > 0),
    DeclaredBySha256 TEXT NOT NULL CHECK(length(DeclaredBySha256)=64 AND DeclaredBySha256 NOT GLOB '*[^0-9A-F]*'),
    DeclaredAtUtc TEXT NOT NULL
);
CREATE TRIGGER TR_WorkflowMaterialPolicies_InsertBinding BEFORE INSERT ON WorkflowMaterialPolicies
WHEN NOT EXISTS (SELECT 1 FROM WorkflowVersions v WHERE v.Id=NEW.VersionId AND v.BlobId=NEW.BlobId)
BEGIN SELECT RAISE(ABORT, 'Material policy must name the stored immutable version bytes.'); END;
CREATE TRIGGER TR_WorkflowMaterialPolicies_UpdateBinding BEFORE UPDATE ON WorkflowMaterialPolicies
WHEN NEW.VersionId IS NOT OLD.VersionId OR NEW.BlobId IS NOT OLD.BlobId OR NEW.Revision != OLD.Revision+1
BEGIN SELECT RAISE(ABORT, 'Material policy keeps its immutable version/blob binding and advances one revision.'); END;
CREATE TRIGGER TR_WorkflowMaterialPolicies_NoDelete BEFORE DELETE ON WorkflowMaterialPolicies
BEGIN SELECT RAISE(ABORT, 'Material policy history is retained.'); END;

CREATE TABLE WorkflowMaterialPolicyChanges (
    Id TEXT PRIMARY KEY,
    VersionId TEXT NOT NULL REFERENCES WorkflowVersions(Id),
    BlobId TEXT NOT NULL,
    Revision INTEGER NOT NULL CHECK(Revision>0),
    PreviousClassification TEXT NULL CHECK(PreviousClassification IS NULL OR PreviousClassification IN ('PublicSource','PrivateSource','Restricted')),
    Classification TEXT NOT NULL CHECK(Classification IN ('PublicSource','PrivateSource','Restricted')),
    ActorSha256 TEXT NOT NULL CHECK(length(ActorSha256)=64 AND ActorSha256 NOT GLOB '*[^0-9A-F]*'),
    OccurredAtUtc TEXT NOT NULL,
    UNIQUE(VersionId,Revision)
);
CREATE TRIGGER TR_WorkflowMaterialPolicyChanges_Binding BEFORE INSERT ON WorkflowMaterialPolicyChanges
WHEN NOT EXISTS (SELECT 1 FROM WorkflowMaterialPolicies p WHERE p.VersionId=NEW.VersionId AND p.BlobId=NEW.BlobId
    AND p.Revision=NEW.Revision AND p.Classification=NEW.Classification AND p.DeclaredBySha256=NEW.ActorSha256)
BEGIN SELECT RAISE(ABORT, 'Material audit matches the current stored declaration.'); END;
CREATE TRIGGER TR_WorkflowMaterialPolicyChanges_NoUpdate BEFORE UPDATE ON WorkflowMaterialPolicyChanges
BEGIN SELECT RAISE(ABORT, 'Material classification audit is immutable.'); END;
CREATE TRIGGER TR_WorkflowMaterialPolicyChanges_NoDelete BEFORE DELETE ON WorkflowMaterialPolicyChanges
BEGIN SELECT RAISE(ABORT, 'Material classification audit is immutable.'); END;

-- This is a local preflight audit, not an execution journal or evidence of HTTP/native delivery.
CREATE TABLE WorkflowAdaptationPromptAdmissions (
    Id TEXT PRIMARY KEY,
    ProjectId TEXT NOT NULL REFERENCES Projects(Id),
    VersionId TEXT NOT NULL REFERENCES WorkflowVersions(Id),
    RouteId TEXT NOT NULL REFERENCES Routes(Id),
    PromptSha256 TEXT NOT NULL,
    PolicySha256 TEXT NOT NULL,
    PrimaryInstanceId TEXT NOT NULL,
    State TEXT NOT NULL CHECK(State IN ('Prepared','CreatePreflight','PromptPreflight')),
    ExpiresAtUtc TEXT NOT NULL
);
CREATE TABLE WorkflowAdaptationPolicyChecks (
    Id TEXT PRIMARY KEY,
    ProjectId TEXT NOT NULL REFERENCES Projects(Id),
    VersionId TEXT NOT NULL REFERENCES WorkflowVersions(Id),
    RouteId TEXT NOT NULL REFERENCES Routes(Id),
    SourceBlobId TEXT NOT NULL,
    MaterialPolicyRevision INTEGER NOT NULL,
    Classification TEXT NOT NULL,
    PromptSha256 TEXT NOT NULL,
    PolicySha256 TEXT NOT NULL,
    PrimaryInstanceId TEXT NOT NULL,
    OccurredAtUtc TEXT NOT NULL
);
CREATE TRIGGER TR_WorkflowAdaptationPolicyChecks_NoUpdate BEFORE UPDATE ON WorkflowAdaptationPolicyChecks
BEGIN SELECT RAISE(ABORT, 'Adaptation preflight audit is immutable.'); END;
CREATE TRIGGER TR_WorkflowAdaptationPolicyChecks_NoDelete BEFORE DELETE ON WorkflowAdaptationPolicyChecks
BEGIN SELECT RAISE(ABORT, 'Adaptation preflight audit is immutable.'); END;
