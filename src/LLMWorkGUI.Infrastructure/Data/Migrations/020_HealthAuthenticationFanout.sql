CREATE TABLE HealthAuthenticationFanout (
    Id TEXT PRIMARY KEY NOT NULL,
    ScopeType TEXT NOT NULL,
    ScopeId TEXT NOT NULL,
    AccountId TEXT NULL,
    Reason TEXT NOT NULL,
    ObservedAtUtc TEXT NOT NULL,
    EvidenceRedactedJson TEXT NULL
);
CREATE INDEX IX_HealthAuthenticationFanout_Scope ON HealthAuthenticationFanout(ScopeType, ScopeId);
CREATE INDEX IX_HealthAuthenticationFanout_Account ON HealthAuthenticationFanout(AccountId);
CREATE TABLE HealthAuthenticationFanoutAccounts (
    ProjectionId TEXT NOT NULL REFERENCES HealthAuthenticationFanout(Id) ON DELETE CASCADE,
    AccountId TEXT NOT NULL,
    PRIMARY KEY(ProjectionId, AccountId)
);
CREATE INDEX IX_HealthAuthenticationFanoutAccounts_Account ON HealthAuthenticationFanoutAccounts(AccountId);
