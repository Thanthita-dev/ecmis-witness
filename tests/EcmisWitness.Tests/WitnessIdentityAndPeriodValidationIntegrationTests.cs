using System.Text.Json;
using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Forms;
using EcmisWitness.Api.Infrastructure;
using EcmisWitness.Api.Security;
using Npgsql;

namespace EcmisWitness.Tests;

[Collection(WitnessIdempotencyPostgresCollection.Name)]
public sealed class WitnessIdentityAndPeriodValidationIntegrationTests
{
    [Theory]
    [InlineData("123456789012", "เลขประจำตัวประชาชนต้องเป็นตัวเลข 13 หลัก")]
    [InlineData("ABC-INVALID-!", "เลขประจำตัวประชาชนต้องเป็นตัวเลข 13 หลัก")]
    [InlineData("9999999999995", "เลขประจำตัวประชาชนไม่ถูกต้อง")]
    public async Task Invalid_citizen_id_create_is_rejected_without_case_or_idempotency_mutation(
        string citizenId,
        string expectedError)
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = Repository(dataSource);
        var actor = User($"identity-create-{Guid.NewGuid():N}");
        var key = $"E2E-WIT-043-CREATE-{Guid.NewGuid():N}";
        var before = await ActorMutationCountsAsync(dataSource, actor.UserId, key);

