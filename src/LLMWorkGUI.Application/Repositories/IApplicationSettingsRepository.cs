namespace LLMWorkGUI.Application.Repositories;

public interface IApplicationSettingsRepository
{
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    Task SetValueAsync(
        string key,
        string value,
        string valueType = "String",
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default);
}
