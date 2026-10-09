-- Capability declarations are scoped to an account/model and its authentication context.
-- Invalidate in the same statement/transaction as the context change, including repository,
-- lifecycle, importer and direct SQL paths. A failed statement rolls back the invalidation too.
-- CASE protects identity extraction from malformed legacy/local payloads.
-- v1 payloads serialize CapabilityState numerically: Stale=3. Preserve declarations for review,
-- but require a fresh observation or explicit user reconfirmation after context changes.
-- An older schema had no context invalidation, so its surviving declarations start stale.
UPDATE ModelCapabilities SET State='Stale',
    CapabilityValue=CASE WHEN json_valid(CapabilityValue) THEN json_set(CapabilityValue,'$.State',3) ELSE CapabilityValue END
WHERE CapabilityKey LIKE 'llmworkgui.evidence.v1/%';

CREATE TRIGGER TR_ModelCapabilities_AccountContext
AFTER UPDATE OF ProviderProfileId,ProviderNativeId,GatewayNativeId,SecretReference,AuthState ON Accounts
WHEN OLD.ProviderProfileId IS NOT NEW.ProviderProfileId
  OR OLD.ProviderNativeId IS NOT NEW.ProviderNativeId
  OR OLD.GatewayNativeId IS NOT NEW.GatewayNativeId
  OR OLD.SecretReference IS NOT NEW.SecretReference
  OR OLD.AuthState IS NOT NEW.AuthState
BEGIN
    UPDATE ModelCapabilities SET State='Stale', CapabilityValue=CASE WHEN json_valid(CapabilityValue) THEN json_set(CapabilityValue,'$.State',3) ELSE CapabilityValue END
    WHERE CapabilityKey LIKE 'llmworkgui.evidence.v1/%'
      AND CASE WHEN json_valid(CapabilityValue) THEN json_extract(CapabilityValue,'$.AccountId') END = OLD.Id;
END;

CREATE TRIGGER TR_ModelCapabilities_AccountDeleted
BEFORE DELETE ON Accounts
BEGIN
    DELETE FROM ModelCapabilities
    WHERE CapabilityKey LIKE 'llmworkgui.evidence.v1/%'
      AND CASE WHEN json_valid(CapabilityValue) THEN json_extract(CapabilityValue,'$.AccountId') END = OLD.Id;
END;

CREATE TRIGGER TR_ModelCapabilities_ModelIdentity
AFTER UPDATE OF Backend,ProviderProfileId,ProviderModelId,GatewayNativeId ON Models
WHEN OLD.Backend IS NOT NEW.Backend OR OLD.ProviderProfileId IS NOT NEW.ProviderProfileId
  OR OLD.ProviderModelId IS NOT NEW.ProviderModelId
  OR OLD.GatewayNativeId IS NOT NEW.GatewayNativeId
BEGIN
    UPDATE ModelCapabilities SET State='Stale', CapabilityValue=CASE WHEN json_valid(CapabilityValue) THEN json_set(CapabilityValue,'$.State',3) ELSE CapabilityValue END WHERE ModelId=OLD.Id;
END;

CREATE TRIGGER TR_ModelCapabilities_ProviderContext
AFTER UPDATE OF Backend,BaseUrl,ExecutablePath,ApiKeySecretReference,GatewayNativeId,CustomHeadersJson ON ProviderProfiles
WHEN OLD.Backend IS NOT NEW.Backend OR OLD.BaseUrl IS NOT NEW.BaseUrl
  OR OLD.ExecutablePath IS NOT NEW.ExecutablePath OR OLD.ApiKeySecretReference IS NOT NEW.ApiKeySecretReference
  OR OLD.GatewayNativeId IS NOT NEW.GatewayNativeId OR OLD.CustomHeadersJson IS NOT NEW.CustomHeadersJson
