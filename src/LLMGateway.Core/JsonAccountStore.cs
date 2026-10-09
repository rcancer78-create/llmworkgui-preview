using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMGateway.Core;

public sealed partial class JsonAccountStore : IAccountStore
{
    private const int FormatVersion = 2;
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly List<AccountProfile> _accounts;
    private readonly bool _persistWrites;
    private string? _persistedRevision;
    private readonly bool _readOnly;
    private readonly string? _persistenceWarning;

    private JsonAccountStore(string filePath, List<AccountProfile> accounts, ILogger logger, string? persistedRevision, bool readOnly = false, string? persistenceWarning = null, bool persistWrites = true)
    {
        _filePath = filePath;
        _accounts = accounts;
        _logger = logger;
        _persistedRevision = persistedRevision;
        _readOnly = readOnly;
        _persistenceWarning = persistenceWarning;
        _persistWrites = persistWrites;
    }

    public string FilePath => _filePath;
    public bool IsReadOnly => _readOnly;
    public string? PersistenceWarning => _persistenceWarning;

    public static JsonAccountStore Load(GatewayOptions options, IEnumerable<IProviderAdapter> adapters, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var path = options.ResolveAccountsFile();
        var accounts = ReadFile(path, logger, out var migrated, out var revision, out var corrupt, out var warning);
        if (corrupt) return new JsonAccountStore(path, [], logger, revision, readOnly: true, persistenceWarning: warning);
        var adapterList = adapters.ToList();
        var changed = migrated || accounts.Count == 0;
        foreach (var account in accounts)
        {
            if (account is null || !Enum.IsDefined(account.Provider))
                throw GatewayException.Invalid("Файл аккаунтов содержит неизвестного провайдера или некорректную запись.");
            if (account.Environment is null) { account.Environment = new(StringComparer.OrdinalIgnoreCase); changed = true; }
            if (account.ExtraArguments is null) { account.ExtraArguments = []; changed = true; }
            // Refuse malformed collection shapes before secret filtering or native use.
            account.Freeze();
            if (AccountEnvironment.RemoveSecrets(account)) changed = true;
            var displayName = account.DisplayName;
            Validate(account);
            if (!string.Equals(displayName, account.DisplayName, StringComparison.Ordinal)) changed = true;
        }
        ValidateUniqueIds(accounts);
        // Optional providers do not create a default profile without a registered adapter.
        foreach (var provider in Enum.GetValues<ProviderKind>().Where(provider => provider != ProviderKind.Unknown
            && (provider != ProviderKind.GrokBot || adapterList.Any(adapter => adapter.Provider == provider))))
        {
            if (accounts.Any(a => a.Provider == provider)) continue;
            var generated = CreateDefault(provider);
            var preferredId = generated.Id;
            var suffix = 2;
            while (accounts.Any(account => account.Id.Equals(generated.Id, StringComparison.OrdinalIgnoreCase)))
                generated.Id = preferredId + "-" + (suffix++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            accounts.Add(generated);
            changed = true;
        }
        if (options.DiscoverProfiles)
        {
            foreach (var candidate in adapterList.SelectMany(SafeDiscover))
            {
                if (candidate is null) throw GatewayException.Invalid("Обнаружен некорректный профиль аккаунта.");
                var discovered = candidate.Freeze();
                if (!IsValidId(discovered.Id)) continue;
                AccountEnvironment.RemoveSecrets(discovered);
                Validate(discovered);
                var duplicate = accounts.Any(a => a.Id.Equals(discovered.Id, StringComparison.OrdinalIgnoreCase)
                    || a.Provider == discovered.Provider && SamePath(a.ConfigDirectory, discovered.ConfigDirectory));
                if (duplicate) continue;
                accounts.Add(discovered);
                changed = true;
            }
        }
        ValidateUniqueIds(accounts);
        changed |= Normalize(accounts);
        var store = new JsonAccountStore(path, accounts, logger, revision);
        if (changed) store.SaveAsync(CancellationToken.None).GetAwaiter().GetResult();
        return store;

        IEnumerable<AccountProfile> SafeDiscover(IProviderAdapter adapter)
        {
            try { return adapter.DiscoverProfiles().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Profile discovery failed for {Provider} ({ExceptionType})", adapter.Provider, ex.GetType().Name);
                return [];
            }
        }
    }

    /// <summary>Creates a memory-only store. An explicit filePath opts into durable writes after mutation.</summary>
    public static JsonAccountStore InMemory(IEnumerable<AccountProfile> accounts, string? filePath = null)
    {
        if (accounts is null) throw GatewayException.Invalid("Конфигурация аккаунтов отсутствует.");
        var list = accounts.Select(account =>
        {
            if (account is null) throw GatewayException.Invalid("Конфигурация содержит некорректный профиль аккаунта.");
            var frozen = account.Freeze();
            Validate(frozen);
            return frozen;
        }).ToList();
        ValidateUniqueIds(list);
        Normalize(list);
        var path = filePath ?? Path.Combine(Path.GetTempPath(), $"llmgateway-{Guid.NewGuid():N}.json");
        return new JsonAccountStore(path, list, NullLogger.Instance,
            filePath is null ? null : ReadRevision(path), persistWrites: filePath is not null);
    }

    public static AccountProfile CreateDefault(ProviderKind provider) => new()
    {
        Id = provider.ToString().ToLowerInvariant() + "-default",
        DisplayName = provider + " (по умолчанию)",
        Provider = provider
    };

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    public IReadOnlyList<AccountProfile> GetAll()
    {
        lock (_gate) return _accounts.Select(a => a.Clone()).ToArray();
    }

    public AccountProfile? Find(string accountId)
    {
        lock (_gate) return _accounts.FirstOrDefault(a => a.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase))?.Clone();
    }

