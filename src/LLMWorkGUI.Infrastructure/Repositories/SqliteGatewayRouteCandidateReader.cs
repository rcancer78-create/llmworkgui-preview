using LLMWorkGUI.Application.ReviewerIdentity;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

/// <summary>
/// Reads the whole route library for the diagnostic resolver. It writes nothing, and nothing composes it.
/// <para>
/// The four rows a route depends on are read in one statement and carried as one candidate, because the
/// cross-links between them are not foreign keys: <c>Accounts.ProviderProfileId</c> and
/// <c>Models.ProviderProfileId</c> are each checked against their own profile, not against the route that
/// selects them, so a row can name an account belonging to a different provider and the database will not
/// object. The resolver judges that, and this reader's job is to hand it the facts unaltered.
/// </para>
/// <para>
/// Two accounts are reported per row: one count from <c>Routes</c> itself, so a row whose provider,
/// account or model is missing shows up as a shortfall rather than disappearing, and the joined set. The
/// join is an inner one, so a route whose identity is not fully present has no candidate at all; that is
/// not a claim that the row does not exist, and the count is what makes the difference visible.
/// </para>
/// <para>
/// Capability columns on <c>Models</c> are read but not interpreted: they live in <c>ModelCapabilities</c>
/// and in columns this reader has no use for, so it passes the persisted values straight to the domain and
/// reports no capability list at all. A capability list invented here would be a fabrication about a model
/// this reader knows nothing beyond its identity.
/// </para>
/// </summary>
public sealed class SqliteGatewayRouteCandidateReader : IGatewayRouteCandidateReader
{
    private const string SelectCandidatesSql = """
        SELECT
            r.Id, r.Backend, r.ProviderProfileId, r.AccountId, r.ModelId,
            r.ReasoningEffort, r.SpeedMode, r.ExecutionMode,
            r.MaxDataClass, r.IsEnabled, r.Health, r.ManualPriority, r.GatewayRouteKey,
            p.Id, p.DisplayName, p.Backend, p.BaseUrl, p.ExecutablePath, p.MaxDataClass, p.IsEnabled, p.GatewayNativeId,
            a.Id, a.ProviderProfileId, a.DisplayName, a.ProviderNativeId, a.AuthState, a.ManualPriority, a.IsEnabled,
            a.Health, a.CooldownUntilUtc, a.DisabledUntilUtc, a.MaxConcurrentExecutions, a.ReserveThreshold,
            a.SecretReference, a.GatewayNativeId,
            m.Id, m.Backend, m.ProviderProfileId, m.ProviderModelId, m.DisplayName, m.CapabilityState, m.Provenance,
            m.IsEnabled, m.Health, m.ContextLimit, m.SupportsTools, m.SupportsAttachments, m.DiscoveredAtUtc,
            m.GatewayNativeId
        FROM Routes r
        JOIN ProviderProfiles p ON p.Id = r.ProviderProfileId
        JOIN Accounts a ON a.Id = r.AccountId
        JOIN Models m ON m.Id = r.ModelId
        ORDER BY r.Id;
        """;