BEGIN
    UPDATE ModelCapabilities SET State='Stale', CapabilityValue=CASE WHEN json_valid(CapabilityValue) THEN json_set(CapabilityValue,'$.State',3) ELSE CapabilityValue END WHERE ModelId IN (SELECT Id FROM Models WHERE ProviderProfileId=OLD.Id);
END;

CREATE TRIGGER TR_ModelCapabilities_SecretChanged
AFTER UPDATE OF State,LastRotatedAtUtc,RevokedAtUtc,Kind ON SecretReferences
WHEN OLD.State IS NOT NEW.State OR OLD.LastRotatedAtUtc IS NOT NEW.LastRotatedAtUtc
  OR OLD.RevokedAtUtc IS NOT NEW.RevokedAtUtc OR OLD.Kind IS NOT NEW.Kind
BEGIN
    UPDATE ModelCapabilities SET State='Stale', CapabilityValue=CASE WHEN json_valid(CapabilityValue) THEN json_set(CapabilityValue,'$.State',3) ELSE CapabilityValue END
    WHERE ModelId IN (
        SELECT m.Id FROM Models m JOIN ProviderProfiles p ON p.Id=m.ProviderProfileId
        WHERE p.ApiKeySecretReference=OLD.Reference
           OR p.Id IN (SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='ProviderProfile'))
    OR (CapabilityKey LIKE 'llmworkgui.evidence.v1/%'
        AND CASE WHEN json_valid(CapabilityValue) THEN json_extract(CapabilityValue,'$.AccountId') END IN (
            SELECT Id FROM Accounts WHERE SecretReference=OLD.Reference
            UNION SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='Account'));
END;

-- BEFORE DELETE still sees the owners that ON DELETE CASCADE will subsequently remove.
CREATE TRIGGER TR_ModelCapabilities_SecretDeleted
BEFORE DELETE ON SecretReferences
BEGIN
    UPDATE ModelCapabilities SET State='Stale', CapabilityValue=CASE WHEN json_valid(CapabilityValue) THEN json_set(CapabilityValue,'$.State',3) ELSE CapabilityValue END
    WHERE ModelId IN (
        SELECT m.Id FROM Models m JOIN ProviderProfiles p ON p.Id=m.ProviderProfileId
        WHERE p.ApiKeySecretReference=OLD.Reference
           OR p.Id IN (SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='ProviderProfile'))
    OR (CapabilityKey LIKE 'llmworkgui.evidence.v1/%'
        AND CASE WHEN json_valid(CapabilityValue) THEN json_extract(CapabilityValue,'$.AccountId') END IN (
            SELECT Id FROM Accounts WHERE SecretReference=OLD.Reference
            UNION SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='Account'));
END;

-- A previously absent reference becoming present changes the credential context too.
CREATE TRIGGER TR_ModelCapabilities_SecretCreated
AFTER INSERT ON SecretReferences
BEGIN
    UPDATE ModelCapabilities SET State='Stale', CapabilityValue=CASE WHEN json_valid(CapabilityValue) THEN json_set(CapabilityValue,'$.State',3) ELSE CapabilityValue END
    WHERE ModelId IN (SELECT Id FROM Models WHERE ProviderProfileId IN (
        SELECT Id FROM ProviderProfiles WHERE ApiKeySecretReference=NEW.Reference))
    OR (CapabilityKey LIKE 'llmworkgui.evidence.v1/%'
        AND CASE WHEN json_valid(CapabilityValue) THEN json_extract(CapabilityValue,'$.AccountId') END IN (
            SELECT Id FROM Accounts WHERE SecretReference=NEW.Reference));
END;

