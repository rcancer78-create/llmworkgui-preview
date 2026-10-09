using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Observability;

public static class WorkflowRoleParser
{
    public static WorkflowRole Parse(string? value) =>
        TryParse(value, out var role) ? role : WorkflowRole.Unknown;

    public static bool TryParse(string? value, out WorkflowRole role)
    {
        role = WorkflowRole.Unknown;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();

        foreach (var definedRole in Enum.GetValues<WorkflowRole>())
        {
            if (string.Equals(definedRole.ToString(), candidate, StringComparison.OrdinalIgnoreCase))
            {
                role = definedRole;
                return true;
            }
        }

        return false;
    }
}
