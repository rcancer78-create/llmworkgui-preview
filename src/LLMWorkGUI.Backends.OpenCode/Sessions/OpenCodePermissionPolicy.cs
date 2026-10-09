using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Backends.OpenCode.Sessions;

/// <summary>Documented native tool meanings, not approval rules or automatic grants.</summary>
public static class OpenCodePermissionPolicy
{
    // https://opencode.ai/docs/permissions/#available-permissions
    // Unknown/custom names retain their native description and require an explicit decision.
    public static NormalizedApprovalKind Normalize(string? nativeKind) => nativeKind switch
    {
        "read" or "glob" or "grep" => NormalizedApprovalKind.ReadFile,
        "edit" => NormalizedApprovalKind.WriteFile,
        "bash" => NormalizedApprovalKind.ShellCommand,
        "webfetch" or "websearch" => NormalizedApprovalKind.NetworkTool,
        "external_directory" => NormalizedApprovalKind.WorkspaceExpansion,
        _ => NormalizedApprovalKind.UnknownHighRisk
    };

    public static string Explain(NormalizedApprovalKind kind) => kind switch
    {
        NormalizedApprovalKind.ReadFile => "Чтение или поиск файлов. Проверьте исходный запрос и пути перед разовым разрешением.",
        NormalizedApprovalKind.WriteFile => "Запись или изменение файлов. Разрешение относится только к этому запросу.",
        NormalizedApprovalKind.ShellCommand => "Выполнение команды оболочки. Проверьте команду и её последствия.",
        NormalizedApprovalKind.NetworkTool => "Обращение к сети. Проверьте адрес и передаваемые данные.",
        NormalizedApprovalKind.WorkspaceExpansion => "Доступ за пределами рабочего каталога. Проверьте запрошенные пути.",
        _ => "UnknownHighRisk: операция не имеет подтверждённого соответствия. Отклоните её или явно разрешите только этот запрос."
    };
}
