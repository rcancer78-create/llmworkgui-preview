using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

/// <summary>
/// Repository for managing operational approval rules across projects and backends.
/// </summary>
public interface IApprovalRuleRepository
{
    Task<IReadOnlyList<ApprovalRule>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApprovalRule>> ListByProjectIdAsync(string projectId, CancellationToken cancellationToken = default);
    Task<ApprovalRule?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(ApprovalRule rule, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
