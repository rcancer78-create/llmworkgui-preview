using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed class ApprovalRuleItemViewModel : ObservableObject
{
    public string Id { get; }
    public BackendType Backend { get; }
    public string ProviderProfileId { get; }
    public string ProjectId { get; }
    public string? PathScope { get; }
    public NormalizedApprovalKind Kind { get; }
    public string Operation { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public string CreatedBy { get; }
    public DateTimeOffset CreatedAt { get; }

    public string ScopeSummary => string.IsNullOrWhiteSpace(PathScope)
        ? $"Project: {ProjectId} | Operation: {Operation}"
        : $"Project: {ProjectId} | Path: {PathScope} | Operation: {Operation}";

    public ApprovalRuleItemViewModel(ApprovalRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        Id = rule.Id;
        Backend = rule.Backend;
        ProviderProfileId = rule.ProviderProfileId;
        ProjectId = rule.ProjectId;
        PathScope = rule.PathScope;
        Kind = rule.Kind;
        Operation = rule.Operation;
        ExpiresAt = rule.ExpiresAt;
        CreatedBy = rule.CreatedBy;
        CreatedAt = rule.CreatedAt;
    }
}
