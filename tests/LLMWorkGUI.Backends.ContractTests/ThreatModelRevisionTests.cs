using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

/// <summary>
/// Phase 12A threat-model review evidence: the document must stay current with every implemented
/// subsystem and with the hardening invariants of Milestone 12A.
/// </summary>
public sealed class ThreatModelRevisionTests
{
    [Fact]
    public void ThreatModel_DeclaresThePhase12ARevision()
    {
        var content = File.ReadAllText(ThreatModelPath);

        Assert.Contains("**Ревизия:** 2026-09-25", content, StringComparison.Ordinal);
        Assert.Contains("TASK-062", content, StringComparison.Ordinal);
        Assert.Contains("Phase 12 Milestone 12A", content, StringComparison.Ordinal);
        Assert.Contains("## 8. Ревизия Phase 12A: покрытие реализованных подсистем", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreatModel_CoversEveryImplementedSubsystem()
    {
        var content = File.ReadAllText(ThreatModelPath);

        foreach (var subsystem in new[]
                 {
                     "Mirasim backend",
                     "StarCliProxy",
                     "Codex",
                     "AGY",
                     "Workflow Studio",
                     "Pre-Coder Gate",
                     "Activity Center",
                     "Diagnostic bundle",
                     "DB backup/restore",
                     "Crash/reboot recovery",
                     "CLI version mismatch"
                 })
        {
            Assert.Contains(subsystem, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ThreatModel_FixesTheHardeningInvariants()
    {
        var content = File.ReadAllText(ThreatModelPath);

        foreach (var invariant in new[]
                 {
                     "DiagnosticBundleBlockedException",
                     "WorkflowSecretScanner",
                     "SensitiveDataFilter",
                     "%USERPROFILE%",
                     "VACUUM INTO",
                     "PRAGMA integrity_check",
                     "SHA-256 sidecar",
                     "rollback-снапшот",
                     "append-only",
                     "capability probe",
                     "silent fallback"
                 })
        {
            Assert.Contains(invariant, content, StringComparison.Ordinal);
        }
    }

    private static string ThreatModelPath =>
        Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "architecture",
            "THREAT_MODEL.md");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LLMWorkGUI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root containing 'LLMWorkGUI.sln' was not found.");
    }
}
