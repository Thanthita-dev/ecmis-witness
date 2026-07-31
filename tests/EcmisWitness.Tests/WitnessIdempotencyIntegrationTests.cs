using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Infrastructure;
using EcmisWitness.Api.Security;
using Npgsql;

namespace EcmisWitness.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WitnessIdempotencyPostgresCollection
{
    public const string Name = "Witness idempotency PostgreSQL";
}

[Collection(WitnessIdempotencyPostgresCollection.Name)]
public sealed class WitnessIdempotencyIntegrationTests
{
    [Fact]
    public async Task Concurrent_create_with_same_key_returns_one_case_for_two_requests()
        => await AssertConcurrentCreateAsync(2);

    [Fact]
    public async Task Concurrent_create_with_same_key_returns_one_case_for_ten_requests()
        => await AssertConcurrentCreateAsync(10);

    [Fact]
    public async Task Same_create_request_replays_after_repository_restart()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        var actor = TestUser("restart");
        var key = $"E2E-IDEMP-RESTART-{Guid.NewGuid():N}";
        var request = CreateRequest(key, "Restart");
        Guid caseId = Guid.Empty;

        try
        {
            WitnessCaseDetailDto first;
            await using (var firstDataSource = NpgsqlDataSource.Create(connectionString))
            {
                await new WitnessDatabaseInitializer(firstDataSource).InitializeAsync();
                first = await Repository(firstDataSource).CreateAsync(
                    request, actor, "127.0.0.1", default);
            }

            caseId = first.Case.Id;
            await using var restartedDataSource = NpgsqlDataSource.Create(connectionString);
            var replay = await Repository(restartedDataSource).CreateAsync(
                request, actor, "127.0.0.1", default);

            Assert.Equal(first.Case.Id, replay.Case.Id);
            Assert.Equal(first.Case.RequestNo, replay.Case.RequestNo);
            var counts = await CountsAsync(restartedDataSource, caseId, key);
            Assert.Equal((1, 1, 1, 1, 1), counts);
        }
        finally
        {
            await DeleteCasesAsync(connectionString, caseId);
        }
    }

    [Fact]
    public async Task Same_create_key_with_different_payload_returns_conflict_without_second_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = Repository(dataSource);
        var actor = TestUser("different-payload");
        var key = $"E2E-IDEMP-DIFF-{Guid.NewGuid():N}";
        Guid caseId = Guid.Empty;

        try
        {
            var first = await repository.CreateAsync(
                CreateRequest(key, "Payload-A"), actor, "127.0.0.1", default);
            caseId = first.Case.Id;

            var conflict = await Assert.ThrowsAsync<WitnessIdempotencyConflictException>(() =>
                repository.CreateAsync(
                    CreateRequest(key, "Payload-B"), actor, "127.0.0.1", default));

            Assert.Contains("คำสั่งหรือข้อมูลอื่น", conflict.Message);
            var counts = await CountsAsync(dataSource, caseId, key);
            Assert.Equal((1, 1, 1, 1, 1), counts);
        }
        finally
        {
            await DeleteCasesAsync(connectionString, caseId);
        }
    }

    [Fact]
    public async Task Same_create_key_is_scoped_by_actor()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = Repository(dataSource);
        var key = $"E2E-IDEMP-ACTOR-{Guid.NewGuid():N}";
        var actorA = TestUser("actor-a");
        var actorB = TestUser("actor-b");
        var caseIds = new List<Guid>();

        try
        {
            var first = await repository.CreateAsync(
                CreateRequest(key, "Actor-A"), actorA, "127.0.0.1", default);
            var second = await repository.CreateAsync(
                CreateRequest(key, "Actor-A"), actorB, "127.0.0.1", default);
            caseIds.Add(first.Case.Id);
            caseIds.Add(second.Case.Id);

            Assert.NotEqual(first.Case.Id, second.Case.Id);
            Assert.NotEqual(first.Case.RequestNo, second.Case.RequestNo);
            Assert.Equal(2, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.idempotency_records
                WHERE idempotency_key=$1 AND resource_scope='witness:cases'
                """, key));
        }
        finally
        {
            await DeleteCasesAsync(connectionString, caseIds.ToArray());
        }
    }

    [Fact]
    public async Task Workflow_retry_replays_result_without_duplicate_event_or_audit_and_rejects_other_action()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = Repository(dataSource);
        var actor = TestUser("workflow");
        var createKey = $"E2E-IDEMP-WF-CREATE-{Guid.NewGuid():N}";
        var commandKey = $"E2E-IDEMP-WF-COMMAND-{Guid.NewGuid():N}";
        Guid caseId = Guid.Empty;

        try
        {
            var created = await repository.CreateAsync(
                CreateRequest(createKey, "Workflow"), actor, "127.0.0.1", default);
            caseId = created.Case.Id;
            var command = new ExecuteWitnessCommandRequest(
                "E2E-TEST ส่งคำร้องเพื่อทดสอบ idempotency",
                created.Case.Version,
                new Dictionary<string, string> { ["test_batch_id"] = "E2E-WIT-IDEMPOTENCY" },
                commandKey);

            var first = await repository.ExecuteCommandAsync(
                caseId, "submit-intake", command, actor, "127.0.0.1", default);
            var replay = await repository.ExecuteCommandAsync(
                caseId, "submit-intake", command, actor, "127.0.0.1", default);

            Assert.Equal(first.CaseId, replay.CaseId);
            Assert.Equal(first.RequestNo, replay.RequestNo);
            Assert.Equal(first.FromStatus, replay.FromStatus);
            Assert.Equal(first.ToStatus, replay.ToStatus);
            Assert.Equal(first.Version, replay.Version);
            Assert.Equal(
                first.AvailableActions.Select(item => item.Code),
                replay.AvailableActions.Select(item => item.Code));
            Assert.Equal(WitnessStatuses.StaffReview, replay.ToStatus);
            Assert.Equal(1, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.workflow_events
                WHERE case_id=$1 AND idempotency_key=$2
                """, caseId, commandKey));
            Assert.Equal(1, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.audit_events
                WHERE case_id=$1 AND action='workflow.transition'
                """, caseId));
            Assert.Equal(2L, await ScalarAsync<long>(dataSource,
                "SELECT row_version FROM witness.cases WHERE id=$1", caseId));

            var conflict = await Assert.ThrowsAsync<WitnessIdempotencyConflictException>(() =>
                repository.ExecuteCommandAsync(
                    caseId,
                    "request-withdrawal",
                    command,
                    actor,
                    "127.0.0.1",
                    default));
            Assert.Contains("คำสั่งหรือข้อมูลอื่น", conflict.Message);
            Assert.Equal(2, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.workflow_events WHERE case_id=$1
                """, caseId));
        }
        finally
        {
            await DeleteCasesAsync(connectionString, caseId);
        }
    }

    [Fact]
    public async Task Database_failure_rolls_back_case_form_workflow_audit_and_idempotency_claim()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = TestUser("rollback") with { DisplayName = new string('ก', 251) };
        var key = $"E2E-IDEMP-ROLLBACK-{Guid.NewGuid():N}";

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            Repository(dataSource).CreateAsync(
                CreateRequest(key, "Rollback"), actor, "127.0.0.1", default));

        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, failure.SqlState);
        Assert.Equal(0, await ScalarAsync<long>(dataSource,
            "SELECT COUNT(*) FROM witness.cases WHERE created_by=$1", actor.UserId));
        Assert.Equal(0, await ScalarAsync<long>(dataSource,
            "SELECT COUNT(*) FROM witness.forms f JOIN witness.cases c ON c.id=f.case_id WHERE c.created_by=$1", actor.UserId));
        Assert.Equal(0, await ScalarAsync<long>(dataSource,
            "SELECT COUNT(*) FROM witness.workflow_events e JOIN witness.cases c ON c.id=e.case_id WHERE c.created_by=$1", actor.UserId));
        Assert.Equal(0, await ScalarAsync<long>(dataSource,
            "SELECT COUNT(*) FROM witness.audit_events a JOIN witness.cases c ON c.id=a.case_id WHERE c.created_by=$1", actor.UserId));
        Assert.Equal(0, await ScalarAsync<long>(dataSource, """
            SELECT COUNT(*) FROM witness.idempotency_records
            WHERE actor_user_id=$1 AND resource_scope='witness:cases' AND idempotency_key=$2
            """, actor.UserId, key));
    }

    private static async Task AssertConcurrentCreateAsync(int concurrency)
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = Repository(dataSource);
        var actor = TestUser($"concurrent-{concurrency}");
        var key = $"E2E-IDEMP-CONCURRENT-{concurrency}-{Guid.NewGuid():N}";
        var request = CreateRequest(key, $"Concurrent-{concurrency}");
        Guid caseId = Guid.Empty;
        var beforeSequence = await SequenceValueAsync(dataSource);

        try
        {
            using var gate = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, concurrency)
                .Select(_ => Task.Run(async () =>
                {
                    gate.Wait();
                    return await repository.CreateAsync(request, actor, "127.0.0.1", default);
                }))
                .ToArray();
            gate.Set();
            var results = await Task.WhenAll(tasks);
            caseId = results[0].Case.Id;

            Assert.All(results, item => Assert.Equal(caseId, item.Case.Id));
            Assert.Single(results.Select(item => item.Case.Id).Distinct());
            Assert.Single(results.Select(item => item.Case.RequestNo).Distinct());
            var counts = await CountsAsync(dataSource, caseId, key);
            Assert.Equal((1, 1, 1, 1, 1), counts);
            Assert.Equal(beforeSequence + 1, await SequenceValueAsync(dataSource));
        }
        finally
        {
            await DeleteCasesAsync(connectionString, caseId);
        }
    }

    private static WitnessRepository Repository(NpgsqlDataSource dataSource)
        => new(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());

    private static WitnessUserContext TestUser(string suffix)
        => new(
            Guid.NewGuid(),
            $"e2e-idempotency-{suffix}",
            $"E2E-TEST Idempotency {suffix}",
            "ผู้ทดสอบระบบ",
            new HashSet<string> { "super_admin" },
            new HashSet<string> { WitnessPermissions.Create });

    private static CreateWitnessCaseRequest CreateRequest(string key, string suffix)
        => new(
            1,
            new Dictionary<string, string>
            {
                ["witness_first_name"] = "E2E-TEST",
                ["witness_last_name"] = suffix,
                ["petitioner_first_name"] = "E2E-TEST",
                ["petitioner_last_name"] = suffix,
                ["test_batch_id"] = "E2E-WIT-IDEMPOTENCY"
            },
            Submit: false,
            IdempotencyKey: key);

    private static string? ConnectionString()
        => Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");

    private static async Task<(int Cases, int Forms, int Workflows, int Audits, int Claims)> CountsAsync(
        NpgsqlDataSource dataSource,
        Guid caseId,
        string idempotencyKey)
    {
        var cases = (int)await ScalarAsync<long>(dataSource,
            "SELECT COUNT(*) FROM witness.cases WHERE id=$1", caseId);
        var forms = (int)await ScalarAsync<long>(dataSource,
            "SELECT COUNT(*) FROM witness.forms WHERE case_id=$1", caseId);
        var workflows = (int)await ScalarAsync<long>(dataSource,
            "SELECT COUNT(*) FROM witness.workflow_events WHERE case_id=$1", caseId);
        var audits = (int)await ScalarAsync<long>(dataSource,
            "SELECT COUNT(*) FROM witness.audit_events WHERE case_id=$1", caseId);
        var claims = (int)await ScalarAsync<long>(dataSource, """
            SELECT COUNT(*) FROM witness.idempotency_records
            WHERE resource_id=$1 AND idempotency_key=$2 AND status='completed'
            """, caseId, idempotencyKey);
        return (cases, forms, workflows, audits, claims);
    }

    private static async Task<long> SequenceValueAsync(NpgsqlDataSource dataSource)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT CASE WHEN is_called THEN last_value ELSE last_value - 1 END FROM witness.request_number_seq");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlDataSource dataSource,
        string sql,
        params object[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
            cmd.Parameters.AddWithValue(parameter);
        var value = await cmd.ExecuteScalarAsync();
        Assert.NotNull(value);
        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task DeleteCasesAsync(string connectionString, params Guid[] caseIds)
    {
        var ids = caseIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
            return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var cmd = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=ANY($1)");
        cmd.Parameters.AddWithValue(ids);
        await cmd.ExecuteNonQueryAsync();
    }
}