-- Rotating bytes twice at the same clock instant is still a rotation. MarkRotatedAsync writes
-- this column explicitly; timestamp equality must not retain the first rotation's capabilities.
CREATE TRIGGER TR_ModelCapabilities_SecretRotationSameTime
AFTER UPDATE OF LastRotatedAtUtc ON SecretReferences
WHEN OLD.LastRotatedAtUtc IS NEW.LastRotatedAtUtc
BEGIN
    UPDATE ModelCapabilities SET State='Stale', CapabilityValue=CASE WHEN json_valid(CapabilityValue) THEN json_set(CapabilityValue,'$.State',3) ELSE CapabilityValue END
    WHERE ModelId IN (
        SELECT m.Id FROM Models m JOIN ProviderProfiles p ON p.Id=m.ProviderProfileId
        WHERE p.ApiKeySecretReference=OLD.Reference
           OR p.Id IN (SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='ProviderProfile'))
    OR (CapabilityKey LIKE 'llmworkgui.evidence.v1/%'
        AND CASE WHEN json_valid(CapabilityValue) THEN json_extract(CapabilityValue,'$.AccountId') END IN (
            SELECT Id FROM Accounts WHERE SecretReference=OLD.Reference
            UNION SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='Account'));
END;

-- Ownership also represents credentials used by custom provider headers.
CREATE TRIGGER TR_ModelCapabilities_SecretOwnerAdded
AFTER INSERT ON SecretReferenceOwners
BEGIN
    UPDATE ModelCapabilities SET State='Stale', CapabilityValue=CASE WHEN json_valid(CapabilityValue) THEN json_set(CapabilityValue,'$.State',3) ELSE CapabilityValue END
    WHERE (NEW.OwnerKind='ProviderProfile' AND ModelId IN (SELECT Id FROM Models WHERE ProviderProfileId=NEW.OwnerId))
       OR (NEW.OwnerKind='Account' AND CapabilityKey LIKE 'llmworkgui.evidence.v1/%'
           AND CASE WHEN json_valid(CapabilityValue) THEN json_extract(CapabilityValue,'$.AccountId') END=NEW.OwnerId);
END;

CREATE TRIGGER TR_ModelCapabilities_SecretOwnerRemoved
BEFORE DELETE ON SecretReferenceOwners
BEGIN
    UPDATE ModelCapabilities SET State='Stale', CapabilityValue=CASE WHEN json_valid(CapabilityValue) THEN json_set(CapabilityValue,'$.State',3) ELSE CapabilityValue END
    WHERE (OLD.OwnerKind='ProviderProfile' AND ModelId IN (SELECT Id FROM Models WHERE ProviderProfileId=OLD.OwnerId))
       OR (OLD.OwnerKind='Account' AND CapabilityKey LIKE 'llmworkgui.evidence.v1/%'
           AND CASE WHEN json_valid(CapabilityValue) THEN json_extract(CapabilityValue,'$.AccountId') END=OLD.OwnerId);
END;

CREATE TRIGGER TR_ModelCapabilities_SecretOwnerChanged
AFTER UPDATE OF Reference,OwnerKind,OwnerId ON SecretReferenceOwners
WHEN OLD.Reference IS NOT NEW.Reference OR OLD.OwnerKind IS NOT NEW.OwnerKind OR OLD.OwnerId IS NOT NEW.OwnerId
BEGIN
    UPDATE ModelCapabilities SET State='Stale', CapabilityValue=CASE WHEN json_valid(CapabilityValue) THEN json_set(CapabilityValue,'$.State',3) ELSE CapabilityValue END
    WHERE (OLD.OwnerKind='ProviderProfile' AND ModelId IN (SELECT Id FROM Models WHERE ProviderProfileId=OLD.OwnerId))
       OR (NEW.OwnerKind='ProviderProfile' AND ModelId IN (SELECT Id FROM Models WHERE ProviderProfileId=NEW.OwnerId))
       OR (CapabilityKey LIKE 'llmworkgui.evidence.v1/%'
           AND CASE WHEN json_valid(CapabilityValue) THEN json_extract(CapabilityValue,'$.AccountId') END IN (
               SELECT OLD.OwnerId WHERE OLD.OwnerKind='Account'
               UNION SELECT NEW.OwnerId WHERE NEW.OwnerKind='Account'));
END;
