-- Legacy aggregate-only observations cannot recover individual failure timestamps/classes.
-- NULL retains that distinction; all new snapshots persist an exact (possibly empty) history.
ALTER TABLE HealthStates ADD COLUMN FailureHistoryJson TEXT NULL;
