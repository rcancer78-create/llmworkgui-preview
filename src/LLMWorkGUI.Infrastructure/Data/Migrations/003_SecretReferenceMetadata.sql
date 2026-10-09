-- Reference-only metadata for every secret URN the application stores (ADR-0005 §2.1, §5).
-- The secret value is never a column: only the URN, its state and the owners that may use it are,
-- so a database dump, backup or diagnostic export stays safe by construction.
CREATE TABLE SecretReferences (
    Reference TEXT NOT NULL PRIMARY KEY CHECK (Reference GLOB 'urn:llmworkgui:secret:*'),
    Kind TEXT NOT NULL CHECK (Kind IN ('Unspecified', 'ProviderApiKey')),
    State TEXT NOT NULL CHECK (State IN ('Active', 'Missing', 'Revoked')),
    CreatedAtUtc TEXT NOT NULL,
    LastRotatedAtUtc TEXT NULL,
    RevokedAtUtc TEXT NULL
);

-- A URN may be shared by a provider profile and by an account, so ownership is a separate
-- one-to-many relation and no single-owner invariant is imposed on the reference row.
CREATE TABLE SecretReferenceOwners (
    Reference TEXT NOT NULL REFERENCES SecretReferences (Reference) ON DELETE CASCADE,
    OwnerKind TEXT NOT NULL CHECK (OwnerKind IN ('Unknown', 'ProviderProfile', 'Account')),
    OwnerId TEXT NOT NULL,
    PRIMARY KEY (Reference, OwnerKind, OwnerId)
);

CREATE INDEX IX_SecretReferenceOwners_OwnerKind_OwnerId ON SecretReferenceOwners (OwnerKind, OwnerId);

-- Staging area for the references that already exist and satisfy the canonical URN contract. The
-- contract has to be evaluated in SQL because the migration must not abort on a legacy value the
-- application would reject: such a row simply receives no metadata and is reported as Missing when
-- it is resolved.
CREATE TEMP TABLE IF NOT EXISTS Migration003CanonicalReferences (
    Reference TEXT NOT NULL PRIMARY KEY
);

INSERT INTO Migration003CanonicalReferences (Reference)
SELECT Reference
FROM (
    WITH RECURSIVE
    referenced(Reference) AS (
        SELECT ApiKeySecretReference FROM ProviderProfiles WHERE ApiKeySecretReference IS NOT NULL
        UNION
        SELECT SecretReference FROM Accounts WHERE SecretReference IS NOT NULL
    ),
    prefixed(Reference) AS (
        SELECT Reference
        FROM referenced
        WHERE substr(Reference, 1, 22) = 'urn:llmworkgui:secret:'
          AND length(substr(Reference, 23)) > 0
    ),
    -- Every character of the identifier is tested, the first one and the last one included: a
    -- position is only recorded when the character at it matches, so a reference is accepted only
    -- when the walk reaches its last character (MAX(Position) = length(Reference)).
    scanned(Reference, Position) AS (
        SELECT Reference, 23
        FROM prefixed
        WHERE substr(Reference, 23, 1) GLOB '[a-z0-9_-]'
        UNION ALL
        SELECT Reference, Position + 1
        FROM scanned
        WHERE Position < length(Reference)
          AND substr(Reference, Position + 1, 1) GLOB '[a-z0-9_-]'
    )
    SELECT Reference
    FROM scanned
    GROUP BY Reference
    HAVING MAX(Position) = length(Reference)
);

-- Existing references predate this metadata, so their creation time and rotation history are
-- unknown and are not invented here. The recorded state is optimistic only: resolution downgrades a
-- reference to Missing when its payload turns out to be absent (ADR-0005 §5.3).
INSERT INTO SecretReferences (Reference, Kind, State, CreatedAtUtc, LastRotatedAtUtc, RevokedAtUtc)
SELECT Reference, 'Unspecified', 'Active', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), NULL, NULL
FROM Migration003CanonicalReferences;

INSERT OR IGNORE INTO SecretReferenceOwners (Reference, OwnerKind, OwnerId)
SELECT ApiKeySecretReference, 'ProviderProfile', Id
FROM ProviderProfiles
WHERE ApiKeySecretReference IN (SELECT Reference FROM Migration003CanonicalReferences);

INSERT OR IGNORE INTO SecretReferenceOwners (Reference, OwnerKind, OwnerId)
SELECT SecretReference, 'Account', Id
FROM Accounts
WHERE SecretReference IN (SELECT Reference FROM Migration003CanonicalReferences);

DROP TABLE Migration003CanonicalReferences;
