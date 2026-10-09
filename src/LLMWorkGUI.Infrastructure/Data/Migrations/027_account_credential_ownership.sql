-- A key replacement/detachment must not race a retained execution's native launch or send.
-- Revocation remains a separate emergency action; it does not grant termination/release authority.
CREATE TRIGGER TR_AccountCredential_HeldExecution BEFORE UPDATE OF SecretReference ON Accounts
WHEN NEW.SecretReference IS NOT OLD.SecretReference AND EXISTS (
    SELECT 1 FROM Sessions s WHERE s.AccountId=OLD.Id AND (
        s.ActiveExecutionId IS NOT NULL
        OR EXISTS (SELECT 1 FROM Executions e WHERE e.SessionId=s.Id AND e.EndedAtUtc IS NULL)
        OR EXISTS (SELECT 1 FROM WorkflowAdaptationRuntimeOwners o WHERE o.SessionId=s.Id AND o.State!='Terminated')))
BEGIN SELECT RAISE(ABORT,'Account credential is retained by an unresolved execution.'); END;