    public async Task AddAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile = profile.Freeze();
        Validate(profile);
        await MutateAsync(accounts =>
        {
            if (accounts.Any(a => a.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)))
                throw new GatewayException(GatewayErrorKind.InvalidRequest, $"Аккаунт '{profile.Id}' уже существует.");
            var copy = profile.Clone();
            if (!accounts.Any(a => a.Provider == copy.Provider)) copy.IsActive = true;
            if (copy.IsActive) Deactivate(accounts, copy.Provider);
            accounts.Add(copy);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(AccountProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile = profile.Freeze();
        Validate(profile);
        await MutateAsync(accounts =>
        {
            var index = accounts.FindIndex(a => a.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new GatewayException(GatewayErrorKind.NotFound, $"Аккаунт '{profile.Id}' не найден.");
            var copy = profile.Clone();
            copy.IsActive = accounts[index].IsActive && accounts[index].Provider == copy.Provider;
            accounts[index] = copy;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await MutateAsync(accounts =>
        {
            var removed = accounts.RemoveAll(a => a.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) throw new GatewayException(GatewayErrorKind.NotFound, $"Аккаунт '{accountId}' не найден.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SelectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await MutateAsync(accounts =>
        {
            var account = accounts.FirstOrDefault(a => a.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                ?? throw new GatewayException(GatewayErrorKind.NotFound, $"Аккаунт '{accountId}' не найден.");
            if (!account.Enabled) throw GatewayException.Invalid("Отключённый аккаунт нельзя выбрать активным.");
            Deactivate(accounts, account.Provider);
            account.IsActive = true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void Deactivate(IEnumerable<AccountProfile> accounts, ProviderKind provider)
    {
        foreach (var item in accounts.Where(a => a.Provider == provider)) item.IsActive = false;
    }

    private async Task MutateAsync(Action<List<AccountProfile>> mutation, CancellationToken cancellationToken)
    {
        if (_readOnly) throw new GatewayException(GatewayErrorKind.Upstream, _persistenceWarning ?? "Account configuration is read-only after a load error.");
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AccountProfile> snapshot;
            lock (_gate) snapshot = _accounts.Select(a => a.Clone()).ToList();
            mutation(snapshot);
            Normalize(snapshot);
            await SaveSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _accounts.Clear();
                _accounts.AddRange(snapshot);
            }
        }
        finally { _saveGate.Release(); }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_readOnly) throw new GatewayException(GatewayErrorKind.Upstream, _persistenceWarning ?? "Account configuration is read-only after a load error.");
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AccountProfile[] snapshot;
            lock (_gate) snapshot = _accounts.Select(a => a.Clone()).ToArray();
            await SaveSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally { _saveGate.Release(); }
    }

    private async Task SaveSnapshotAsync(IReadOnlyCollection<AccountProfile> snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_persistWrites) return;
        string? temp = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            // The short OS lease serializes revision validation and publication across stores/processes.
            // Keep the lock file: removing its pathname could create two independent live leases.
            using var lease = new FileStream(_filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!string.Equals(ReadRevision(_filePath), _persistedRevision, StringComparison.Ordinal))
                throw new GatewayException(GatewayErrorKind.Upstream,
                    "Файл аккаунтов изменён другим владельцем; загрузите актуальную конфигурацию перед повтором.");
            temp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var document = new StoredAccounts { Version = FormatVersion, Accounts = [.. snapshot] };
            var options = new JsonSerializerOptions(GatewayJson.Options) { WriteIndented = true };
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, document, options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            var revision = ReadRevision(temp);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, _filePath, overwrite: true);
            // No fallible I/O follows publication: the in-memory snapshot can now commit atomically.
            _persistedRevision = revision;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Failed to save account configuration ({ExceptionType})", ex.GetType().Name);
            throw new GatewayException(GatewayErrorKind.Upstream, "Не удалось сохранить конфигурацию аккаунтов.", ex);
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Owned account snapshot cleanup remains unconfirmed ({ExceptionType})", ex.GetType().Name);
                }
            }
        }
    }

    private static void ValidateUniqueIds(IEnumerable<AccountProfile> accounts)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
            if (!ids.Add(account.Id))
                throw GatewayException.Invalid("Конфигурация содержит повторяющиеся идентификаторы аккаунтов.");
    }

    private static string? ReadRevision(string path) => File.Exists(path)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;

    private static void Validate(AccountProfile profile)
    {
        if (!Enum.IsDefined(profile.Provider) || profile.Provider == ProviderKind.Unknown || !Enum.IsDefined(profile.AuthMode))
            throw GatewayException.Invalid("Профиль содержит неизвестного провайдера или режим авторизации.");
        if (!IsValidId(profile.Id))
            throw GatewayException.Invalid("Id аккаунта: 1-64 символа, латиница, цифры, '.', '_' или '-', без '/'.");
        if (profile.AuthMode == AccountAuthMode.ApiKeyFromEnvironment && string.IsNullOrWhiteSpace(profile.ApiKeyVariable))
            throw GatewayException.Invalid("Для режима ApiKeyFromEnvironment укажите имя переменной окружения с ключом.");
        if (profile.ApiKeyVariable is { Length: > 0 } variable && !EnvNamePattern().IsMatch(variable))
            throw GatewayException.Invalid("Имя переменной окружения содержит недопустимые символы.");
        if (profile.Environment.Any(pair => AccountEnvironment.IsSecret(pair.Key, pair.Value)))
            throw GatewayException.Invalid("Профиль не хранит секреты. Укажите имя переменной в api_key_variable; значение ключа в environment не записывается.");
        if (profile.ExtraArguments.Any(a => a.Contains('\n') || a.Contains('\r')))
            throw GatewayException.Invalid("Дополнительные аргументы не должны содержать переводы строк.");
        CliArguments.RejectUnsafe(profile.ExtraArguments);
        if (string.IsNullOrWhiteSpace(profile.DisplayName)) profile.DisplayName = profile.Id;
    }


    private static bool Normalize(List<AccountProfile> accounts)
    {
        var changed = false;
        foreach (var group in accounts.GroupBy(a => a.Provider))
        {
            var active = group.FirstOrDefault(a => a.IsActive && a.Enabled) ?? group.FirstOrDefault(a => a.Enabled) ?? group.First();
            foreach (var account in group)
            {
                var isActive = ReferenceEquals(account, active);
                changed |= account.IsActive != isActive;
                account.IsActive = isActive;
            }
        }
        return changed;
    }

    private static List<AccountProfile> ReadFile(string path, ILogger logger, out bool migrated, out string? revision, out bool corrupt, out string? warning)
    {
        migrated = false;
        revision = null;
        corrupt = false;
        warning = null;
        if (!File.Exists(path)) return [];
        try
        {
            var bytes = File.ReadAllBytes(path);
            revision = Convert.ToHexString(SHA256.HashData(bytes));
            using var source = new MemoryStream(bytes);
            using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var node = JsonNode.Parse(reader.ReadToEnd());
            migrated = node is JsonArray;
            if (node is JsonObject document)
            {
                // A syntactically valid but unknown document must not become an empty
                // snapshot: defaults would then overwrite its original contents.
                if (Find(document, "version") is not JsonValue versionNode
                    || versionNode.GetValueKind() != JsonValueKind.Number
                    || !versionNode.TryGetValue<int>(out var version))
                    throw GatewayException.Invalid("Некорректная структура файла аккаунтов; исходный файл не изменён.");
                if (version != FormatVersion)
                    throw new GatewayException(GatewayErrorKind.Unsupported, "Версия файла аккаунтов не поддерживается; исходный файл не изменён.");
                if (Find(document, "accounts") is not JsonArray)
                    throw GatewayException.Invalid("Некорректная структура файла аккаунтов; исходный файл не изменён.");
                var stored = node.Deserialize<StoredAccounts>(GatewayJson.Options);
                return stored!.Accounts;
            }
            if (node is JsonArray legacy)
            {
                var result = MigrateLegacy(legacy);
                ValidateUniqueIds(result);
                var backup = path + $".v1-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
                File.Copy(path, backup, overwrite: false);
                if (!string.Equals(ReadRevision(backup), revision, StringComparison.Ordinal))
                    throw new IOException("Legacy account configuration changed before backup; migration was not published.");
                logger.LogInformation("Legacy account configuration backed up before migration.");
                return result;
            }
            throw GatewayException.Invalid("Некорректная структура файла аккаунтов; исходный файл не изменён.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            corrupt = true;
            var backupSaved = false;
            try
            {
                File.Copy(path, path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}", overwrite: false);
                backupSaved = true;
            }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
            {
                logger.LogError("Account recovery copy failed ({ExceptionType}); original file remains unchanged.", copyError.GetType().Name);
            }
            logger.LogError("Account configuration is malformed; original file remains unchanged ({ExceptionType}).", ex.GetType().Name);
            warning = backupSaved
                ? "Account configuration is malformed and read-only; original file and a recovery copy were preserved. Fix the file and restart."
                : "Account configuration is malformed and read-only; original file was preserved, but recovery copy failed. Fix the file and restart.";
            return [];
        }
    }

    /// <summary>Converts the v1 array format (NativeAccountOptions) without losing account ids.</summary>
    private static List<AccountProfile> MigrateLegacy(JsonArray legacy)
    {
        var result = new List<AccountProfile>();
        foreach (var node in legacy)
        {
            if (node is not JsonObject item)
                throw new JsonException("Legacy account entry must be an object; the original file was not changed.");
            var id = Get(item, "id") ?? string.Empty;
            if (!IsValidId(id) || !TryProvider(Find(item, "provider"), out var provider) || provider == ProviderKind.Unknown)
                throw new JsonException("Legacy account entry has an invalid identity or provider; the original file was not changed.");
            var profile = new AccountProfile
            {
                Id = id,
                DisplayName = Get(item, "displayName") ?? id,
                Provider = provider,
                WorkingDirectory = Get(item, "workingDirectory"),
                IsActive = Find(item, "isActive")?.GetValueKind() == JsonValueKind.True
            };
            var executable = Get(item, "executable");
            if (!string.IsNullOrWhiteSpace(executable) && !IsDefaultExecutable(provider, executable)) profile.Executable = executable;
            if (Find(item, "environment") is JsonObject environment)
            {
                foreach (var pair in environment)
                {
                    var value = pair.Value?.GetValueKind() == JsonValueKind.String ? pair.Value.GetValue<string>() : null;
                    if (value is null) continue;
                    if (pair.Key.Equals("CODEX_HOME", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase))
                        profile.ConfigDirectory = value;
                    else
                        profile.Environment[pair.Key] = value;
                }
            }
            result.Add(profile);
        }
        return result;

        static string? Get(JsonObject obj, string name) =>
            Find(obj, name) is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    private static JsonNode? Find(JsonObject obj, string name) =>
        obj.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool TryProvider(JsonNode? node, out ProviderKind provider)
    {
        provider = default;
        if (node is not JsonValue value) return false;
        if (value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number) && Enum.IsDefined(typeof(ProviderKind), number))
        {
            provider = (ProviderKind)number;
            return true;
        }
        return value.GetValueKind() == JsonValueKind.String && Enum.TryParse(value.GetValue<string>(), true, out provider)
            && Enum.IsDefined(provider);
    }

    private static bool IsDefaultExecutable(ProviderKind provider, string executable) => provider switch
    {
        ProviderKind.Codex => executable.Equals("codex", StringComparison.OrdinalIgnoreCase),
        ProviderKind.Cursor => executable.Equals("cursor-agent", StringComparison.OrdinalIgnoreCase),
        ProviderKind.Antigravity => executable.Equals("agy", StringComparison.OrdinalIgnoreCase),
        ProviderKind.Grok => executable.Equals("grok", StringComparison.OrdinalIgnoreCase),
        ProviderKind.Claude => executable.Equals("claude", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex EnvNamePattern();

    private sealed class StoredAccounts
    {
        public int Version { get; set; }
        public List<AccountProfile> Accounts { get; set; } = [];
    }
}
