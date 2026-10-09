using System.Text.Json.Nodes;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public class OpenCodeConfigServiceTests
{
    private class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _store = new();

        public Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
        {
            var reference = $"urn:secret:dpapi:{Guid.NewGuid():N}";
            _store[reference] = secret;
            return Task.FromResult(reference);
        }

        public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            _store.TryGetValue(secretReference, out var secret);
            return Task.FromResult(secret);
        }

        public Task<bool> DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_store.Remove(secretReference));
        }
    }

    private readonly OpenCodeConfigService _service = new();

    [Fact]
    public void GenerateProviderConfigJson_GeneratesValidOpenCodeStructure()
    {
        var headers = new[]
        {
            new CustomProviderHeader("X-Custom-Header", "Value123", isSecret: false),
            new CustomProviderHeader("Authorization-Secret", "Bearer SecretToken", isSecret: true)
        };
        var models = new[]
        {
            new CustomProviderModelSettings("custom-gpt4", "Custom GPT-4", new[] { "low", "high" })
        };
        var settings = new CustomProviderSettings(
            "my-provider",
            "My Custom Provider",
            "https://api.custom.com/v1",
            customHeaders: headers,
            models: models);

        var json = _service.GenerateProviderConfigJson(settings, resolvedApiKey: "sk-my-secret-key", redactSecrets: false);

        Assert.NotNull(json);
        var parsed = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(parsed);

        var options = parsed["options"] as JsonObject;
        Assert.NotNull(options);
        Assert.Equal("https://api.custom.com/v1", options["baseURL"]?.GetValue<string>());
        Assert.Equal("sk-my-secret-key", options["apiKey"]?.GetValue<string>());

        var parsedHeaders = options["headers"] as JsonObject;
        Assert.NotNull(parsedHeaders);
        Assert.Equal("Value123", parsedHeaders["X-Custom-Header"]?.GetValue<string>());
        Assert.Equal("Bearer SecretToken", parsedHeaders["Authorization-Secret"]?.GetValue<string>());

        var parsedModels = parsed["models"] as JsonObject;
        Assert.NotNull(parsedModels);
        var model = parsedModels["custom-gpt4"] as JsonObject;
        Assert.NotNull(model);
        Assert.Equal("Custom GPT-4", model["name"]?.GetValue<string>());
        var variants = model["variants"] as JsonObject;
        Assert.NotNull(variants);
        Assert.True(variants["low"]?.GetValue<bool>());
        Assert.True(variants["high"]?.GetValue<bool>());
    }

    [Fact]
    public void GenerateProviderConfigJson_WithRedactSecrets_MasksSensitiveValues()
    {
        var headers = new[]
        {
            new CustomProviderHeader("Public-Header", "PublicValue", isSecret: false),
            new CustomProviderHeader("Secret-Header", "SensitiveSecret", isSecret: true)
        };
        var settings = new CustomProviderSettings(
            "my-provider",
            "My Custom Provider",
            "https://api.custom.com/v1",
            customHeaders: headers);

        var json = _service.GenerateProviderConfigJson(settings, resolvedApiKey: "real-secret-api-key", redactSecrets: true);

        Assert.DoesNotContain("real-secret-api-key", json);
        Assert.DoesNotContain("SensitiveSecret", json);
        Assert.Contains("***REDACTED***", json);
        Assert.Contains("PublicValue", json);
    }

    [Fact]
    public void GeneratePreview_WithoutExistingConfig_ReturnsNewDiffWithAllLinesAdded()
    {
        var settings = new CustomProviderSettings(
            "new-provider",
            "New Provider",
            "http://127.0.0.1:11434/v1");

        var preview = _service.GeneratePreview(settings, existingConfigContent: null, resolvedApiKey: "secret-key");

        Assert.False(preview.HasExistingConfig);
        Assert.True(preview.HasChanges);
        Assert.Contains("***REDACTED***", preview.RedactedJson);
        Assert.Contains("***REDACTED***", preview.DiffText);
        Assert.DoesNotContain("secret-key", preview.RedactedJson);
        Assert.DoesNotContain("secret-key", preview.DiffText);
        Assert.Contains("+", preview.DiffText);
    }

    [Fact]
    public void MergeConfig_PreservesExistingProvidersAndSettings()
    {
        var existingJson = """
        {
          "$schema": "https://opencode.ai/config.json",
          "plugin": ["some-plugin"],
          "provider": {
            "existing-provider": {
              "options": {
                "baseURL": "https://api.existing.com"
              }
            }
          }
        }
        """;

        var settings = new CustomProviderSettings(
            "added-provider",
            "Added Provider",
            "https://api.added.com/v1");

        var merged = _service.MergeConfig(existingJson, settings, resolvedApiKey: "added-key");

        Assert.NotNull(merged);
        var parsed = JsonNode.Parse(merged) as JsonObject;
        Assert.NotNull(parsed);

        Assert.Equal("https://opencode.ai/config.json", parsed["$schema"]?.GetValue<string>());
        Assert.NotNull(parsed["plugin"]);

        var providers = parsed["provider"] as JsonObject;
        Assert.NotNull(providers);

        // Existing provider is preserved
        Assert.NotNull(providers["existing-provider"]);
        var existingOptions = providers["existing-provider"]?["options"] as JsonObject;
        Assert.Equal("https://api.existing.com", existingOptions?["baseURL"]?.GetValue<string>());

        // Added provider is present
        Assert.NotNull(providers["added-provider"]);
        var addedOptions = providers["added-provider"]?["options"] as JsonObject;
        Assert.Equal("https://api.added.com/v1", addedOptions?["baseURL"]?.GetValue<string>());
        Assert.Equal("added-key", addedOptions?["apiKey"]?.GetValue<string>());
    }

    [Fact]
    public void GeneratePreview_WithExistingConfig_MasksSecretsInDiffAndJson()
    {
        var existingJson = """
        {
          "$schema": "https://opencode.ai/config.json",
          "provider": {}
        }
        """;

        var settings = new CustomProviderSettings(
            "new-provider",
            "New Provider",
            "https://api.custom.com/v1");

        var preview = _service.GeneratePreview(settings, existingJson, resolvedApiKey: "super-secret-key-12345");

        Assert.True(preview.HasExistingConfig);
        Assert.True(preview.HasChanges);
        Assert.DoesNotContain("super-secret-key-12345", preview.RedactedJson);
        Assert.DoesNotContain("super-secret-key-12345", preview.DiffText);
        Assert.Contains("***REDACTED***", preview.RedactedJson);
        Assert.Contains("***REDACTED***", preview.DiffText);
        Assert.Contains("super-secret-key-12345", preview.GeneratedJson);
    }

    [Fact]
    public void CustomProviderHeader_ThrowsOnInvalidNames()
    {
        Assert.Throws<ArgumentException>(() => new CustomProviderHeader("", "value"));
        Assert.Throws<ArgumentException>(() => new CustomProviderHeader("   ", "value"));
        Assert.Throws<ArgumentException>(() => new CustomProviderHeader("Header Name With Spaces", "value"));
        Assert.Throws<ArgumentException>(() => new CustomProviderHeader("Header:Colon", "value"));
    }

    [Fact]
    public async Task CreateUnboundApiKeyReferenceAsync_PersistsSecretAndReturnsUrn()
    {
        var secretStore = new FakeSecretStore();

        var reference = await _service.CreateUnboundApiKeyReferenceAsync("secret-api-token-999", secretStore);

        Assert.NotNull(reference);
        Assert.StartsWith("urn:secret:dpapi:", reference);

        var retrieved = await secretStore.GetSecretAsync(reference);
        Assert.Equal("secret-api-token-999", retrieved);
    }
}
