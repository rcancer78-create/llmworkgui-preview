using LLMWorkGUI.Application.Providers;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Providers;

public sealed class CliVersionValidatorTests
{
    private readonly CliVersionValidator _validator = new();

    [Fact]
    public void Validate_OpenCodeBaselineVersion_IsSupported()
    {
        var assessment = _validator.Validate(CliVersionValidator.OpenCodeCliName, "1.18.31");

        Assert.Equal(CliVersionStatus.Supported, assessment.Status);
        Assert.True(assessment.IsSupported);
        Assert.False(assessment.BlocksSilentFallback);
        Assert.False(assessment.RequiresCapabilityProbe);
    }

    [Fact]
    public void Validate_OpenCodeNewerVersion_RequiresCapabilityProbeInsteadOfSilentFallback()
    {
        var assessment = _validator.Validate(CliVersionValidator.OpenCodeCliName, "opencode 1.19.0");

        Assert.Equal(CliVersionStatus.CapabilityProbeRequired, assessment.Status);
        Assert.True(assessment.RequiresCapabilityProbe);
        Assert.False(assessment.BlocksSilentFallback);
        Assert.Equal("1.19.0.0", assessment.ObservedVersion);
    }

    [Fact]
    public void Validate_OpenCodeOlderVersion_BlocksTheAdapter()
    {
        var assessment = _validator.Validate(CliVersionValidator.OpenCodeCliName, "1.17.9");

        Assert.Equal(CliVersionStatus.OlderThanMinimum, assessment.Status);
        Assert.True(assessment.BlocksSilentFallback);
        Assert.False(assessment.RequiresCapabilityProbe);
    }

    [Fact]
    public void Validate_MirasimExactVersion_IsSupported()
    {
        var assessment = _validator.Validate(CliVersionValidator.MirasimCliName, "0.0.354");

        Assert.Equal(CliVersionStatus.Supported, assessment.Status);
    }

    [Fact]
    public void Validate_MirasimDifferentVersion_IsAnExactMismatch()
    {
        var assessment = _validator.Validate(CliVersionValidator.MirasimCliName, "0.0.355");

        Assert.Equal(CliVersionStatus.ExactVersionMismatch, assessment.Status);
        Assert.True(assessment.BlocksSilentFallback);
    }

    [Fact]
    public void Validate_CursorAgentWithoutDeclaredRange_AlwaysRequiresProbe()
    {
        var assessment = _validator.Validate(CliVersionValidator.CursorAgentCliName, "2026.09.15-d2fe57e");

        Assert.Equal(CliVersionStatus.CapabilityProbeRequired, assessment.Status);
        Assert.False(assessment.BlocksSilentFallback);
    }

    [Fact]
    public void Validate_UnparseableOutput_BlocksTheAdapter()
    {
        var assessment = _validator.Validate(CliVersionValidator.CodexCliName, "version unavailable");

        Assert.Equal(CliVersionStatus.Unparseable, assessment.Status);
        Assert.True(assessment.BlocksSilentFallback);
    }

    [Fact]
    public void Validate_MissingOutput_IsReportedAsNotDetectedWithoutBlocking()
    {
        var assessment = _validator.Validate(CliVersionValidator.AgyCliName, null);

        Assert.Equal(CliVersionStatus.NotDetected, assessment.Status);
        Assert.False(assessment.BlocksSilentFallback);
    }

    [Fact]
    public void Validate_UnknownCli_IsTypedAndBlocks()
    {
        var assessment = _validator.Validate("unknown-cli", "1.0.0");

        Assert.Equal(CliVersionStatus.UnknownCli, assessment.Status);
        Assert.Null(assessment.Requirement);
        Assert.True(assessment.BlocksSilentFallback);
    }

    [Fact]
    public void ValidateAll_CoversEveryRequirementAndReportsMissingClis()
    {
        var assessments = _validator.ValidateAll(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [CliVersionValidator.OpenCodeCliName] = "1.18.31",
            [CliVersionValidator.MirasimCliName] = "0.0.354"
        });

        Assert.Equal(CliVersionValidator.DefaultRequirements.Count, assessments.Count);

        Assert.Equal(
            CliVersionStatus.Supported,
            assessments.Single(item => item.CliName == CliVersionValidator.OpenCodeCliName).Status);

        Assert.Equal(
            CliVersionStatus.NotDetected,
            assessments.Single(item => item.CliName == CliVersionValidator.AgyCliName).Status);
    }

    [Theory]
    [InlineData("opencode 1.18.31", 1, 18, 31)]
    [InlineData("2026.09.15-d2fe57e", 2026, 9, 15)]
    [InlineData("v0.155.0", 0, 155, 0)]
    [InlineData("1.1.23\n", 1, 1, 23)]
    public void TryParseVersion_ExtractsComparableVersion(string output, int major, int minor, int build)
    {
        Assert.True(CliVersionValidator.TryParseVersion(output, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no digits here")]
    public void TryParseVersion_RejectsOutputWithoutAVersion(string output)
    {
        Assert.False(CliVersionValidator.TryParseVersion(output, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void Validate_RejectsAnotherProgramsVersionEvenWhenItMatchesTheBaseline()
    {
        var result = _validator.Validate("opencode", "Runtime 1.18.31");
        Assert.Equal(CliVersionStatus.Unparseable, result.Status);
    }

    [Theory]
    [InlineData("Runtime 1.18.31; opencode 1.17.0")]
    [InlineData("opencode 1.18.31\nruntime 8.2.1")]
    [InlineData("1.18.31.0.7")]
    public void TryParseVersion_RejectsAmbiguousOrPartialNumericTokens(string output)
    {
        Assert.False(CliVersionValidator.TryParseVersion(output, out var version));
        Assert.Null(version);
    }

    [Theory]
    [InlineData("opencode", "OpenCode v1.18.31")]
    [InlineData("codex", "codex-cli 0.155.0")]
    [InlineData("agy", "AGY version 1.1.23")]
    public void Validate_AcceptsTheCliLabelAndItsSingleVersion(string cli, string output)
    {
        Assert.Equal(CliVersionStatus.Supported, _validator.Validate(cli, output).Status);
    }

    [Fact]
    public void CustomRequirements_OverrideTheDefaultMatrix()
    {
        var validator = new CliVersionValidator(new[]
        {
            new CliVersionRequirement("custom", "Custom CLI", new Version(2, 0, 0), new Version(3, 0, 0))
        });

        Assert.Equal(CliVersionStatus.Supported, validator.Validate("custom", "2.5.1").Status);
        Assert.Equal(CliVersionStatus.OlderThanMinimum, validator.Validate("custom", "1.9.9").Status);
        Assert.Equal(
            CliVersionStatus.CapabilityProbeRequired,
            validator.Validate("custom", "3.1.0").Status);
        Assert.Equal(CliVersionStatus.UnknownCli, validator.Validate("opencode", "1.18.31").Status);
    }
}
