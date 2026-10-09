CREATE TABLE ProjectDataPolicyChanges (
    Id TEXT NOT NULL PRIMARY KEY,
    ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
    PreviousClass TEXT NOT NULL CHECK (PreviousClass IN ('PublicSource','PrivateSource','Restricted')),
    NewClass TEXT NOT NULL CHECK (NewClass IN ('PublicSource','PrivateSource','Restricted')),
    ChangedAtUtc TEXT NOT NULL,
    Reason TEXT NOT NULL
);
CREATE INDEX IX_ProjectDataPolicyChanges_ProjectId ON ProjectDataPolicyChanges(ProjectId,ChangedAtUtc);
