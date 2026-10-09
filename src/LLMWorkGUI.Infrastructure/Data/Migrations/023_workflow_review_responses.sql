CREATE TABLE WorkflowReviewResponses (
    ExecutionId TEXT NOT NULL PRIMARY KEY REFERENCES WorkflowReviewExecutions(ExecutionId) ON DELETE CASCADE,
    NativeSessionId TEXT NULL,
    ResponseText TEXT NOT NULL CHECK(length(CAST(ResponseText AS BLOB)) BETWEEN 1 AND 65536),
    ResponseSha256 TEXT NOT NULL CHECK(length(ResponseSha256)=64 AND ResponseSha256 NOT GLOB '*[^0-9A-F]*'),
    ReceivedSha256 TEXT NOT NULL CHECK(length(ReceivedSha256)=64 AND ReceivedSha256 NOT GLOB '*[^0-9A-F]*'),
    CapturedAtUtc TEXT NOT NULL
);

CREATE TRIGGER TR_WorkflowReviewResponses_CompletionBinding
BEFORE INSERT ON WorkflowReviewResponses
WHEN NOT EXISTS (
    SELECT 1 FROM WorkflowReviewExecutions b
    JOIN Executions e ON e.Id=b.ExecutionId AND e.SessionId=b.SessionId
    JOIN Sessions s ON s.Id=b.SessionId AND s.WorkflowRunId=b.WorkflowRunId
    WHERE b.ExecutionId=NEW.ExecutionId AND b.IsReadOnly=1
      AND e.State IN ('Succeeded','Failed','Cancelled','RouteMismatch','Ambiguous','TimedOut')
      AND s.NativeSessionId IS NEW.NativeSessionId
      AND s.State IN ('Closed','Ambiguous')
)
BEGIN SELECT RAISE(ABORT, 'Reviewer response requires its completed read-only execution and session'); END;

CREATE TRIGGER TR_WorkflowReviewResponses_Immutable
BEFORE UPDATE ON WorkflowReviewResponses
BEGIN SELECT RAISE(ABORT, 'Reviewer response is immutable'); END;