    private const string CountRoutesSql = "SELECT COUNT(*) FROM Routes;";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteGatewayRouteCandidateReader(ISqliteConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<GatewayRouteCandidateSnapshot> ReadCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using var transaction = connection.BeginTransaction(deferred: true);
        var candidates = new List<GatewayRouteCandidate>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SelectCandidatesSql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(ReadCandidate(reader));
            }
        }

        var routeCount = await CountRoutesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return new GatewayRouteCandidateSnapshot(routeCount, candidates);
    }

    private static async Task<int> CountRoutesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CountRoutesSql;

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt32(
            scalar,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static GatewayRouteCandidate ReadCandidate(SqliteDataReader reader)
    {
        // Each row's own id and its own ProviderProfileId are read from that row, never from the route that
        // joined to it. The join is what makes them equal for a well-formed library; substituting the route's
        // value would make an incoherent one look coherent, and the resolver's coherence check is the only
        // thing that ever notices.
        var route = new Route(
            reader.GetString(0),
            new SessionBinding(
                ParseBackend(reader.GetString(1), "Routes", "Backend"),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                SqliteRepositorySupport.GetNullableString(reader, 5),
                SqliteRepositorySupport.GetNullableString(reader, 6),
                SqliteRepositorySupport.GetNullableString(reader, 7)),
            ParseEnum<DataClassification>(reader.GetString(8), "Routes", "MaxDataClass"),
            reader.GetInt64(9) != 0,
            ParseEnum<HealthState>(reader.GetString(10), "Routes", "Health"),
            checked((int)reader.GetInt64(11)),
            SqliteRepositorySupport.GetNullableString(reader, 12));

        var profile = new ProviderProfile(
            reader.GetString(13),
            reader.GetString(14),
            ParseBackend(reader.GetString(15), "ProviderProfiles", "Backend"),
            SqliteRepositorySupport.GetNullableString(reader, 16),
            SqliteRepositorySupport.GetNullableString(reader, 17),
            ParseEnum<DataClassification>(reader.GetString(18), "ProviderProfiles", "MaxDataClass"),
            reader.GetInt64(19) != 0,
            SqliteRepositorySupport.GetNullableString(reader, 20));

        var account = new Account(
            reader.GetString(21),
            reader.GetString(22),
            reader.GetString(23),
            SqliteRepositorySupport.GetNullableString(reader, 24),
            ParseEnum<AuthState>(reader.GetString(25), "Accounts", "AuthState"),
            checked((int)reader.GetInt64(26)),
            reader.GetInt64(27) != 0,
            ParseEnum<HealthState>(reader.GetString(28), "Accounts", "Health"),
            SqliteRepositorySupport.ParseNullableTimestamp(SqliteRepositorySupport.GetNullableString(reader, 29)),
            SqliteRepositorySupport.ParseNullableTimestamp(SqliteRepositorySupport.GetNullableString(reader, 30)),
            checked((int)reader.GetInt64(31)),
            reader.IsDBNull(32) ? null : reader.GetDouble(32),
            sessionBindings: null,
            secretReference: SqliteRepositorySupport.GetNullableString(reader, 33),
            gatewayNativeId: SqliteRepositorySupport.GetNullableString(reader, 34));

        var model = new ModelDescriptor(
            reader.GetString(35),
            ParseBackend(reader.GetString(36), "Models", "Backend"),
            reader.GetString(37),
            reader.GetString(38),
            reader.GetString(39),
            availableAccountIds: Array.Empty<string>(),
            supportedReasoningEfforts: Array.Empty<string>(),
            supportedSpeedModes: Array.Empty<string>(),
            supportedModes: Array.Empty<string>(),
            ParseEnum<CapabilityState>(reader.GetString(40), "Models", "CapabilityState"),
            ParseEnum<ModelProvenance>(reader.GetString(41), "Models", "Provenance"),
            reader.GetInt64(42) != 0,
            ParseEnum<HealthState>(reader.GetString(43), "Models", "Health"),
            reader.IsDBNull(44) ? null : checked((int)reader.GetInt64(44)),
            reader.GetInt64(45) != 0,
            reader.GetInt64(46) != 0,
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(47)),
            SqliteRepositorySupport.GetNullableString(reader, 48));

        return new GatewayRouteCandidate(route, profile, account, model);
    }

    /// <summary>
    /// The backend by its persisted name only, and only when this build knows that name.
    /// <para>
    /// The same rule the by-id route reader applies, and for the same reason: an unknown value used to be
    /// read as OpenCode, which re-pointed a row written by another build at the one backend this application
    /// happens to have, with no trace of the substitution in the candidate, the report or the refusal. A read
    /// that cannot say which backend a row selects does not return a partial answer at all.
    /// </para>
    /// </summary>
    private static BackendType ParseBackend(string value, string table, string column) =>
        Enum.TryParse<BackendType>(value, ignoreCase: false, out var backend) && Enum.IsDefined(backend)
            ? backend
            : throw new InvalidDataException(
                $"{table}.{column} value '{value}' is not a backend this build knows, so the route library is "
                    + "not read at all rather than read as a different backend.");

    /// <summary>
    /// A persisted enum by name only. The column, not just the value, is named in the failure because a
    /// health value this build does not recognise is the difference between "unhealthy" and "unreadable",
    /// and only the column says which one was found.
    /// </summary>
    private static TEnum ParseEnum<TEnum>(string value, string table, string column)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException(
                $"{table}.{column} value '{value}' is not a {typeof(TEnum).Name} this build knows, so the route "
                    + "library is not read at all rather than read with that row's state guessed.");
}
