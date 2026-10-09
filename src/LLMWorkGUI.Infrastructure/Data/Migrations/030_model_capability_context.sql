-- Revisions exist even before the first declaration, and change on every identity/auth event.
-- Random per-row tokens prevent ABA and same-ID recreation from reviving an old observation.
-- They are local concurrency tokens, not proof of credentials or response origin.
ALTER TABLE Accounts ADD COLUMN CapabilityRevision TEXT NOT NULL DEFAULT '';
ALTER TABLE Models ADD COLUMN CapabilityRevision TEXT NOT NULL DEFAULT '';
UPDATE Accounts SET CapabilityRevision=lower(hex(randomblob(16)));
UPDATE Models SET CapabilityRevision=lower(hex(randomblob(16)));

CREATE TRIGGER TR_CapabilityRevision_AccountCreated AFTER INSERT ON Accounts
BEGIN
    UPDATE Accounts SET CapabilityRevision=lower(hex(randomblob(16))) WHERE Id=NEW.Id;
END;

CREATE TRIGGER TR_CapabilityRevision_ModelCreated AFTER INSERT ON Models
BEGIN
    UPDATE Models SET CapabilityRevision=lower(hex(randomblob(16))) WHERE Id=NEW.Id;
END;

CREATE TRIGGER TR_CapabilityRevision_AccountContext
AFTER UPDATE OF ProviderProfileId,ProviderNativeId,GatewayNativeId,SecretReference,AuthState ON Accounts
WHEN OLD.ProviderProfileId IS NOT NEW.ProviderProfileId
  OR OLD.ProviderNativeId IS NOT NEW.ProviderNativeId
  OR OLD.GatewayNativeId IS NOT NEW.GatewayNativeId
  OR OLD.SecretReference IS NOT NEW.SecretReference
  OR OLD.AuthState IS NOT NEW.AuthState
BEGIN
    UPDATE Accounts SET CapabilityRevision=lower(hex(randomblob(16)))
    WHERE Id=OLD.Id;
END;

CREATE TRIGGER TR_CapabilityRevision_ModelIdentity
AFTER UPDATE OF Backend,ProviderProfileId,ProviderModelId,GatewayNativeId ON Models
WHEN OLD.Backend IS NOT NEW.Backend OR OLD.ProviderProfileId IS NOT NEW.ProviderProfileId
  OR OLD.ProviderModelId IS NOT NEW.ProviderModelId
  OR OLD.GatewayNativeId IS NOT NEW.GatewayNativeId
BEGIN
    UPDATE Models SET CapabilityRevision=lower(hex(randomblob(16)))
    WHERE Id=OLD.Id;
END;

CREATE TRIGGER TR_CapabilityRevision_ProviderContext
AFTER UPDATE OF Backend,BaseUrl,ExecutablePath,ApiKeySecretReference,GatewayNativeId,CustomHeadersJson ON ProviderProfiles
WHEN OLD.Backend IS NOT NEW.Backend OR OLD.BaseUrl IS NOT NEW.BaseUrl
  OR OLD.ExecutablePath IS NOT NEW.ExecutablePath OR OLD.ApiKeySecretReference IS NOT NEW.ApiKeySecretReference
  OR OLD.GatewayNativeId IS NOT NEW.GatewayNativeId OR OLD.CustomHeadersJson IS NOT NEW.CustomHeadersJson
BEGIN
    UPDATE Accounts SET CapabilityRevision=lower(hex(randomblob(16)))
    WHERE ProviderProfileId=OLD.Id;
END;

CREATE TRIGGER TR_CapabilityRevision_SecretChanged
AFTER UPDATE OF State,LastRotatedAtUtc,RevokedAtUtc,Kind ON SecretReferences
WHEN OLD.State IS NOT NEW.State OR OLD.LastRotatedAtUtc IS NOT NEW.LastRotatedAtUtc
  OR OLD.RevokedAtUtc IS NOT NEW.RevokedAtUtc OR OLD.Kind IS NOT NEW.Kind
