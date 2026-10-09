namespace LLMWorkGUI.Infrastructure.Data;

public sealed record MigrationReport(
    int PreviousSchemaVersion,
    int CurrentSchemaVersion,
    IReadOnlyList<DatabaseMigration> AppliedMigrations)
{
    public bool WasUpToDate => AppliedMigrations.Count == 0;
}
