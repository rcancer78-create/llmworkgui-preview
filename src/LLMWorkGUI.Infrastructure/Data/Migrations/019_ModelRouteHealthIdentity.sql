-- Legacy account:model identifiers could alias another account/model pair or an opaque route.
-- Retain legacy records/audit. Project restrictions to every possible account interpretation, including scopes without an Accounts row.
-- Ambiguous positive evidence never authorizes a new scope: it needs a fresh pinned probe.
CREATE TEMP TABLE ModelRouteHealthMigration AS
WITH RECURSIVE Splits(LegacyId, LegacyScopeId, Separator) AS (
    SELECT Id, ScopeId, instr(ScopeId, ':') FROM HealthStates
    WHERE ScopeType='route' AND instr(ScopeId, ':') > 0
    UNION ALL
    SELECT LegacyId, LegacyScopeId,
           Separator + instr(substr(LegacyScopeId, Separator + 1), ':')
    FROM Splits WHERE instr(substr(LegacyScopeId, Separator + 1), ':') > 0
), ValidSplits AS (
    SELECT * FROM Splits WHERE Separator > 1 AND Separator < length(LegacyScopeId)
)
SELECT s.LegacyId,
       'v1:' || hex(substr(s.LegacyScopeId, 1, s.Separator - 1)) || ':' ||
       hex(substr(s.LegacyScopeId, s.Separator + 1)) AS NewScopeId,
       (SELECT count(*) FROM ValidSplits s2 WHERE s2.LegacyId=s.LegacyId)
       + (SELECT count(*) FROM Routes r WHERE r.Id=s.LegacyScopeId) AS Interpretations
FROM ValidSplits s;

INSERT INTO HealthStates
    (Id, ScopeType, ScopeId, State, ErrorClass, FailureCount, WindowStartedAtUtc,
     CooldownUntilUtc, EvidenceRedactedJson, UpdatedAtUtc, FailureHistoryJson)
SELECT 'model-route:' || m.NewScopeId, 'model-route', m.NewScopeId,
       CASE WHEN m.Interpretations > 1 AND h.State IN ('Healthy','Degraded','ForcedEnabled')
            THEN 'ProbeRequired' ELSE h.State END,
       h.ErrorClass, h.FailureCount, h.WindowStartedAtUtc, h.CooldownUntilUtc,
       h.EvidenceRedactedJson, h.UpdatedAtUtc, h.FailureHistoryJson
FROM ModelRouteHealthMigration m JOIN HealthStates h ON h.Id=m.LegacyId;

INSERT INTO HealthEvents
    (Id, ScopeType, ScopeId, PreviousState, NewState, ErrorClass, Reason, OccurredAtUtc)
SELECT lower(hex(randomblob(16))), n.ScopeType, n.ScopeId, NULL, n.State, n.ErrorClass,
       'Migration 019 projected legacy model-route health; ambiguous positive evidence requires a new probe.',
       strftime('%Y-%m-%dT%H:%M:%fZ','now')
FROM ModelRouteHealthMigration m JOIN HealthStates n
  ON n.ScopeType='model-route' AND n.ScopeId=m.NewScopeId;

-- A legacy key that also names an actual opaque route is ambiguous on that side too.
INSERT INTO HealthEvents
    (Id, ScopeType, ScopeId, PreviousState, NewState, ErrorClass, Reason, OccurredAtUtc)
SELECT lower(hex(randomblob(16))), h.ScopeType, h.ScopeId, h.State, 'ProbeRequired', h.ErrorClass,
       'Migration 019 found a legacy route/model-route identity collision; a new probe is required.',
       strftime('%Y-%m-%dT%H:%M:%fZ','now')
FROM HealthStates h
WHERE h.Id IN (SELECT LegacyId FROM ModelRouteHealthMigration)
  AND EXISTS (SELECT 1 FROM Routes r WHERE r.Id=h.ScopeId)
  AND h.State IN ('Healthy','Degraded','ForcedEnabled');

UPDATE HealthStates SET State='ProbeRequired'
WHERE Id IN (SELECT LegacyId FROM ModelRouteHealthMigration)
  AND EXISTS (SELECT 1 FROM Routes r WHERE r.Id=HealthStates.ScopeId)
  AND State IN ('Healthy','Degraded','ForcedEnabled');

DROP TABLE ModelRouteHealthMigration;
