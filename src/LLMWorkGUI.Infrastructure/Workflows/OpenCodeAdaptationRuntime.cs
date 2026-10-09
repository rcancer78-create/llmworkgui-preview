using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Events;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>A dedicated local runtime, not SQL release or productive account identity authority.
/// The caller must durably admit the exact launch before StartAsync; production wiring waits for that journal.</summary>
internal sealed class OpenCodeAdaptationRuntime
{
    private const string CredentialVariable = "LLMWORKGUI_ADAPTATION_API_KEY";
    private readonly OpenCodeServerConnection _connection;
    private readonly HttpClient _http;
    private readonly string _cwd;
    private readonly string _home;
    private readonly string _model;
    private readonly string _provider;
    private readonly string _configHash;
    private readonly string _toolOutputGlob;
    private readonly string _credentialHash;
    private readonly Func<string, string, string, CancellationToken, Task> _admit;
    private readonly OpenCodeOwnedServerLaunch _launch;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private int _attempted;
    private bool _closed;
    private Uri? _endpoint;

    public OpenCodeAdaptationRuntime(string nativeExecutionId, string executable, string scratchRoot,
        string model, string apiKey, StorageOptions storage,
        Func<string, string, string, CancellationToken, Task> admit,
        TimeSpan? startupTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(admit);
        ArgumentNullException.ThrowIfNull(storage);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)
            || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw Refused(); // Never use a shell shim that can silently choose another native binary.
        var prefix = "adaptation-native-";
        if (nativeExecutionId is null || !nativeExecutionId.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(nativeExecutionId[prefix.Length..], "N", out var id)) throw Refused();
        var dataRoot = Path.GetFullPath(storage.AppDataDirectory ?? AppDataPaths.DefaultRootDirectory);
        var expectedRoot = Path.Combine(dataRoot, "scratch", "adaptation-runtime", id.ToString("N"));
        if (!Path.IsPathFullyQualified(scratchRoot) || !Directory.Exists(scratchRoot)
            || !string.Equals(Path.GetFullPath(scratchRoot), expectedRoot, StringComparison.OrdinalIgnoreCase)
            || Directory.EnumerateFileSystemEntries(scratchRoot).Any()) throw Refused();
        InputSanitizer.EnsureNoReparsePoints(Path.GetPathRoot(scratchRoot)!, scratchRoot);
        _home = Path.GetFullPath(scratchRoot);
        _cwd = Path.Combine(_home, "workspace");
        if (Directory.Exists(_cwd) || File.Exists(_cwd)) throw Refused();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 16384 || apiKey.Any(char.IsControl)) throw Refused();
        if (model is null) throw Refused();
        var pieces = model.Split('/', 2);
        if (pieces.Length != 2 || pieces.Any(string.IsNullOrWhiteSpace)
            || pieces[0].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            || model.Length > 512 || model.Any(char.IsControl)) throw Refused();
        _model = model; _provider = pieces[0]; _admit = admit;
        _toolOutputGlob = Path.Combine(scratchRoot, "data", "opencode", "tool-output", "*");
        _credentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        var managed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "opencode");
        if (File.Exists(Path.Combine(managed, "opencode.json")) || File.Exists(Path.Combine(managed, "opencode.jsonc")))
            throw Refused(); // Managed configuration wins over inline config; do not execute its plugins first.
        Directory.CreateDirectory(_cwd);
        var config = JsonSerializer.Serialize(new
        {
            model, small_model = model, enabled_providers = new[] { _provider }, permission = "deny",
            agent = new Dictionary<string, object> { ["plan"] = new { permission = "deny", model } },
            provider = new Dictionary<string, object>
            { [_provider] = new { options = new { apiKey = "{env:" + CredentialVariable + "}" } } },
            plugin = Array.Empty<string>(), mcp = new { }, instructions = Array.Empty<string>(),
            share = "disabled", autoupdate = false, snapshot = false, lsp = false, formatter = false
        });
        _configHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config)));
        var child = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Path.GetDirectoryName(Environment.SystemDirectory)!,
            ["ComSpec"] = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["PATH"] = Environment.SystemDirectory + Path.PathSeparator + Path.GetDirectoryName(executable),
            ["HOME"] = scratchRoot, ["USERPROFILE"] = scratchRoot,
            ["APPDATA"] = Path.Combine(scratchRoot, "roaming"), ["LOCALAPPDATA"] = Path.Combine(scratchRoot, "local"),
            ["XDG_CONFIG_HOME"] = Path.Combine(scratchRoot, "config"), ["XDG_DATA_HOME"] = Path.Combine(scratchRoot, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(scratchRoot, "cache"), ["XDG_STATE_HOME"] = Path.Combine(scratchRoot, "state"),
            ["TEMP"] = Path.Combine(scratchRoot, "temp"), ["TMP"] = Path.Combine(scratchRoot, "temp"),
            ["OPENCODE_CONFIG_DIR"] = Path.Combine(scratchRoot, "config", "opencode"),
            ["OPENCODE_CONFIG_CONTENT"] = config, ["OPENCODE_PERMISSION"] = "{\"*\":\"deny\"}",
            ["OPENCODE_DISABLE_PROJECT_CONFIG"] = "true", ["OPENCODE_DISABLE_CLAUDE_CODE"] = "true",
            ["OPENCODE_DISABLE_AUTOUPDATE"] = "true", ["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true",
            ["OPENCODE_DISABLE_MODELS_FETCH"] = "true", ["OPENCODE_DISABLE_AUTO_COMPACT"] = "true",
            ["OPENCODE_SERVER_USERNAME"] = "opencode",
            ["OPENCODE_SERVER_PASSWORD"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            [CredentialVariable] = apiKey
        };
        foreach (var key in new[] { "HOME", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_DATA_HOME",
            "XDG_CACHE_HOME", "XDG_STATE_HOME", "TEMP", "OPENCODE_CONFIG_DIR" }) Directory.CreateDirectory(child[key]);
        _launch = new OpenCodeOwnedServerLaunch(nativeExecutionId, _cwd, new ReadOnlyDictionary<string, string>(child));
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + child["OPENCODE_SERVER_PASSWORD"])));
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions()), storage);
        var manager = new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
        { CustomExecutablePath = executable, StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30) }),
            supervisor, new NoDiscovery(), _http, ownedLaunch: _launch);
        _connection = new OpenCodeServerConnection(manager);
    }

    public string NativeExecutionId => _launch.ExecutionId;
    public string WorkingDirectory => _cwd;
    public string ConfigurationSha256 => _configHash;
    public Uri? BaseUrl { get; private set; }

    internal OpenCodeClient CreateClient(SqliteAdaptationTransportPolicy policy)
    {
        var endpoint = BaseUrl ?? throw Refused();
        return new OpenCodeClient(_http, endpoint,
            eventStreamService: new OpenCodeEventStreamService(_http, endpoint), adaptationTransportPolicy: policy);
    }

    public async Task StartAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_closed || Interlocked.Exchange(ref _attempted, 1) != 0) throw Refused();
            await _admit(_launch.ExecutionId, _cwd, _configHash, token).ConfigureAwait(false);
            _endpoint = await _connection.GetBaseUrlAsync(token).ConfigureAwait(false);
            await ValidateEffectiveConfigurationAsync(token).ConfigureAwait(false);
            BaseUrl = _endpoint;
        }
        finally { _lifecycle.Release(); }
    }

    // Awaiting this exact owner is necessary for release. Callers must not replace this operation by a bool/DTO.
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _closed = true;
            BaseUrl = null;
            await _connection.DisposeAsync().ConfigureAwait(false);
            _http.Dispose();
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ValidateEffectiveConfigurationAsync(CancellationToken token)
    {
        using var path = await ReadJsonAsync("/path", token).ConfigureAwait(false);
        if (!HasOwnedPaths(path.RootElement, _home)) throw Refused();
        using var config = await ReadJsonAsync("/config", token).ConfigureAwait(false);
        var value = config.RootElement;
        if (!value.TryGetProperty("model", out var model) || model.GetString() != _model
            || !value.TryGetProperty("small_model", out var smallModel) || smallModel.GetString() != _model
            || !value.TryGetProperty("permission", out var permissions) || !AllDenied(permissions)
            || !value.TryGetProperty("agent", out var agents) || !agents.TryGetProperty("plan", out var plan)
            || !plan.TryGetProperty("model", out var planModel) || planModel.GetString() != _model
            || !plan.TryGetProperty("permission", out var planPermissions) || !AllDenied(planPermissions)
            || !value.TryGetProperty("enabled_providers", out var enabled) || enabled.ValueKind != JsonValueKind.Array
            || enabled.GetArrayLength() != 1 || enabled[0].GetString() != _provider
            || !value.TryGetProperty("plugin", out var plugins) || plugins.ValueKind != JsonValueKind.Array || plugins.GetArrayLength() != 0
            || !value.TryGetProperty("mcp", out var mcp) || mcp.ValueKind != JsonValueKind.Object || mcp.EnumerateObject().Any()
            || !HasDisabledAuxiliaryServices(value)) throw Refused();
        if (!value.TryGetProperty("provider", out var providers) || !providers.TryGetProperty(_provider, out var selected)
            || !selected.TryGetProperty("options", out var options) || !options.TryGetProperty("apiKey", out var key)
            || key.ValueKind != JsonValueKind.String
            || Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.GetString()!))) != _credentialHash) throw Refused();
        using var nativeAgents = await ReadJsonAsync("/agent", token).ConfigureAwait(false);
        if (nativeAgents.RootElement.ValueKind != JsonValueKind.Array) throw Refused();
        var nativePlan = nativeAgents.RootElement.EnumerateArray().Where(a => a.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String && name.GetString() == "plan").ToArray();
        if (nativePlan.Length != 1 || !nativePlan[0].TryGetProperty("permission", out var rules)
            || rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() == 0) throw Refused();
        if (!HasOnlyDeniedTools(rules, _toolOutputGlob)) throw Refused();
    }

    internal static bool HasOwnedPaths(JsonElement paths, string home)
    {
        if (paths.ValueKind != JsonValueKind.Object) return false;
        foreach (var expected in new Dictionary<string, string>
        {
            ["home"] = home, ["directory"] = Path.Combine(home, "workspace"),
            ["config"] = Path.Combine(home, "config", "opencode"),
            ["state"] = Path.Combine(home, "state", "opencode")
        })
        {
            if (!paths.TryGetProperty(expected.Key, out var value) || value.ValueKind != JsonValueKind.String
                || !Path.IsPathFullyQualified(value.GetString()!)) return false;
            try
            {
                if (!string.Equals(Path.GetFullPath(value.GetString()!), expected.Value, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }
        return true;
    }

    internal static bool HasDisabledAuxiliaryServices(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("share", out var share)
            || share.ValueKind != JsonValueKind.String || share.GetString() != "disabled") return false;
        foreach (var name in new[] { "autoupdate", "snapshot", "lsp", "formatter" })
            if (!config.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.False) return false;
        return config.TryGetProperty("instructions", out var instructions)
            && instructions.ValueKind == JsonValueKind.Array && instructions.GetArrayLength() == 0;
    }

    internal static bool HasOnlyDeniedTools(JsonElement rules, string ownedOutputGlob)
    {
        if (rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() == 0) return false;
        var globalDeny = -1;
        for (var i = 0; i < rules.GetArrayLength(); i++)
        {
            var rule = rules[i];
            if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("permission", out var permission)
                || permission.ValueKind != JsonValueKind.String || !rule.TryGetProperty("pattern", out var pattern)
                || pattern.ValueKind != JsonValueKind.String || !rule.TryGetProperty("action", out var action)
                || action.ValueKind != JsonValueKind.String) return false;
            if (permission.GetString() == "*" && pattern.GetString() == "*" && action.GetString() == "deny") globalDeny = i;
        }
        if (globalDeny < 0) return false;
        for (var i = globalDeny + 1; i < rules.GetArrayLength(); i++)
        {
            var rule = rules[i]; var action = rule.GetProperty("action").GetString();
            if (action == "deny") continue;
            // Native appends this directory guard after user rules. It grants no read/edit/bash/task permission.
            if (action != "allow" || rule.GetProperty("permission").GetString() != "external_directory"
                || !string.Equals(rule.GetProperty("pattern").GetString()!.Replace('/', '\\'),
                    ownedOutputGlob.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        token = deadline.Token; // ResponseHeadersRead does not bound reading the response body itself.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint!, path));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 200000) throw Refused();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var chunk = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        { if (buffer.Length + read > 200000) throw Refused(); buffer.Write(chunk, 0, read); }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private static bool AllDenied(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() == "deny"
        : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("*", out var wildcard) && wildcard.ValueKind == JsonValueKind.String
            && wildcard.GetString() == "deny" && value.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "deny");
    private static InvalidOperationException Refused() => new("Изолированный runtime адаптации не подтверждён; запуск/передача запрещены.");
    private sealed class NoDiscovery : IOpenCodeDiscoveryService
    {
        public Task<OpenCodeDiscoveryResult> DiscoverAsync(CancellationToken token = default) => throw Refused();
    }
}
