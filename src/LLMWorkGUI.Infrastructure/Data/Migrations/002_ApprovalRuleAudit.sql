CREATE TABLE ApprovalRuleAudit (
    Sequence INTEGER NOT NULL PRIMARY KEY,
    RuleId TEXT NOT NULL,
    Action TEXT NOT NULL CHECK (Action IN ('Existing', 'Created', 'Updated', 'Deleted')),
    Backend TEXT NOT NULL,
    ProviderProfileId TEXT NOT NULL,
    ProjectId TEXT NOT NULL,
    PathScope TEXT NULL,
    Kind TEXT NOT NULL,
    Operation TEXT NOT NULL,
    ExpiresAtUtc TEXT NULL,
    CreatedBy TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    ChangedAtUtc TEXT NOT NULL
);

CREATE INDEX IX_ApprovalRuleAudit_RuleId_Sequence ON ApprovalRuleAudit (RuleId, Sequence);

-- Existing rules predate this audit. Preserve their current state without claiming
-- to know the original creation event or the history before this migration.
INSERT INTO ApprovalRuleAudit
    (RuleId, Action, Backend, ProviderProfileId, ProjectId, PathScope, Kind, Operation, ExpiresAtUtc, CreatedBy, CreatedAtUtc, ChangedAtUtc)
SELECT Id, 'Existing', Backend, ProviderProfileId, ProjectId, PathScope, Kind, Operation,
       ExpiresAtUtc, CreatedBy, CreatedAtUtc, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM ApprovalRules;

CREATE TRIGGER TR_ApprovalRuleAudit_NoUpdate BEFORE UPDATE ON ApprovalRuleAudit
BEGIN
    SELECT RAISE(ABORT, 'Approval rule audit entries are immutable.');
END;

CREATE TRIGGER TR_ApprovalRuleAudit_NoDelete BEFORE DELETE ON ApprovalRuleAudit
BEGIN
    SELECT RAISE(ABORT, 'Approval rule audit entries are immutable.');
END;

CREATE TRIGGER TR_ApprovalRules_AuditInsert AFTER INSERT ON ApprovalRules
BEGIN
    INSERT INTO ApprovalRuleAudit
        (RuleId, Action, Backend, ProviderProfileId, ProjectId, PathScope, Kind, Operation, ExpiresAtUtc, CreatedBy, CreatedAtUtc, ChangedAtUtc)
    VALUES
        (NEW.Id, 'Created', NEW.Backend, NEW.ProviderProfileId, NEW.ProjectId, NEW.PathScope, NEW.Kind, NEW.Operation, NEW.ExpiresAtUtc, NEW.CreatedBy, NEW.CreatedAtUtc, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
END;

CREATE TRIGGER TR_ApprovalRules_AuditUpdate AFTER UPDATE ON ApprovalRules
BEGIN
    INSERT INTO ApprovalRuleAudit
        (RuleId, Action, Backend, ProviderProfileId, ProjectId, PathScope, Kind, Operation, ExpiresAtUtc, CreatedBy, CreatedAtUtc, ChangedAtUtc)
    VALUES
        (NEW.Id, 'Updated', NEW.Backend, NEW.ProviderProfileId, NEW.ProjectId, NEW.PathScope, NEW.Kind, NEW.Operation, NEW.ExpiresAtUtc, NEW.CreatedBy, NEW.CreatedAtUtc, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
END;

CREATE TRIGGER TR_ApprovalRules_AuditDelete AFTER DELETE ON ApprovalRules
BEGIN
    INSERT INTO ApprovalRuleAudit
        (RuleId, Action, Backend, ProviderProfileId, ProjectId, PathScope, Kind, Operation, ExpiresAtUtc, CreatedBy, CreatedAtUtc, ChangedAtUtc)
    VALUES
        (OLD.Id, 'Deleted', OLD.Backend, OLD.ProviderProfileId, OLD.ProjectId, OLD.PathScope, OLD.Kind, OLD.Operation, OLD.ExpiresAtUtc, OLD.CreatedBy, OLD.CreatedAtUtc, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
END;
