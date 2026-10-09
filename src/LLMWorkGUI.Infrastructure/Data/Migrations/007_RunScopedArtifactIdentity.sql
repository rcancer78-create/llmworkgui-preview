-- A run-scoped artifact row also has to name itself (Phase 10A/10E durable artifact gate).
--
-- Migration 006 made a *new* run-scoped row complete in the values that say what was stored: its stage, its
-- kind, the blob that holds the bytes and the hash those bytes produce. It did not look at the row's own Id,
-- and `Id TEXT NOT NULL` accepts an empty or whitespace string - NOT NULL is about the absence of a value,
-- not about the value naming anything.
--
-- The artifact id is not decoration. It is the identity a transition record pins its authorization to ("this
-- transition was authorized by artifact X"), it is the tiebreak that resolves two artifacts recorded inside
-- the same clock tick, and it is what an operator has to be able to name in order to find the row at all. A
-- blank id takes all three away, and the repository then refuses to read the run rather than letting the
-- gate fall back on an older artifact of the same stage.
--
-- 006 is not rewritten here: a database that already applied it records its checksum, and changing the file
-- would make that database refuse to start. This migration is additive in the same sense 006 was:
--
--   * The trigger fires only for a row that names a WorkflowRunId, so every execution-only legacy row is
--     unaffected and keeps whatever identifier it has.
--   * Only INSERT is guarded. Nothing that exists today is re-validated, rewritten, refused or deleted: a
--     database that holds a run-scoped row with a blank identifier keeps it, and the read path is what
--     refuses to load that run. Deleting or renaming a row somebody recorded is a decision for an operator,
--     not one a schema migration may take on its own.
--
-- The whitespace test has to be spelled out rather than left to trim(X), whose second form is the only way
-- to say what "whitespace" means here: SQLite's one-argument trim() removes spaces and nothing else, so a
-- tab would otherwise pass this trigger and be refused by the reader, which decides the same question in
-- .NET and calls U+0009-U+000D, U+0085, U+00A0, U+1680, U+2000-U+200A, U+2028, U+2029, U+202F, U+205F and
-- U+3000 whitespace. The set below is exactly those code points, and a storage rule that is narrower than
-- the rule it defends is not a rule.
CREATE TRIGGER TR_Artifacts_RunScopedArtifactIsNamed
BEFORE INSERT ON Artifacts
WHEN NEW.WorkflowRunId IS NOT NULL
     AND NEW.Id IS NOT NULL
     AND length(
             trim(
                 NEW.Id,
                    ' ' || char(9) || char(10) || char(11) || char(12) || char(13) || char(133)
                 || char(160) || char(5760)
                 || char(8192) || char(8193) || char(8194) || char(8195) || char(8196) || char(8197)
                 || char(8198) || char(8199) || char(8200) || char(8201) || char(8202)
                 || char(8232) || char(8233) || char(8239) || char(8287) || char(12288)
             )
         ) = 0
BEGIN
    SELECT RAISE(ABORT, 'A run-scoped workflow artifact must be given a non-empty identifier.');
END;
