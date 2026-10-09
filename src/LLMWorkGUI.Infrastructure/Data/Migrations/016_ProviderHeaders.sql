-- Extend the purpose constraint without changing existing references or their owners.
CREATE TABLE SecretReferencesV16 (
    Reference TEXT NOT NULL PRIMARY KEY CHECK (Reference GLOB 'urn:llmworkgui:secret:*'),
    Kind TEXT NOT NULL CHECK (Kind IN ('Unspecified','ProviderApiKey','GatewayApiKey','ProviderHeader')),
    State TEXT NOT NULL CHECK (State IN ('Active','Missing','Revoked')),
    CreatedAtUtc TEXT NOT NULL,
    LastRotatedAtUtc TEXT NULL,
    RevokedAtUtc TEXT NULL
);
INSERT INTO SecretReferencesV16 SELECT * FROM SecretReferences;
CREATE TABLE SecretReferenceOwnersV16 (
    Reference TEXT NOT NULL REFERENCES SecretReferencesV16 (Reference) ON DELETE CASCADE,
    OwnerKind TEXT NOT NULL CHECK (OwnerKind IN ('Unknown','ProviderProfile','Account')),
    OwnerId TEXT NOT NULL,
    PRIMARY KEY (Reference,OwnerKind,OwnerId)
);
INSERT INTO SecretReferenceOwnersV16 SELECT * FROM SecretReferenceOwners;
DROP TABLE SecretReferenceOwners;
DROP TABLE SecretReferences;
ALTER TABLE SecretReferencesV16 RENAME TO SecretReferences;
ALTER TABLE SecretReferenceOwnersV16 RENAME TO SecretReferenceOwners;
CREATE INDEX IX_SecretReferenceOwners_OwnerKind_OwnerId ON SecretReferenceOwners (OwnerKind,OwnerId);
ALTER TABLE ProviderProfiles ADD COLUMN CustomHeadersJson TEXT NULL;
ALTER TABLE ProviderProfiles ADD COLUMN Revision INTEGER NOT NULL DEFAULT 0;
