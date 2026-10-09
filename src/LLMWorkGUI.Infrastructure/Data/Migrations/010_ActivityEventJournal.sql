-- The Activity Center journal: the durable home of the redacted activity stream (ROADMAP Phase 11,
-- ТЗ §9.2 "100 000 сохранённых execution events").
--
-- Until now the activity stream existed only as objects held by one running process, so "already saved"
-- could mean nothing that survives a restart. This table makes the saved history a product fact: it is
-- app-data-backed, reloaded by a fresh process, and bounded by an explicit retention rule that is
-- reported rather than silent.
--
-- Additive only. Nothing existing is rewritten, altered or removed, so every other table of an already
-- migrated database - projects, providers, routes, sessions, executions, artifacts, reviews, settings -
-- keeps exactly the rows and the text it had. The 001-009 migrations keep their recorded checksums.
--
-- What is stored, and what deliberately is not:
--
--   * The free-text columns are named for the redacted content they hold. The ingestion boundary
--     (ActivityEventRedactor) is what guarantees that: the product never constructs an ActivityEvent
--     holding a raw secret, so there is no unredacted copy of a journal row anywhere in the process.
--     A raw secret therefore cannot be read back out of the database even by direct SQL.
--   * DiffText and ArtifactContent are intentionally not carried here. A 256 KiB message body belongs in
--     the blob store, not in an index-bearing row that every filter has to walk; the Activity Center
--     keeps the structured provenance (name, size, digest, change status) that the detail pane needs and
--     rehydrates the body from the blob store on demand. This is what keeps the journal bounded even
--     when the normative profile offers messages up to 256 KiB.
--   * The structured provenance columns (SessionId, ExecutionId, RouteId, ArtifactName, ArtifactSha256)
--     are stored verbatim, because provenance is what the screen exists to show and none of them carries
--     credential material by construction.
--
-- Id is the primary key: an event is identified by its own unique id, never by a derived per-execution
-- key, so repeated updates of one execution accumulate as distinct rows instead of collapsing onto one.
CREATE TABLE IF NOT EXISTS ActivityEvents (
    Id TEXT NOT NULL PRIMARY KEY,
    OccurredAtUtc TEXT NOT NULL,
    Kind TEXT NOT NULL CHECK (Kind IN ('Execution', 'Session', 'Health', 'UserAction', 'System')),
    Role TEXT NOT NULL,
    State TEXT NOT NULL CHECK (State IN ('Running', 'Completed', 'Failed', 'Cancelled', 'Warning')),
    Source TEXT NOT NULL CHECK (Source IN ('Native', 'Synthetic')),
    TitleRedacted TEXT NOT NULL,
    DescriptionRedacted TEXT NOT NULL,
    SessionId TEXT NULL,
    ExecutionId TEXT NULL,
    RouteId TEXT NULL,
    ArtifactName TEXT NULL,
    ArtifactSizeBytes INTEGER NULL CHECK (ArtifactSizeBytes IS NULL OR ArtifactSizeBytes >= 0),
    ArtifactSha256 TEXT NULL,
    ArtifactChangeStatus TEXT NULL,
    IngestedAtUtc TEXT NOT NULL
);

-- Newest-first paging is the only read pattern the Activity Center uses, and the retention rule deletes
-- exactly the oldest rows. One index serves both: the retention pass walks it in reverse to find its cut
-- point, and a page walk stops after the page instead of scanning the whole journal.
CREATE INDEX IF NOT EXISTS IX_ActivityEvents_OccurredAtUtc
    ON ActivityEvents (OccurredAtUtc DESC, Id DESC);

-- Provenance is the second axis an operator filters and inspects by, and a normative run asserts its
-- eight execution identities independently of the free-text search.
CREATE INDEX IF NOT EXISTS IX_ActivityEvents_ExecutionId
    ON ActivityEvents (ExecutionId);
