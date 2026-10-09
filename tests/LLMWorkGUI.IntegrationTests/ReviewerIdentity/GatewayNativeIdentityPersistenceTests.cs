using System.Globalization;
using LLMWorkGUI.Application.ReviewerIdentity;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.ReviewerIdentity;

/// <summary>
/// Migration 012 and the diagnostic reader, against a real migrated database.
/// <para>
/// The tests seed SQL directly, because there is no writer for these columns and adding one purely so a
/// test could use it would widen a public API the brief says to leave alone. What is being checked is the
/// storage contract itself: that the four columns exist and are nullable, that a blank value is refused at
/// the database rather than normalized, that uniqueness holds inside each namespace and across none of
/// them, and that a row written before the migration survives it unbound rather than acquiring a value
/// nobody observed.
/// </para>
/// <para>
/// No secret, credential, artifact body or user path is written by any of these cases, and the two paths
/// that appear are the literal strings the migration comments discuss.
/// </para>
/// </summary>
public sealed class GatewayNativeIdentityPersistenceTests : IDisposable
{
    private const string Timestamp = "2026-10-01T00:00:00Z";
    private const string NativeProvider = "gw-provider";
    private const string NativeAccount = "gw-account";
    private const string NativeModel = "gw-model";

    /// <summary>
    /// The shape <c>Accounts.ProviderNativeId</c> already has for the Codex backend. It is stored, read back
    /// and left exactly as it was: the new identity is a second value beside it, not a reinterpretation.
    /// </summary>
    private const string CodexPath = @"C:\Users\example\.codex\auth.json";

    private readonly TestDirectory _directory = new();

    /// <summary>
    /// The temporary directory only. This class deliberately does not clear process-global pools:
    /// xunit runs test classes in parallel, so teardown must not affect a connection belonging to
    /// another collection. Nothing here needs a global clear - every test uses its own
    /// temporary path, and <see cref="TestDirectory"/> already drains pending finalizers and retries before
    /// it gives up on releasing the file.
    /// </summary>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task EveryExistingRowStaysUnboundAcrossTheUpgrade()
    {
        var factory = CreateFactory();
        var migrations = DatabaseMigrator.LoadEmbeddedMigrations();

        // A database at the version immediately before this one, with a full route library already in it.
        await new DatabaseMigrator(factory, migrations.Where(m => m.Version < 12).ToArray()).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await SeedLibraryAsync(connection, bound: false, gatewayColumnsExist: false);
        }

        var report = await new DatabaseMigrator(factory).MigrateAsync();

        Assert.Equal(11, report.PreviousSchemaVersion);
        Assert.Equal(DatabaseMigrator.LoadEmbeddedMigrations().Max(m => m.Version), report.CurrentSchemaVersion);
        Assert.Equal(new[] { "ReviewerNativeRouteIdentity", "GatewayApiKeyPurpose", "HealthFailureHistory", "ReviewerInsertObservationAuthority", "ProviderHeaders", "WorkflowReviewTerminalRetries", "ProviderSecretDeletionQueue", "ModelRouteHealthIdentity", "HealthAuthenticationFanout", "ArtifactExecutionAssociation", "ArtifactExecutionChronology", "workflow_review_responses", "workflow_material_policy", "workflow_adaptation_transport", "adaptation_runtime_ownership", "account_credential_ownership", "opencode_dispatch_decision", "model_capability_invalidation", "model_capability_context", "project_data_policy_audit" }, report.AppliedMigrations.Select(m => m.Name));

        await using var upgraded = await factory.OpenConnectionAsync();

        // The four new columns exist and every legacy row is null in all of them. Nothing was derived from
        // the display name, the path, the requested alias or the past request.
        Assert.Null(await ExecuteNullableScalarAsync(
            upgraded,
            "SELECT GatewayNativeId FROM ProviderProfiles WHERE Id = 'profile-1';"));
        Assert.Null(await ExecuteNullableScalarAsync(
            upgraded,
            "SELECT GatewayNativeId FROM Accounts WHERE Id = 'account-1';"));
        Assert.Null(await ExecuteNullableScalarAsync(
            upgraded,
            "SELECT GatewayNativeId FROM Models WHERE Id = 'model-1';"));
        Assert.Null(await ExecuteNullableScalarAsync(
            upgraded,
            "SELECT GatewayRouteKey FROM Routes WHERE Id = 'route-1';"));