        var error = await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.CreateAsync(
            new CreateWitnessCaseRequest(
                1,
                new Dictionary<string, string>
                {
                    ["petitioner_first_name"] = "E2E-TEST",
                    ["petitioner_last_name"] = "เลขประชาชน",
                    ["petitioner_citizen_id"] = citizenId,
                    ["witness_first_name"] = "E2E-TEST",
                    ["witness_last_name"] = "เลขประชาชน"
                },
                Submit: false,
                IdempotencyKey: key),
            actor,
            "127.0.0.1",
            default));

        Assert.Equal(expectedError, error.Message);
        Assert.Equal(before, await ActorMutationCountsAsync(dataSource, actor.UserId, key));
    }

    [Fact]
    public async Task Invalid_identity_expiry_save_is_rejected_for_draft_and_complete_without_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = Repository(dataSource);
        var actor = User($"identity-date-{Guid.NewGuid():N}");
        var created = await CreateDraftAsync(repository, actor, "WIT-044");

        try
        {
            var before = await SnapshotAsync(dataSource, created.Case.Id);
            foreach (var complete in new[] { false, true })
            {
                var error = await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.SaveFormAsync(
                    created.Case.Id,
                    1,
                    new SaveWitnessFormRequest(
                        new Dictionary<string, string>
                        {
                            ["petitioner_card_issued"] = "2026-07-20",
                            ["petitioner_card_expired"] = "2026-07-19"
                        },
                        complete,
                        1,
                        created.Case.Version),
                    actor,
                    "127.0.0.1",
                    default));
                Assert.Equal("วันหมดอายุต้องไม่ก่อนวันออกบัตร", error.Message);
                Assert.Equal(before, await SnapshotAsync(dataSource, created.Case.Id));
            }
        }
        finally
        {
            await DeleteCaseAsync(dataSource, created.Case.Id);
        }
    }

    [Fact]
    public async Task Invalid_Kb4_period_is_rejected_for_draft_and_complete_without_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = Repository(dataSource);
        var actor = User($"kb4-date-{Guid.NewGuid():N}");
        var created = await CreateSubmittedUrgentAsync(repository, actor, "WIT-045");

        try
        {
            var before = await SnapshotAsync(dataSource, created.Case.Id);
            foreach (var complete in new[] { false, true })
            {
                var error = await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.SaveFormAsync(
                    created.Case.Id,
                    4,
                    new SaveWitnessFormRequest(
                        new Dictionary<string, string>
                        {
                            ["start_date"] = "2026-07-20",
                            ["end_date"] = "2026-07-19"
                        },
                        complete,
                        0,
                        created.Case.Version),
                    actor,
                    "127.0.0.1",
                    default));
                Assert.Equal(
                    "วันสิ้นสุดการคุ้มครองต้องไม่ก่อนวันเริ่มต้นการคุ้มครอง",
                    error.Message);
                Assert.Equal(before, await SnapshotAsync(dataSource, created.Case.Id));
            }
        }
        finally
        {
            await DeleteCaseAsync(dataSource, created.Case.Id);
        }
    }

    [Fact]
    public async Task Legacy_invalid_Kb4_is_blocked_before_sign_and_urgent_transition_without_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = Repository(dataSource);
        var actor = User($"kb4-legacy-{Guid.NewGuid():N}");
        var created = await CreateSubmittedUrgentAsync(repository, actor, "WIT-045-LEGACY");

        try
        {
            var validKb4 = CompleteValues(4);
            validKb4.Remove("supervisor_opinion");
            validKb4.Remove("director_opinion");
            validKb4["proposal_5_1"] = "true";
            validKb4["proposal_5_2"] = "false";
            validKb4["start_date"] = "2026-07-20";
            validKb4["end_date"] = "2026-07-21";
            var draft = await repository.SaveFormAsync(
                created.Case.Id,
                4,
                new SaveWitnessFormRequest(validKb4, false, 0, created.Case.Version),
                actor,
                "127.0.0.1",
                default);
            var completed = await repository.SaveFormAsync(
                created.Case.Id,
                4,
                new SaveWitnessFormRequest(validKb4, true, draft.Version, draft.CaseVersion),
                actor,
                "127.0.0.1",
                default);

            // Legacy fixture: simulates a row created before WIT-E2E-045 was fixed.
            await using (var corruptLegacyFixture = dataSource.CreateCommand("""
                UPDATE witness.forms
                SET values_data=jsonb_set(
                    jsonb_set(values_data, '{start_date}', '"2026-07-20"'::jsonb),
                    '{end_date}', '"2026-07-19"'::jsonb)
                WHERE case_id=$1 AND form_number=4
                """))
            {
                corruptLegacyFixture.Parameters.AddWithValue(created.Case.Id);
                Assert.Equal(1, await corruptLegacyFixture.ExecuteNonQueryAsync());
            }

            var before = await SnapshotAsync(dataSource, created.Case.Id);
            var signError = await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.SignFormAsync(
                created.Case.Id,
                4,
                new SignWitnessFormRequest(
                    "เจ้าหน้าที่ผู้เสนอ",
                    "เจ้าหน้าที่ทดสอบ",
                    "บัญชีทดสอบ",
                    "E2E-WIT-045-SIGN",
                    completed.Version,
                    completed.CaseVersion),
                actor,
                "127.0.0.1",
                default));
            Assert.Contains("วันสิ้นสุดการคุ้มครอง", signError.Message);
            Assert.Equal(before, await SnapshotAsync(dataSource, created.Case.Id));

            var commandKey = $"E2E-WIT-045-WORKFLOW-{Guid.NewGuid():N}";
            var transitionError = await Assert.ThrowsAsync<WitnessWorkflowException>(() =>
                repository.ExecuteCommandAsync(
                    created.Case.Id,
                    "urgent-submit-supervisor",
                    new ExecuteWitnessCommandRequest(
                        "E2E-TEST ตรวจข้อมูล legacy",
                        completed.CaseVersion,
                        IdempotencyKey: commandKey),
                    actor,
                    "127.0.0.1",
                    default));
            Assert.Contains("วันสิ้นสุดการคุ้มครอง", transitionError.Message);
            Assert.Equal(before, await SnapshotAsync(dataSource, created.Case.Id));
            Assert.Equal(0, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.idempotency_records
                WHERE resource_id=$1 AND idempotency_key=$2
                """, created.Case.Id, commandKey));
        }
        finally
        {
            await DeleteCaseAsync(dataSource, created.Case.Id);
        }
    }

    private static WitnessRepository Repository(NpgsqlDataSource dataSource)
        => new(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());

    private static WitnessUserContext User(string suffix)
        => new(
            Guid.NewGuid(),
            $"e2e-validation-{suffix}",
            $"E2E-TEST Validation {suffix}",
            "เจ้าหน้าที่ทดสอบ",
            new HashSet<string> { "officer" },
            new HashSet<string>
            {
                WitnessPermissions.Create,
                WitnessPermissions.OfficerReview,
                WitnessPermissions.ViewPii
            });

    private static Task<WitnessCaseDetailDto> CreateDraftAsync(
        WitnessRepository repository,
        WitnessUserContext actor,
        string suffix)
        => repository.CreateAsync(
            new CreateWitnessCaseRequest(
                1,
                new Dictionary<string, string>
                {
                    ["petitioner_first_name"] = "E2E-TEST",
                    ["petitioner_last_name"] = suffix,
                    ["petitioner_citizen_id"] = "9999999999994",
                    ["witness_first_name"] = "E2E-TEST",
                    ["witness_last_name"] = suffix,
                    ["witness_citizen_id"] = "9999999999994"
                },
                Submit: false,
                IdempotencyKey: $"E2E-WIT-VALIDATION-{Guid.NewGuid():N}"),
            actor,
            "127.0.0.1",
            default);

    private static Task<WitnessCaseDetailDto> CreateSubmittedUrgentAsync(
        WitnessRepository repository,
        WitnessUserContext actor,
        string suffix)
    {
        var values = CompleteValues(1);
        values["petitioner_first_name"] = "E2E-TEST";
        values["petitioner_last_name"] = suffix;
        values["witness_first_name"] = "E2E-TEST";
        values["witness_last_name"] = suffix;
        values["urgent"] = "true";
        return repository.CreateAsync(
            new CreateWitnessCaseRequest(
                1,
                values,
                IsUrgent: true,
                Submit: true,
                IdempotencyKey: $"E2E-WIT-VALIDATION-{Guid.NewGuid():N}"),
            actor,
            "127.0.0.1",
            default);
    }

    private static Dictionary<string, string> CompleteValues(int number)
    {
        var form = WitnessProtectionFormCatalog.Get(number);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in form.Fields)
        {
            values[field.Key] = field.Label.Contains("เลขประจำตัวประชาชน", StringComparison.Ordinal)
                ? "9999999999994"
                : field.Type switch
                {
                    WitnessFormFieldType.Checkbox => "true",
                    WitnessFormFieldType.MultiSelect => JsonSerializer.Serialize(new[]
                    {
                        field.Options?.FirstOrDefault() ?? "ตัวเลือกทดสอบ"
                    }),
                    WitnessFormFieldType.Select => field.Options?.FirstOrDefault() ?? "ข้อมูลทดสอบ",
                    WitnessFormFieldType.Address => JsonSerializer.Serialize(
                        (field.Columns ?? []).ToDictionary(item => item.Key, _ => "ข้อมูลทดสอบ")),
                    WitnessFormFieldType.Repeating => JsonSerializer.Serialize(new[]
                    {
                        (field.Columns ?? []).ToDictionary(item => item.Key, _ => "ข้อมูลทดสอบ")
                    }),
                    WitnessFormFieldType.Date => "2026-07-20",
                    WitnessFormFieldType.Number => "1",
                    _ => "ข้อมูลทดสอบ"
                };
        }
        if (number == 1)
        {
            values["related_people"] = "[]";
            values["threat_status"] = "ไม่มี";
            values["petitioner_officer_id"] = "";
            values["witness_officer_id"] = "";
        }
        return values;
    }

    private static string? ConnectionString()
        => Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");

    private static async Task<MutationCounts> ActorMutationCountsAsync(
        NpgsqlDataSource dataSource,
        Guid actorId,
        string idempotencyKey)
        => new(
            await ScalarAsync<long>(dataSource, "SELECT COUNT(*) FROM witness.cases WHERE created_by=$1", actorId),
            await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.forms f JOIN witness.cases c ON c.id=f.case_id
                WHERE c.created_by=$1
                """, actorId),
            await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.workflow_events w JOIN witness.cases c ON c.id=w.case_id
                WHERE c.created_by=$1
                """, actorId),
            await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.audit_events a JOIN witness.cases c ON c.id=a.case_id
                WHERE c.created_by=$1
                """, actorId),
            await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.idempotency_records
                WHERE actor_user_id=$1 AND idempotency_key=$2
                """, actorId, idempotencyKey));

    private static async Task<DatabaseSnapshot> SnapshotAsync(NpgsqlDataSource dataSource, Guid caseId)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT c.status, c.urgent_status, c.row_version,
                   (SELECT COUNT(*) FROM witness.forms f WHERE f.case_id=c.id),
                   COALESCE((SELECT MAX(f.version) FROM witness.forms f WHERE f.case_id=c.id), 0),
                   COALESCE((SELECT string_agg(f.form_number::text || ':' || f.status || ':' || f.version::text || ':' || f.values_data::text, '|' ORDER BY f.form_number)
                             FROM witness.forms f WHERE f.case_id=c.id), ''),
                   (SELECT COUNT(*) FROM witness.form_signatures s JOIN witness.forms f ON f.id=s.form_id WHERE f.case_id=c.id),
                   (SELECT COUNT(*) FROM witness.workflow_events w WHERE w.case_id=c.id),
                   (SELECT COUNT(*) FROM witness.audit_events a WHERE a.case_id=c.id),
                   (SELECT COUNT(*) FROM witness.notifications n WHERE n.case_id=c.id),
                   (SELECT COUNT(*) FROM witness.protection_periods p WHERE p.case_id=c.id),
                   (SELECT COUNT(*) FROM witness.appeals ap WHERE ap.case_id=c.id),
                   (SELECT COUNT(*) FROM witness.idempotency_records i WHERE i.resource_id=c.id AND i.status='processing'),
                   COALESCE(c.appeal_deadline::text, '')
            FROM witness.cases c WHERE c.id=$1
            """);
        cmd.Parameters.AddWithValue(caseId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DatabaseSnapshot(
            reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt32(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11),
            reader.GetInt64(12), reader.GetString(13));
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

    private static async Task DeleteCaseAsync(NpgsqlDataSource dataSource, Guid caseId)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=$1");
        cmd.Parameters.AddWithValue(caseId);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record MutationCounts(long Cases, long Forms, long Workflows, long Audits, long Idempotency);

    private sealed record DatabaseSnapshot(
        string Status,
        string UrgentStatus,
        long CaseVersion,
        long FormCount,
        int MaxFormVersion,
        string FormState,
        long SignatureCount,
        long WorkflowCount,
        long AuditCount,
        long NotificationCount,
        long ProtectionPeriodCount,
        long AppealCount,
        long ProcessingIdempotencyCount,
        string AppealDeadline);
}
