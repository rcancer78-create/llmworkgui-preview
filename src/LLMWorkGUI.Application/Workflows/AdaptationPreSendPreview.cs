using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

public sealed record AdaptationPreSendPreview
{
    public const string UnknownQuotaValue = "Unknown";

    public AdaptationPreSendPreview(
        string sourceVersionId,
        int sourceVersionNumber,
        string sourceBlobId,
        string adapterRouteId,
        string adapterModelId,
        AdaptationGoal goal,
        WorkflowSecretScanReport scanReport,
        IReadOnlyList<string> includedFiles,
        IReadOnlyList<string> excludedFiles,
        SanitizedCapabilityCatalog sanitizedCatalog,
        string quotaState,
        string quotaFreshness,
        double? reserveThreshold,
        string promptPreview)
    {
        SourceVersionId = ApplicationGuard.NotBlank(sourceVersionId, nameof(sourceVersionId));
        SourceVersionNumber = sourceVersionNumber;
        SourceBlobId = ApplicationGuard.NotBlank(sourceBlobId, nameof(sourceBlobId));
        AdapterRouteId = ApplicationGuard.NotBlank(adapterRouteId, nameof(adapterRouteId));
        AdapterModelId = ApplicationGuard.NotBlank(adapterModelId, nameof(adapterModelId));
        Goal = goal;
        ScanReport = scanReport ?? throw new ArgumentNullException(nameof(scanReport));
        IncludedFiles = (includedFiles ?? throw new ArgumentNullException(nameof(includedFiles))).ToArray();
        ExcludedFiles = (excludedFiles ?? throw new ArgumentNullException(nameof(excludedFiles))).ToArray();
        SanitizedCatalog = sanitizedCatalog ?? throw new ArgumentNullException(nameof(sanitizedCatalog));
        QuotaState = ApplicationGuard.NotBlank(quotaState, nameof(quotaState));
        QuotaFreshness = ApplicationGuard.NotBlank(quotaFreshness, nameof(quotaFreshness));
        ReserveThreshold = reserveThreshold;
        PromptPreview = promptPreview ?? throw new ArgumentNullException(nameof(promptPreview));
    }

    public string SourceVersionId { get; }

    public int SourceVersionNumber { get; }

    public string SourceBlobId { get; }

    public string AdapterRouteId { get; }

    public string AdapterModelId { get; }

    public AdaptationGoal Goal { get; }

    public WorkflowSecretScanReport ScanReport { get; }

    public IReadOnlyList<string> IncludedFiles { get; }

    public IReadOnlyList<string> ExcludedFiles { get; }

    public SanitizedCapabilityCatalog SanitizedCatalog { get; }

    public string QuotaState { get; }

    public string QuotaFreshness { get; }

    public double? ReserveThreshold { get; }

    public string PromptPreview { get; }
}
