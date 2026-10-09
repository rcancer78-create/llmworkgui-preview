-- Durable full-text search for the Activity Center journal, plus the indexes the durable filter axes
-- need (ROADMAP Phase 11, ТЗ §9.2).
--
-- Why this migration exists. Migration 010 made the saved history durable, but the only free-text index
-- was the in-memory EventSearchIndex, which is bounded at 100 000 documents. The normative profile is
-- 100 000 already-saved events PLUS 90 000 more during the run, so a search on the shipped product could
-- only ever see the newest 100 000 of the 190 000 rows that were actually retained and searchable by
-- paging. Search and paging disagreed about the retained set. An exact durable search is the fix, and
-- "exact" over 190 000 rows is only possible with an index: a LIKE scan over the concatenated searchable
-- text measured 132 ms for one token and 349 ms for two on this machine, which does not fit the §9.2
-- 200 ms UI budget, while the FTS5 MATCH below measures in single-digit milliseconds.
--
-- What is indexed. One column, `body`, holding exactly the same projection the in-memory index uses:
-- id, title, description, role, kind, state, source, session, execution, route and artifact name, joined
-- by single spaces. That text is already redacted - the ingestion boundary redacts before anything is
-- stored - so the full-text index cannot make a secret findable either. Artifact bodies and diffs are
-- deliberately excluded, exactly as in 010, so raw file content can never be surfaced by a search.
--
-- The projection is written by the repository from the same ActivityEventSearchText.Build call the
-- in-memory index uses, so the two indexes cannot drift apart: a token that the in-memory index finds is
-- a token the durable index finds. The backfill below has to express that projection in SQL because the
-- rows already on disk predate the repository change; it concatenates the columns in the same order.
--
-- Maintenance. One row in ActivityEventsSearch per row in ActivityEvents, keyed by the same rowid. The
-- repository inserts it inside the same transaction as the journal row and deletes it inside the same
-- transaction as a retention delete, so the index cannot drift from the journal. There are no triggers:
-- a trigger would have to rebuild the projection in SQL on every insert, which is the one thing that
-- could make the two indexes disagree.
--
-- Additive only. No existing table is altered, removed or rewritten, so the recorded checksums of
-- 001-010 stay valid and every other table of an already migrated database keeps exactly its rows.
CREATE VIRTUAL TABLE IF NOT EXISTS ActivityEventsSearch USING fts5(body, tokenize='unicode61');

INSERT INTO ActivityEventsSearch (rowid, body)
SELECT
    rowid,
    Id || ' ' || TitleRedacted || ' ' || DescriptionRedacted || ' ' || Role || ' ' || Kind || ' '
        || State || ' ' || Source || ' ' || COALESCE(SessionId, '') || ' ' || COALESCE(ExecutionId, '')
        || ' ' || COALESCE(RouteId, '') || ' ' || COALESCE(ArtifactName, '')
FROM ActivityEvents;

-- Role and state are the two axes an operator filters on most and the only ones the journal could not
-- serve from an index. One composite index serves both the single-axis and the combined filter, and
-- carrying the newest-first ordering key in it means a filtered page walk stops after the page instead
-- of sorting every matching row.
CREATE INDEX IF NOT EXISTS IX_ActivityEvents_RoleStateTime
    ON ActivityEvents (Role, State, OccurredAtUtc DESC, Id DESC);

-- Provenance (native product events against synthetic fixtures) is the third durable filter axis.
CREATE INDEX IF NOT EXISTS IX_ActivityEvents_SourceTime
    ON ActivityEvents (Source, OccurredAtUtc DESC, Id DESC);
