using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Projects;

public sealed record ProjectDataPolicySnapshot(string ProjectId, string DisplayName, string RootPath,
    DataClassification DataClassification, string Revision);

public interface IProjectDataPolicyService
{
    Task<IReadOnlyList<ProjectDataPolicySnapshot>> ListAsync(CancellationToken cancellationToken = default);
    Task ChangeAsync(ProjectDataPolicySnapshot expected, DataClassification requested, bool confirmed,
        CancellationToken cancellationToken = default);
}
