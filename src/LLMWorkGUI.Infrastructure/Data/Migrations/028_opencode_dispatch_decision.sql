-- Snapshot of the route identity used at OpenCode admission. ObservedRouteId stays empty
-- until a confirmed outcome reports the same model id.
ALTER TABLE Executions ADD COLUMN DispatchProviderProfileId TEXT NULL
    CHECK (DispatchProviderProfileId IS NULL OR length(trim(DispatchProviderProfileId)) > 0);
ALTER TABLE Executions ADD COLUMN DispatchAccountId TEXT NULL
    CHECK (DispatchAccountId IS NULL OR length(trim(DispatchAccountId)) > 0);
ALTER TABLE Executions ADD COLUMN DispatchNativeModelId TEXT NULL
    CHECK (DispatchNativeModelId IS NULL OR length(trim(DispatchNativeModelId)) > 0);

CREATE TRIGGER TR_Executions_DispatchDecisionComplete
BEFORE INSERT ON Executions
WHEN (NEW.DispatchProviderProfileId IS NULL) != (NEW.DispatchAccountId IS NULL)
  OR (NEW.DispatchProviderProfileId IS NULL) != (NEW.DispatchNativeModelId IS NULL)
BEGIN
    SELECT RAISE(ABORT, 'OpenCode dispatch decision stores profile, account, and native model together.');
END;

CREATE TRIGGER TR_Executions_DispatchDecisionImmutable
BEFORE UPDATE OF DispatchProviderProfileId, DispatchAccountId, DispatchNativeModelId ON Executions
WHEN NEW.DispatchProviderProfileId IS NOT OLD.DispatchProviderProfileId
  OR NEW.DispatchAccountId IS NOT OLD.DispatchAccountId
  OR NEW.DispatchNativeModelId IS NOT OLD.DispatchNativeModelId
BEGIN
    SELECT RAISE(ABORT, 'OpenCode dispatch decision is immutable.');
END;
