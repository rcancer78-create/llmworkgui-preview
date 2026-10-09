-- Payloads live outside SQLite. Revocation and deletion intent commit with the profile;
-- the primary supervisor drains this reference-only queue after commit and on startup.
-- Revision history intentionally survives deletion: an old editor cannot overwrite a
-- different incarnation of the same user-selected profile ID after it is recreated.
CREATE TABLE ProviderProfileRevisions (
    ProviderId TEXT NOT NULL PRIMARY KEY,
    LastRevision INTEGER NOT NULL CHECK (LastRevision >= 0)
);
INSERT INTO ProviderProfileRevisions SELECT Id, Revision FROM ProviderProfiles;
CREATE TRIGGER TR_ProviderProfiles_InsertRevisionIsNew
BEFORE INSERT ON ProviderProfiles
WHEN EXISTS (SELECT 1 FROM ProviderProfileRevisions WHERE ProviderId=NEW.Id AND LastRevision>=NEW.Revision)
     AND NOT EXISTS (SELECT 1 FROM ProviderProfiles WHERE Id=NEW.Id)
BEGIN
    SELECT RAISE(ABORT, 'A recreated provider must use a new revision.');
END;
CREATE TRIGGER TR_ProviderProfiles_RememberInsertedRevision
AFTER INSERT ON ProviderProfiles
BEGIN
    INSERT INTO ProviderProfileRevisions (ProviderId, LastRevision) VALUES (NEW.Id,NEW.Revision)
    ON CONFLICT(ProviderId) DO UPDATE SET LastRevision=MAX(LastRevision,excluded.LastRevision);
END;
CREATE TRIGGER TR_ProviderProfiles_RevisionCannotDecrease
BEFORE UPDATE OF Revision ON ProviderProfiles WHEN NEW.Revision<OLD.Revision
BEGIN
    SELECT RAISE(ABORT, 'Provider revision cannot decrease.');
END;
CREATE TRIGGER TR_ProviderProfiles_RememberUpdatedRevision
AFTER UPDATE OF Revision ON ProviderProfiles
BEGIN
    INSERT INTO ProviderProfileRevisions (ProviderId, LastRevision) VALUES (NEW.Id,NEW.Revision)
    ON CONFLICT(ProviderId) DO UPDATE SET LastRevision=MAX(LastRevision,excluded.LastRevision);
END;

CREATE TABLE PendingSecretDeletions (
    Reference TEXT NOT NULL PRIMARY KEY REFERENCES SecretReferences(Reference) ON DELETE CASCADE,
    RequestedAtUtc TEXT NOT NULL
);

CREATE TRIGGER TR_SecretReferences_PendingDeletionStaysRevoked
BEFORE UPDATE OF State ON SecretReferences
WHEN NEW.State != 'Revoked' AND EXISTS (SELECT 1 FROM PendingSecretDeletions WHERE Reference=OLD.Reference)
BEGIN
    SELECT RAISE(ABORT, 'A secret awaiting payload deletion cannot be reactivated.');
END;

