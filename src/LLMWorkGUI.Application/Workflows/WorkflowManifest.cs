using System.Text.Json;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

public sealed record WorkflowManifest(
    bool HasFormalManifest,
    string? ManifestPath,
    string? WorkflowId,
    string? WorkflowVersion,
    string? Name,
    IReadOnlyList<WorkflowEntrypointDescriptor> Entrypoints,
    IReadOnlyList<WorkflowRole> DeclaredRoles,
    string? BindingsJson,
    string? CreationMetadataJson)
{
    public string EntrypointsJson => SerializeEntrypoints(Entrypoints);

    public string DeclaredRolesJson => SerializeDeclaredRoles(DeclaredRoles);

    public static string SerializeEntrypoints(IEnumerable<WorkflowEntrypointDescriptor> entrypoints)
    {
        ArgumentNullException.ThrowIfNull(entrypoints);

        return JsonSerializer.Serialize(entrypoints.Select(entrypoint => entrypoint.Path).ToArray());
    }

    public static string SerializeDeclaredRoles(IEnumerable<WorkflowRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        return JsonSerializer.Serialize(roles.Select(role => role.ToString()).ToArray());
    }
}
