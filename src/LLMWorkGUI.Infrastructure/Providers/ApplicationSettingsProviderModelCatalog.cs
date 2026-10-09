using System.Text.Json;
using System.Text.Json.Serialization;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Infrastructure.Security;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>
/// Local, sanitized store of the backend model ids a provider profile is configured with, backed by
/// the application settings table. The same <see cref="CustomProviderModelSettings"/> list the provider
/// editor confirms is what is written here, so the model ids are exactly the ones the local provider
/// configuration declares — never a discovered or invented one.
/// <para>
/// Only the model id and its display name are persisted, and both are re-checked against
/// <see cref="BackendModelIdPolicy"/> and the <see cref="SensitiveDataFilter"/> on read. A value that
/// cannot pass is dropped: a home path, a profile name, a secret URN or a blank entry is never
/// returned as a model.
/// </para>
/// </summary>
public sealed class ApplicationSettingsProviderModelCatalog : IProviderModelCatalogSource, IProviderModelCatalogWriter
{
    private const string KeyPrefix = "provider.models.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IApplicationSettingsRepository _settingsRepository;
    private readonly SensitiveDataFilter _sensitiveDataFilter;
    private readonly LLMWorkGUI.Infrastructure.Data.ISqliteConnectionFactory? _configurationFactory;

    public ApplicationSettingsProviderModelCatalog(
        IApplicationSettingsRepository settingsRepository,
        SensitiveDataFilter? sensitiveDataFilter = null,
        LLMWorkGUI.Infrastructure.Data.ISqliteConnectionFactory? configurationFactory = null)
    {
        ArgumentNullException.ThrowIfNull(settingsRepository);

        _settingsRepository = settingsRepository;
        _sensitiveDataFilter = sensitiveDataFilter ?? new SensitiveDataFilter();
        _configurationFactory = configurationFactory;
    }

    public async Task<IReadOnlyList<ProviderModelDescriptor>> ListModelsAsync(
        ProviderModelCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var stored = await _settingsRepository
            .GetValueAsync(BuildKey(query), cancellationToken)
            .ConfigureAwait(false);

        var configured = Parse(stored).ToDictionary(model => model.ModelId, StringComparer.Ordinal);
        if (_configurationFactory is not null)
        {
            await using var connection = await _configurationFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.ProviderModelId,m.DisplayName,m.IsEnabled,m.CapabilityState
                FROM Models m JOIN ProviderProfiles p ON p.Id=m.ProviderProfileId AND p.Backend=m.Backend
                WHERE m.ProviderProfileId=$profile AND m.Backend=$backend
                """;
            command.Parameters.AddWithValue("$profile", query.ProviderProfileId);
            command.Parameters.AddWithValue("$backend", query.Backend.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                // Explicit saved configuration overrides legacy discovered/settings entries.
                configured.Remove(id);
                if (reader.GetBoolean(2) && reader.GetString(3) == "Supported")
                    foreach (var model in Normalize(new[] { ((string?)id, (string?)reader.GetString(1)) })) configured[model.ModelId] = model;
            }
        }
        return configured.Values.ToArray();
    }

    public async Task SaveModelsAsync(
        ProviderModelCatalogQuery query,
        IReadOnlyList<ProviderModelDescriptor> models,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(models);

        var accepted = Normalize(models
            .Select(model => ((string?)model.ModelId, (string?)model.DisplayName)));

        if (accepted.Count == 0)
        {
            await ClearModelsAsync(query, cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = JsonSerializer.Serialize(accepted, JsonOptions);

        await _settingsRepository
            .SetValueAsync(BuildKey(query), payload, "Json", cancellationToken)
            .ConfigureAwait(false);
    }

    public Task ClearModelsAsync(ProviderModelCatalogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return _settingsRepository.RemoveAsync(BuildKey(query), cancellationToken);
    }

    private static string BuildKey(ProviderModelCatalogQuery query) =>
        string.Concat(KeyPrefix, query.Backend.ToString(), ".", query.ProviderProfileId);

    private IReadOnlyList<ProviderModelDescriptor> Normalize(
        IEnumerable<(string? ModelId, string? DisplayName)> candidates)
    {
        var accepted = new List<ProviderModelDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (!SanitizedModelId.TryNormalize(_sensitiveDataFilter, candidate.ModelId, out var modelId)
                || !seen.Add(modelId))
            {
                continue;
            }

            var redactedDisplayName = _sensitiveDataFilter.Redact(candidate.DisplayName);

            accepted.Add(new ProviderModelDescriptor(
                modelId,
                string.IsNullOrWhiteSpace(redactedDisplayName) ? modelId : redactedDisplayName));
        }

        return accepted;
    }

    private IReadOnlyList<ProviderModelDescriptor> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return Array.Empty<ProviderModelDescriptor>();
        }

        IReadOnlyList<StoredModel>? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<IReadOnlyList<StoredModel>>(stored, JsonOptions);
        }
        catch (JsonException)
        {
            return Array.Empty<ProviderModelDescriptor>();
        }

        if (parsed is null)
        {
            return Array.Empty<ProviderModelDescriptor>();
        }

        return Normalize(parsed
            .Where(entry => entry is not null)
            .Select(entry => ((string?)entry!.ModelId, entry.DisplayName)));
    }

    private sealed record StoredModel
    {
        [JsonPropertyName("modelId")]
        public string? ModelId { get; init; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }
    }
}