-- Historical metadata can omit owners. Backfill only existing registered bindings;
-- this never invents a purpose or changes availability/authorization.
INSERT OR IGNORE INTO SecretReferenceOwners (Reference, OwnerKind, OwnerId)
SELECT p.ApiKeySecretReference, 'ProviderProfile', p.Id
FROM ProviderProfiles p JOIN SecretReferences s ON s.Reference=p.ApiKeySecretReference;
INSERT OR IGNORE INTO SecretReferenceOwners (Reference, OwnerKind, OwnerId)
SELECT a.SecretReference, 'Account', a.Id
FROM Accounts a JOIN SecretReferences s ON s.Reference=a.SecretReference;
-- A replacement commits the new binding and durable retirement of any unshared old
-- credential together. Startup can finish payload cleanup after a crash at that boundary.
CREATE TRIGGER TR_ProviderProfiles_QueueSupersededSecrets
AFTER UPDATE OF ApiKeySecretReference, CustomHeadersJson ON ProviderProfiles
BEGIN
    INSERT INTO PendingSecretDeletions (Reference, RequestedAtUtc)
    SELECT DISTINCT s.Reference, strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM SecretReferences s
    JOIN (
        SELECT OLD.ApiKeySecretReference AS Reference, 'ProviderApiKey' AS Kind
        UNION ALL SELECT json_extract(h.value,'$.SecretReference'), 'ProviderHeader'
            FROM json_each(COALESCE(OLD.CustomHeadersJson,'[]')) h
    ) old ON old.Reference=s.Reference AND old.Kind=s.Kind
    WHERE NOT EXISTS (SELECT 1 FROM PendingSecretDeletions WHERE Reference=s.Reference)
      AND NOT EXISTS (SELECT 1 FROM ProviderProfiles WHERE ApiKeySecretReference=s.Reference)
      AND NOT EXISTS (SELECT 1 FROM Accounts WHERE SecretReference=s.Reference)
      AND NOT EXISTS (SELECT 1 FROM ProviderProfiles p, json_each(COALESCE(p.CustomHeadersJson,'[]')) h
                      WHERE json_extract(h.value,'$.SecretReference')=s.Reference)
      AND NOT EXISTS (SELECT 1 FROM SecretReferenceOwners WHERE Reference=s.Reference AND OwnerKind='Unknown');
    UPDATE SecretReferences SET State='Revoked', RevokedAtUtc=COALESCE(RevokedAtUtc,strftime('%Y-%m-%dT%H:%M:%fZ','now'))
    WHERE State<>'Revoked' AND Reference IN (SELECT Reference FROM PendingSecretDeletions);
END;

CREATE TRIGGER TR_Accounts_QueueSupersededSecret
AFTER UPDATE OF SecretReference ON Accounts
WHEN OLD.SecretReference IS NOT NEW.SecretReference
BEGIN
    INSERT INTO PendingSecretDeletions (Reference, RequestedAtUtc)
    SELECT s.Reference, strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM SecretReferences s
    WHERE s.Reference=OLD.SecretReference AND s.Kind='ProviderApiKey'
      AND NOT EXISTS (SELECT 1 FROM PendingSecretDeletions WHERE Reference=s.Reference)
      AND NOT EXISTS (SELECT 1 FROM ProviderProfiles WHERE ApiKeySecretReference=s.Reference)
      AND NOT EXISTS (SELECT 1 FROM Accounts WHERE SecretReference=s.Reference)
      AND NOT EXISTS (SELECT 1 FROM ProviderProfiles p, json_each(COALESCE(p.CustomHeadersJson,'[]')) h
                      WHERE json_extract(h.value,'$.SecretReference')=s.Reference)
      AND NOT EXISTS (SELECT 1 FROM SecretReferenceOwners WHERE Reference=s.Reference AND OwnerKind='Unknown');
    UPDATE SecretReferences SET State='Revoked', RevokedAtUtc=COALESCE(RevokedAtUtc,strftime('%Y-%m-%dT%H:%M:%fZ','now'))
    WHERE State<>'Revoked' AND Reference IN (SELECT Reference FROM PendingSecretDeletions);
END;
-- Explicit revocation and compensation of an uncommitted replacement also need recovery
-- if the external payload delete fails. These intents are recorded with the metadata change.
CREATE TRIGGER TR_SecretReferences_QueueRevokedPayload
AFTER UPDATE OF State ON SecretReferences WHEN NEW.State='Revoked'
BEGIN
    INSERT INTO PendingSecretDeletions (Reference, RequestedAtUtc)
    SELECT NEW.Reference,strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE NOT EXISTS (SELECT 1 FROM PendingSecretDeletions WHERE Reference=NEW.Reference);
END;

CREATE TRIGGER TR_SecretReferences_QueueInsertedRevokedPayload
AFTER INSERT ON SecretReferences WHEN NEW.State='Revoked'
BEGIN
    INSERT INTO PendingSecretDeletions (Reference, RequestedAtUtc)
    SELECT NEW.Reference,strftime('%Y-%m-%dT%H:%M:%fZ','now')
    WHERE NOT EXISTS (SELECT 1 FROM PendingSecretDeletions WHERE Reference=NEW.Reference);
END;