        // And what each row already said is untouched.
        Assert.Equal(
            CodexPath,
            Convert.ToString(
                await ExecuteScalarAsync(upgraded, "SELECT ProviderNativeId FROM Accounts WHERE Id = 'account-1';"),
                CultureInfo.InvariantCulture));
        Assert.Equal(
            "alias-requested",
            Convert.ToString(
                await ExecuteScalarAsync(upgraded, "SELECT ProviderModelId FROM Models WHERE Id = 'model-1';"),
                CultureInfo.InvariantCulture));
        Assert.Equal(
            "Legacy provider",
            Convert.ToString(
                await ExecuteScalarAsync(upgraded, "SELECT DisplayName FROM ProviderProfiles WHERE Id = 'profile-1';"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ABlankGatewayIdentityIsRefusedByTheDatabaseInEveryOneOfTheFourColumns()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using var connection = await factory.OpenConnectionAsync();
        await SeedLibraryAsync(connection, bound: false);

        // A column that is present must hold something. An empty or whitespace-only value would otherwise
        // read as "the gateway reported an empty name", which is a different and unfalsifiable claim from
        // "no gateway name is recorded", and the resolver's unbound report depends on telling them apart.
        // SQLite's default trim() strips the space character and nothing else, so the tab, the newline, the
        // vertical tab, the form feed and the two non-ASCII spaces are here to prove the constraint names its
        // own character set rather than inheriting one. The escapes are written out so the intent survives
        // an editor that would otherwise strip or normalise an invisible character.
        foreach (var blank in new[]
                 {
                     string.Empty,
                     " ",
                     "\t",
                     "\n",
                     "\r",
                     "\v",
                     "\f",
                     "   ",
                     " ",
                     " "
                 })
        {

            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
                connection,
                "UPDATE ProviderProfiles SET GatewayNativeId = $value WHERE Id = 'profile-1';",
                ("$value", blank)));

            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
                connection,
                "UPDATE Accounts SET GatewayNativeId = $value WHERE Id = 'account-1';",
                ("$value", blank)));

            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
                connection,
                "UPDATE Models SET GatewayNativeId = $value WHERE Id = 'model-1';",
                ("$value", blank)));

            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
                connection,
                "UPDATE Routes SET GatewayRouteKey = $value WHERE Id = 'route-1';",
                ("$value", blank)));
        }

        // A non-blank value is accepted, and so is leaving the column null.
        await ExecuteAsync(
            connection,
            "UPDATE ProviderProfiles SET GatewayNativeId = 'gw-provider' WHERE Id = 'profile-1';");
        Assert.Equal(
            NativeProvider,
            Convert.ToString(
                await ExecuteScalarAsync(connection, "SELECT GatewayNativeId FROM ProviderProfiles WHERE Id = 'profile-1';"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task UniquenessHoldsInsideEachNamespaceAndAcrossNoneOfThem()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using var connection = await factory.OpenConnectionAsync();
        await SeedLibraryAsync(connection, bound: true, gatewayRouteKey: "gw-route");

        // A provider name is only meaningful against a backend, so a second profile on the SAME backend may
        // not repeat it, and a second profile on a DIFFERENT backend may.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, GatewayNativeId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('profile-clash', 'Clash', 'StarCliProxy', 'PrivateSource', 'gw-provider', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            """));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, GatewayNativeId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('profile-other-backend', 'Other backend', 'OpenCode', 'PrivateSource', 'gw-provider', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            """);

        // An account name is namespaced by its own profile, so the same name under a different profile is a
        // different account as far as the gateway is concerned and is allowed.
        await ExecuteAsync(
            connection,
            """
            INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, AuthState, Health, GatewayNativeId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('account-other-profile', 'profile-other-backend', 'Other profile', 'Valid', 'Healthy', 'gw-account',
                    '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            """);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, AuthState, Health, GatewayNativeId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('account-clash', 'profile-1', 'Clash', 'Valid', 'Healthy', 'gw-account', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            """));

        // So is a model name.
        await ExecuteAsync(
            connection,
            """
            INSERT INTO Models (Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState, Provenance,
                                Health, GatewayNativeId, DiscoveredAtUtc)
            VALUES ('model-other-profile', 'OpenCode', 'profile-other-backend', 'alias-other', 'Other', 'Supported', 'ProviderReported',
                    'Healthy', 'gw-model', '2026-10-01T00:00:00Z');
            """);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO Models (Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState, Provenance,
                                Health, GatewayNativeId, DiscoveredAtUtc)
            VALUES ('model-clash', 'StarCliProxy', 'profile-1', 'alias-clash', 'Clash', 'Supported', 'ProviderReported',
                    'Healthy', 'gw-model', '2026-10-01T00:00:00Z');
            """));

        // And a route key.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO Routes (Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, Health, GatewayRouteKey,
                                CreatedAtUtc, UpdatedAtUtc)
            VALUES ('route-clash', 'StarCliProxy', 'profile-1', 'account-1', 'model-1', 'PrivateSource', 'Healthy', 'gw-route',
                    '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            """));
    }

    [Fact]
    public async Task APartlyBoundLibraryResolvesToNothingAndSaysWhichRowsAreUnbound()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            // The provider and the model are bound, the account is not. This is the state an operator is in
            // after binding two of the three facts by hand, and it is the state a wrong resolver would read
            // as a complete identity.
            await SeedLibraryAsync(connection, bound: false, bindProvider: true, bindModel: true);
        }

        var snapshot = await ReadAsync(factory);
        var resolution = GatewayRouteIdentityResolver.Resolve(
            Observation(NativeProvider, NativeAccount, NativeModel),
            snapshot.Candidates,
            Now);

        Assert.Equal(GatewayRouteRefusalKind.IncompletePersistedIdentity, resolution.Refusal);
        Assert.Null(resolution.RouteId);
        Assert.Equal(new[] { "route-1" }, resolution.UnboundRouteIds);
    }

    [Fact]
    public async Task ACompletelyBoundLibraryResolvesToTheOneRouteThatOccupiesTheTriple()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await SeedLibraryAsync(connection, bound: true);
        }

        var snapshot = await ReadAsync(factory);
        var resolution = GatewayRouteIdentityResolver.Resolve(
            Observation(NativeProvider, NativeAccount, NativeModel, reasoningEffort: "high", speedMode: "fast"),
            snapshot.Candidates,
            Now);

        Assert.Equal("route-1", resolution.RouteId);
        Assert.Equal(1, snapshot.RouteRowCount);
        Assert.Single(snapshot.Candidates);
    }

    [Fact]
    public async Task TwoModeVariantsOnOnePersistedTupleAreSeparatedOnlyByTheObservedDimension()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await SeedLibraryAsync(connection, bound: true);
            await ExecuteAsync(
                connection,
                """
                INSERT INTO Routes (Id, Backend, ProviderProfileId, AccountId, ModelId, ReasoningEffort, SpeedMode,
                                    ExecutionMode, MaxDataClass, IsEnabled, Health, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('route-2', 'StarCliProxy', 'profile-1', 'account-1', 'model-1', 'high', 'slow', 'exec',
                        'PrivateSource', 1, 'Healthy', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
                """);
        }

        var candidates = (await ReadAsync(factory)).Candidates;

        // Both rows sit on one triple and differ only in the speed mode, so the speed mode the gateway
        // reports is the only thing that can tell them apart - and when it does, one survives.
        var identified = GatewayRouteIdentityResolver.Resolve(
            Observation(NativeProvider, NativeAccount, NativeModel, speedMode: SpeedFast),
            candidates,
            Now);

        Assert.Equal("route-1", identified.RouteId);
        Assert.Equal(GatewayRouteRefusalKind.None, identified.Refusal);
    }

    [Fact]
    public async Task AnUnobservedDimensionOnAPersistedTupleRefusesWithoutNamingEitherRow()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await SeedLibraryAsync(connection, bound: true);
            await ExecuteAsync(
                connection,
                """
                INSERT INTO Routes (Id, Backend, ProviderProfileId, AccountId, ModelId, ReasoningEffort, SpeedMode,
                                    ExecutionMode, MaxDataClass, IsEnabled, Health, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('route-2', 'StarCliProxy', 'profile-1', 'account-1', 'model-1', 'high', 'slow', 'exec',
                        'PrivateSource', 1, 'Healthy', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
                """);
        }

        var candidates = (await ReadAsync(factory)).Candidates;

        // The same two rows, and a response that stayed silent about the dimension separating them. The
        // refusal is raised on the observation, so it names no row at all - there is no row to name.
        var refusal = GatewayRouteIdentityResolver.Resolve(
            Observation(NativeProvider, NativeAccount, NativeModel, speedMode: null),
            candidates,
            Now);

        Assert.Equal(GatewayRouteRefusalKind.UnobservedModeDimension, refusal.Refusal);
        Assert.Null(refusal.RouteId);
        Assert.Empty(refusal.CandidateRouteIds);
        Assert.Contains("speedMode", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowWhoseModeColumnIsNullIsNotResolvableByACompleteObservation()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            // A fully bound route that simply does not declare an execution mode.
            await SeedLibraryAsync(connection, bound: true);
            await ExecuteAsync(
                connection,
                "UPDATE Routes SET ExecutionMode = NULL WHERE Id = 'route-1';");
        }

        var resolution = GatewayRouteIdentityResolver.Resolve(
            Observation(NativeProvider, NativeAccount, NativeModel),
            (await ReadAsync(factory)).Candidates,
            Now);

        // The row carries a complete gateway identity, so it is not reported as unbound - it is simply not
        // what a complete observation described, because it never said which execution mode it was.
        Assert.Equal(GatewayRouteRefusalKind.NoEligibleRoute, resolution.Refusal);
        Assert.Null(resolution.RouteId);
        Assert.Empty(resolution.UnboundRouteIds);
    }

    [Fact]
    public async Task ADisabledOrUnhealthyPersistedRowIsNotACandidate()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await SeedLibraryAsync(connection, bound: true);
            await ExecuteAsync(connection, "UPDATE Routes SET IsEnabled = 0 WHERE Id = 'route-1';");
        }

        var candidates = (await ReadAsync(factory)).Candidates;

        Assert.Single(candidates);
        Assert.False(GatewayRouteIdentityResolver.IsEligible(candidates[0], Now));

        var resolution = GatewayRouteIdentityResolver.Resolve(
            Observation(NativeProvider, NativeAccount, NativeModel),
            candidates,
            Now);

        Assert.Equal(GatewayRouteRefusalKind.NoEligibleRoute, resolution.Refusal);
        Assert.Null(resolution.RouteId);
    }

    [Fact]
    public async Task ARouteWhoseAccountBelongsToAnotherProfileIsReadAndRefusedAsIncoherent()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await SeedLibraryAsync(connection, bound: true);

            // A second provider on the same backend, so the account genuinely belongs to another profile
            // while the route keeps naming the first one. Nothing in the schema objects: each foreign key
            // holds, because Accounts.ProviderProfileId is checked against its own profile, not the route.
            await ExecuteAsync(
                connection,
                """
                INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, GatewayNativeId, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('profile-2', 'Second provider', 'StarCliProxy', 'PrivateSource', 'gw-provider-two',
                        '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
                INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, AuthState, Health, GatewayNativeId, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('account-2', 'profile-2', 'Second account', 'Valid', 'Healthy', 'gw-account-two',
                        '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
                """);

            await ExecuteAsync(
                connection,
                """
                INSERT INTO Routes (Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, IsEnabled, Health,
                                    CreatedAtUtc, UpdatedAtUtc)
                VALUES ('route-crossed', 'StarCliProxy', 'profile-1', 'account-2', 'model-1', 'PrivateSource', 1, 'Healthy',
                        '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
                """);
        }

        var candidates = (await ReadAsync(factory)).Candidates;
        var crossed = Assert.Single(candidates, candidate => candidate.Route.Id == "route-crossed");

        Assert.Equal(new[] { "AccountProviderProfile" }, crossed.Incoherences.ToArray());
        Assert.False(GatewayRouteIdentityResolver.IsEligible(crossed, Now));
    }

    [Fact]
    public async Task TheReaderCountsARouteRowItCannotJoinInsteadOfLosingIt()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await SeedLibraryAsync(connection, bound: true);

            // Foreign keys are off by default in a raw connection unless the schema turns them on, so this
            // row is inserted the only way the constraint allows: with the identity switched off for the
            // statement, exactly as a library edited outside this process would look.
            await ExecuteAsync(
                connection,
                "PRAGMA foreign_keys = OFF;");
            await ExecuteAsync(
                connection,
                """
                INSERT INTO Routes (Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, IsEnabled, Health,
                                    CreatedAtUtc, UpdatedAtUtc)
                VALUES ('route-orphan', 'StarCliProxy', 'profile-1', 'account-missing', 'model-1', 'PrivateSource', 1, 'Healthy',
                        '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
                """);
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
        }

        var snapshot = await ReadAsync(factory);
        var diagnostic = GatewayNativeIdentityDiagnostic.Report(snapshot, Now);

        Assert.Equal(2, snapshot.RouteRowCount);
        Assert.Single(snapshot.Candidates);
        Assert.Equal(1, diagnostic.JoinedRouteCount);
        Assert.False(diagnostic.IsMappingComplete);
    }

    [Fact]
    public async Task ABackendNameThisBuildDoesNotKnowRefusesTheWholeRead()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await SeedLibraryAsync(connection, bound: true);
            await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;");
            await ExecuteAsync(
                connection,
                "UPDATE ProviderProfiles SET Backend = 'SomeNewerBackend' WHERE Id = 'profile-1';");
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
        }

        // Defaulting it would silently re-point the row at the one backend this application happens to
        // have, so the read refuses outright and names the column it could not interpret.
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(factory));

        Assert.Contains("ProviderProfiles.Backend", failure.Message, StringComparison.Ordinal);
        Assert.Contains("SomeNewerBackend", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHealthNameThisBuildDoesNotKnowRefusesTheWholeRead()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await SeedLibraryAsync(connection, bound: true);

            // 'Unknown' is not a HealthState, so the row is unreadable rather than unhealthy. Reporting it
            // as merely ineligible would be a guess about a row whose state cannot be named at all.
            await ExecuteAsync(
                connection,
                "UPDATE Routes SET Health = 'Unknown' WHERE Id = 'route-1';");
        }

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(factory));

        Assert.Contains("Routes.Health", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReaderReportsCapabilityColumnsAsPersistedAndInventsNoCapabilityList()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await SeedLibraryAsync(connection, bound: true);
        }

        var candidate = Assert.Single((await ReadAsync(factory)).Candidates);

        Assert.Equal(BackendType.StarCliProxy, candidate.Model.Backend);
        Assert.Equal("alias-requested", candidate.Model.ProviderModelId);
        Assert.Equal(NativeModel, candidate.Model.GatewayNativeId);
        Assert.Equal(CodexPath, candidate.Account.ProviderNativeId);
        Assert.Equal(NativeAccount, candidate.Account.GatewayNativeId);
        Assert.Equal(NativeProvider, candidate.Profile.GatewayNativeId);

        // This reader knows an identity and nothing else about a model, and says so rather than filling the
        // list from a table it did not read.
        Assert.Empty(candidate.Model.SupportedModes);
        Assert.Empty(candidate.Model.SupportedReasoningEfforts);
        Assert.Empty(candidate.Model.SupportedSpeedModes);
        Assert.Empty(candidate.Model.AvailableAccountIds);
        Assert.Empty(candidate.Account.SessionBindings);
    }

    [Fact]
    public async Task TheReaderWritesNothingAndLeavesEveryRouteKeyedRowAsItFoundIt()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await SeedLibraryAsync(connection, bound: true, gatewayRouteKey: "gw-route-key");
        }

        await ReadAsync(factory);
        await ReadAsync(factory);

        await using var verify = await factory.OpenConnectionAsync();

        Assert.Equal(
            1L,
            Convert.ToInt64(
                await ExecuteScalarAsync(verify, "SELECT COUNT(*) FROM Routes;"),
                CultureInfo.InvariantCulture));
        Assert.Equal(
            "gw-route-key",
            Convert.ToString(
                await ExecuteScalarAsync(verify, "SELECT GatewayRouteKey FROM Routes WHERE Id = 'route-1';"),
                CultureInfo.InvariantCulture));
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The three mode dimensions the seeded route-1 declares, and the ones a complete observation has to
    /// report to match it. The native-name form refuses an observation that omits any of them, so a test
    /// that is about something else states all three and a test that is about a missing dimension passes an
    /// explicit null.
    /// </summary>
    private const string ReasoningHigh = "high";

    private const string SpeedFast = "fast";
    private const string ExecutionMode = "exec";

    private static GatewayRouteObservation Observation(
        string provider,
        string account,
        string model,
        string? reasoningEffort = ReasoningHigh,
        string? speedMode = SpeedFast,
        string? executionMode = ExecutionMode) =>
        GatewayRouteObservation.Merge(new[]
        {
            new GatewayRouteObservationChunk(provider, account, model, reasoningEffort, speedMode, executionMode)
        }).Observation!;

    private SqliteConnectionFactory CreateFactory() =>
        new(_directory.GetPath("llmworkgui.db"));

    private static async Task<GatewayRouteCandidateSnapshot> ReadAsync(SqliteConnectionFactory factory) =>
        await new SqliteGatewayRouteCandidateReader(factory).ReadCandidatesAsync();

    /// <summary>
    /// One profile, one account, one model and one route, in the shape the initial schema wrote them. The
    /// account carries a Codex path in the column that has always held one, the model carries a requested
    /// alias in the column that has always held one, and the gateway identity is bound only when asked for.
    /// <para>
    /// With <c>gatewayColumnsExist: false</c> the four new columns are left out of the statement entirely,
    /// which is what seeding a database at the version before this migration requires: naming a column that
    /// does not exist yet would fail, and omitting it afterwards would not prove the migration left the
    /// legacy row null.
    /// </para>
    /// </summary>
    private static async Task SeedLibraryAsync(
        SqliteConnection connection,
        bool bound,
        bool bindProvider = false,
        bool bindModel = false,
        string? gatewayRouteKey = null,
        bool gatewayColumnsExist = true)
    {
        var providerNativeId = bound || bindProvider ? NativeProvider : null;
        var modelNativeId = bound || bindModel ? NativeModel : null;
        var accountNativeId = bound ? NativeAccount : null;
        var routeKey = bound ? gatewayRouteKey : null;

        // The column list and the value list move together: naming a column the pre-migration schema does
        // not have would fail outright, and leaving the column out of both would not prove the migration
        // left an existing row null.
        var providerColumn = gatewayColumnsExist ? "GatewayNativeId, " : string.Empty;
        var accountColumn = gatewayColumnsExist ? "GatewayNativeId, " : string.Empty;
        var modelColumn = gatewayColumnsExist ? "GatewayNativeId, " : string.Empty;
        var routeColumn = gatewayColumnsExist ? "GatewayRouteKey, " : string.Empty;
        var providerValue = gatewayColumnsExist ? "$provider, " : string.Empty;
        var accountValue = gatewayColumnsExist ? "$account, " : string.Empty;
        var modelValue = gatewayColumnsExist ? "$model, " : string.Empty;
        var routeValue = gatewayColumnsExist ? "$routeKey, " : string.Empty;

        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, IsEnabled, {providerColumn}CreatedAtUtc, UpdatedAtUtc)
            VALUES ('profile-1', 'Legacy provider', 'StarCliProxy', 'PrivateSource', 1, {providerValue}'2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, ProviderNativeId, AuthState, ManualPriority, IsEnabled, Health,
                                  MaxConcurrentExecutions, {accountColumn}CreatedAtUtc, UpdatedAtUtc)
            VALUES ('account-1', 'profile-1', 'Legacy account', $path, 'Valid', 0, 1, 'Healthy', 1, {accountValue}'2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            INSERT INTO Models (Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState, Provenance, IsEnabled,
                                Health, SupportsTools, SupportsAttachments, {modelColumn}DiscoveredAtUtc)
            VALUES ('model-1', 'StarCliProxy', 'profile-1', 'alias-requested', 'Legacy model', 'Supported', 'ProviderReported', 1,
                    'Healthy', 0, 0, {modelValue}'2026-10-01T00:00:00Z');
            INSERT INTO Routes (Id, Backend, ProviderProfileId, AccountId, ModelId, ReasoningEffort, SpeedMode, ExecutionMode,
                                MaxDataClass, IsEnabled, Health, ManualPriority, {routeColumn}CreatedAtUtc, UpdatedAtUtc)
            VALUES ('route-1', 'StarCliProxy', 'profile-1', 'account-1', 'model-1', 'high', 'fast', 'exec', 'PrivateSource', 1,
                    'Healthy', 0, {routeValue}'2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            """,
            ("$provider", providerNativeId),
            ("$account", accountNativeId),
            ("$model", modelNativeId),
            ("$path", CodexPath),
            ("$routeKey", routeKey));
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    /// <summary>
    /// A SQL NULL as a real null. <c>ExecuteScalarAsync</c> hands back <see cref="DBNull.Value"/> for one,
    /// which is not the same thing to an assertion about whether a value was recorded.
    /// </summary>
    private static async Task<object?> ExecuteNullableScalarAsync(SqliteConnection connection, string sql)
    {
        var value = await ExecuteScalarAsync(connection, sql);

        return value is null or DBNull ? null : value;
    }
}
