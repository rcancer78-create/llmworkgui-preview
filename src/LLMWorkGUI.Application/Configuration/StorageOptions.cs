namespace LLMWorkGUI.Application.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public const string DefaultDatabaseFileName = "llmworkgui.db";

    public string? AppDataDirectory { get; set; }

    public string DatabaseFileName { get; set; } = DefaultDatabaseFileName;
}
