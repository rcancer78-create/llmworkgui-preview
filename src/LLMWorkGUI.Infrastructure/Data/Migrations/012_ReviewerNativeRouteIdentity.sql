-- Gateway-native identity for the assigned reviewer route: the four facts a gateway has to report after
-- actual execution, stored on the rows they describe and nowhere else.
-- (Phase 10 native reviewer identity foundation.)
--
-- Why this migration exists. The read-only reviewer channel refuses every route for four named reasons, and
-- three of them are storage facts this schema cannot currently express. A gateway reports a native provider
-- name, a native account name, a native model name and a mode or variant; nothing here could record any of
-- them, so no mapping from a response-origin observation to one persisted Routes.Id could be built, and the
-- refusal was correct. This migration gives that mapping somewhere to live without touching anything that
-- already exists.
--
-- Four new columns, all nullable, one per identity, and each on the row it actually describes:
--
--   * ProviderProfiles.GatewayNativeId - the gateway's own name for this provider.
--   * Accounts.GatewayNativeId         - the gateway's own name for this account.
--   * Models.GatewayNativeId           - the gateway's own name for the model that actually ran.
--   * Routes.GatewayRouteKey           - one gateway route key, when the gateway reports a single key
--                                        instead of separate mode dimensions.
--
-- What is deliberately NOT reused. Three existing columns look like they already hold these facts and none
-- of them does:
--
--   * Accounts.ProviderNativeId is, for the Codex backend, a filesystem path and for AGY a profile label.
--     Those are where an account happens to be stored, not which account answered: two saved profiles can
--     name one path, and a path can be re-pointed. Reading it as an account identity would let a row's
--     storage location stand in for the credential that ran. It is left exactly as it is.
--   * Models.ProviderModelId may be a requested alias. A request names a model, and the gateway maps
--     aliases onto real models, with fallbacks. Matching a response-origin native model id against a
--     requested alias would match the question to the answer. Only the new column is ever matched against.
--   * Routes.Id is this application's own primary key. A gateway route key is the gateway's namespace and
--     can collide with ours by accident, so the new column is separate and the two are never compared.
--
-- No backfill, by decision rather than by omission. Every existing row therefore stays unbound, and that is
-- the truthful state: this build has never seen a gateway report a response-origin identity, so there is no
-- value to copy. Deriving a gateway id from a display name, a path, a profile label, a model alias or a
-- past request would manufacture exactly the evidence the reviewer channel refuses to invent, and it would
-- do so in a column a later reviewer could not tell from a real observation. An unbound row is legible; a
-- fabricated one is not. Nothing is rewritten, and the recorded checksums of 001-011 stay valid.
--
-- Blank values are refused, not normalized. A column that is present must hold something; an empty or
-- whitespace-only value would otherwise read as "the gateway reported an empty name", which is a different
-- claim from "no gateway name is recorded", and the first one is unfalsifiable. The resolver depends on
-- telling those two apart.
--
-- Uniqueness is partial and namespaced, and the namespace is different for each of the four because the
-- gateway's identifiers are too. A native provider name only means something within one backend, an
-- account name within one provider, a model name within one provider, and a route key within one provider.
-- Two rows that declare the same native name in the same namespace are a storage-level contradiction, and
-- the database is where such a contradiction is cheapest to refuse. A NULL is excluded from every index, so
-- the thousands of unbound rows this migration creates do not collide with each other.
--
-- The consequence is worth stating plainly: uniqueness inside a namespace is necessary but not sufficient
-- for an observation to resolve. Two provider profiles on two different backends may legitimately declare
-- the same native provider name, and two routes on one tuple may differ only in a mode dimension the
-- gateway did not report. Neither is forbidden here, and both have to be refused by the resolver at read
-- time - which is why the resolver exists and why it refuses ambiguity rather than choosing.
--
-- The blank test names its own character set rather than using trim()'s default, because SQLite's default
-- strips the space character and nothing else: a tab, a newline or a form feed would pass a bare
-- length(trim(x)) > 0 and then be refused by the domain, which treats every Unicode whitespace as blank.
-- The two have to agree or the column and the entity would disagree about what a value means, so the set
-- is spelled out. It covers ASCII whitespace, the non-breaking space and the ogham space mark; the domain
-- guard remains the authority on the rest, and no writer in this build can put a value it refuses here.
ALTER TABLE ProviderProfiles ADD COLUMN GatewayNativeId TEXT NULL
    CHECK (GatewayNativeId IS NULL
        OR length(trim(GatewayNativeId, ' ' || char(9) || char(10) || char(11) || char(12) || char(13)
            || char(160) || char(5760))) > 0);

ALTER TABLE Accounts ADD COLUMN GatewayNativeId TEXT NULL
    CHECK (GatewayNativeId IS NULL
        OR length(trim(GatewayNativeId, ' ' || char(9) || char(10) || char(11) || char(12) || char(13)
            || char(160) || char(5760))) > 0);

ALTER TABLE Models ADD COLUMN GatewayNativeId TEXT NULL
    CHECK (GatewayNativeId IS NULL
        OR length(trim(GatewayNativeId, ' ' || char(9) || char(10) || char(11) || char(12) || char(13)
            || char(160) || char(5760))) > 0);

ALTER TABLE Routes ADD COLUMN GatewayRouteKey TEXT NULL
    CHECK (GatewayRouteKey IS NULL
        OR length(trim(GatewayRouteKey, ' ' || char(9) || char(10) || char(11) || char(12) || char(13)
            || char(160) || char(5760))) > 0);

-- A provider name is only meaningful against the backend that reports it, so the namespace is the pair.
CREATE UNIQUE INDEX IF NOT EXISTS IX_ProviderProfiles_BackendGatewayNativeId
    ON ProviderProfiles (Backend, GatewayNativeId)
    WHERE GatewayNativeId IS NOT NULL;

-- An account and a model are both named inside one provider, so the namespace is the owning profile.
CREATE UNIQUE INDEX IF NOT EXISTS IX_Accounts_ProviderProfileGatewayNativeId
    ON Accounts (ProviderProfileId, GatewayNativeId)
    WHERE GatewayNativeId IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS IX_Models_ProviderProfileGatewayNativeId
    ON Models (ProviderProfileId, GatewayNativeId)
    WHERE GatewayNativeId IS NOT NULL;

-- The gateway route key is the gateway's own namespace. It is deliberately NOT unique against Routes.Id
-- and nothing in the database relates the two.
CREATE UNIQUE INDEX IF NOT EXISTS IX_Routes_ProviderProfileGatewayRouteKey
    ON Routes (ProviderProfileId, GatewayRouteKey)
    WHERE GatewayRouteKey IS NOT NULL;