BEGIN
    UPDATE Accounts SET CapabilityRevision=lower(hex(randomblob(16)))
    WHERE Id IN (SELECT a.Id FROM Accounts a JOIN ProviderProfiles p ON p.Id=a.ProviderProfileId
        WHERE a.SecretReference=OLD.Reference OR p.ApiKeySecretReference=OLD.Reference
           OR p.Id IN (SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='ProviderProfile')
           OR a.Id IN (SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='Account'));
END;

CREATE TRIGGER TR_CapabilityRevision_SecretDeleted
BEFORE DELETE ON SecretReferences
BEGIN
    UPDATE Accounts SET CapabilityRevision=lower(hex(randomblob(16)))
    WHERE Id IN (SELECT a.Id FROM Accounts a JOIN ProviderProfiles p ON p.Id=a.ProviderProfileId
        WHERE a.SecretReference=OLD.Reference OR p.ApiKeySecretReference=OLD.Reference
           OR p.Id IN (SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='ProviderProfile')
           OR a.Id IN (SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='Account'));
END;

CREATE TRIGGER TR_CapabilityRevision_SecretCreated
AFTER INSERT ON SecretReferences
BEGIN
    UPDATE Accounts SET CapabilityRevision=lower(hex(randomblob(16)))
    WHERE Id IN (SELECT a.Id FROM Accounts a JOIN ProviderProfiles p ON p.Id=a.ProviderProfileId
        WHERE a.SecretReference=NEW.Reference OR p.ApiKeySecretReference=NEW.Reference
           OR p.Id IN (SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=NEW.Reference AND OwnerKind='ProviderProfile')
           OR a.Id IN (SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=NEW.Reference AND OwnerKind='Account'));
END;

CREATE TRIGGER TR_CapabilityRevision_SecretRotationSameTime
AFTER UPDATE OF LastRotatedAtUtc ON SecretReferences
WHEN OLD.LastRotatedAtUtc IS NEW.LastRotatedAtUtc
BEGIN
    UPDATE Accounts SET CapabilityRevision=lower(hex(randomblob(16)))
    WHERE Id IN (SELECT a.Id FROM Accounts a JOIN ProviderProfiles p ON p.Id=a.ProviderProfileId
        WHERE a.SecretReference=OLD.Reference OR p.ApiKeySecretReference=OLD.Reference
           OR p.Id IN (SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='ProviderProfile')
           OR a.Id IN (SELECT OwnerId FROM SecretReferenceOwners WHERE Reference=OLD.Reference AND OwnerKind='Account'));
END;

CREATE TRIGGER TR_CapabilityRevision_SecretOwnerAdded
AFTER INSERT ON SecretReferenceOwners
BEGIN
    UPDATE Accounts SET CapabilityRevision=lower(hex(randomblob(16)))
    WHERE (NEW.OwnerKind='ProviderProfile' AND ProviderProfileId=NEW.OwnerId) OR (NEW.OwnerKind='Account' AND Id=NEW.OwnerId);
END;

CREATE TRIGGER TR_CapabilityRevision_SecretOwnerRemoved
BEFORE DELETE ON SecretReferenceOwners
BEGIN
    UPDATE Accounts SET CapabilityRevision=lower(hex(randomblob(16)))
    WHERE (OLD.OwnerKind='ProviderProfile' AND ProviderProfileId=OLD.OwnerId) OR (OLD.OwnerKind='Account' AND Id=OLD.OwnerId);
END;

CREATE TRIGGER TR_CapabilityRevision_SecretOwnerChanged
AFTER UPDATE OF Reference,OwnerKind,OwnerId ON SecretReferenceOwners
WHEN OLD.Reference IS NOT NEW.Reference OR OLD.OwnerKind IS NOT NEW.OwnerKind OR OLD.OwnerId IS NOT NEW.OwnerId
BEGIN
    UPDATE Accounts SET CapabilityRevision=lower(hex(randomblob(16)))
    WHERE (OLD.OwnerKind='ProviderProfile' AND ProviderProfileId=OLD.OwnerId) OR (OLD.OwnerKind='Account' AND Id=OLD.OwnerId) OR (NEW.OwnerKind='ProviderProfile' AND ProviderProfileId=NEW.OwnerId) OR (NEW.OwnerKind='Account' AND Id=NEW.OwnerId);
END;
