using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Infrastructure;
using EcmisWitness.Api.Security;
using Npgsql;
using System.Globalization;

namespace EcmisWitness.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WitnessNoticeReceiptPostgresCollection
{
    public const string Name = "Witness notice receipt PostgreSQL";
}

[Collection(WitnessNoticeReceiptPostgresCollection.Name)]
public sealed class WitnessNoticeReceiptIntegrationTests
{
    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(17)]
    public async Task Receipt_before_sent_by_one_minute_is_rejected_without_business_mutation(int formNumber)
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = NoticeUser($"before-{formNumber}");
        var sentAt = DateTimeOffset.UtcNow.AddHours(-2);
        var fixture = await SeedNoticeCaseAsync(dataSource, actor, formNumber, sentAt);

        try
        {
            var before = await SnapshotAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
            var error = await Assert.ThrowsAsync<WitnessWorkflowException>(() => Repository(dataSource)
                .ExecuteCommandAsync(
                    fixture.CaseId,
                    ReceiptAction(formNumber),
                    ReceiptRequest(fixture, sentAt.AddMinutes(-1), $"E2E-WIT-028-BEFORE-{formNumber}"),
                    actor,
                    "127.0.0.1",
                    default));
            var after = await SnapshotAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);

            Assert.Contains("วันรับหนังสือต้องไม่ก่อนวันที่และเวลาส่งหนังสือ", error.Message);
            Assert.Equal(before, after);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
        }
    }

    [Fact]
    public async Task Kb9_receipt_equal_to_sent_is_allowed_without_appeal_deadline()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = NoticeUser("equal-kb9");
        var sentAt = DateTimeOffset.UtcNow.AddHours(-2);
        var fixture = await SeedNoticeCaseAsync(dataSource, actor, 9, sentAt);

        try
        {
            var result = await Repository(dataSource).ExecuteCommandAsync(
                fixture.CaseId,
                ReceiptAction(9),
                ReceiptRequest(fixture, sentAt, "E2E-WIT-028-EQUAL-KB9"),
                actor,
                "127.0.0.1",
                default);

            Assert.Equal(WitnessStatuses.ProtectionSetup, result.ToStatus);
            var after = await SnapshotAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
            Assert.Equal(8, after.CaseVersion);
            Assert.Equal(1, after.ReceivedDeliveryCount);
            Assert.Equal("", after.AppealDeadline);
            Assert.Equal(1, after.WorkflowCount);
            Assert.Equal(1, after.AuditCount);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
        }
    }

    [Fact]
    public async Task Kb10_receipt_after_sent_sets_deadline_from_validated_received_at()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = NoticeUser("after-kb10");
        var sentAt = DateTimeOffset.UtcNow.AddHours(-3);
        var receivedAt = sentAt.AddHours(1);
        var fixture = await SeedNoticeCaseAsync(dataSource, actor, 10, sentAt);

        try
        {
            var result = await Repository(dataSource).ExecuteCommandAsync(
                fixture.CaseId,
                ReceiptAction(10),
                ReceiptRequest(fixture, receivedAt, "E2E-WIT-028-AFTER-KB10"),
                actor,
                "127.0.0.1",
                default);

            Assert.Equal(WitnessStatuses.AppealWindow, result.ToStatus);
            var expectedDeadline = DateOnly.FromDateTime(
                receivedAt.ToOffset(TimeSpan.FromHours(7)).Date).AddDays(30)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var after = await SnapshotAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
            Assert.Equal(expectedDeadline, after.AppealDeadline);
            Assert.Equal(1, after.ReceivedDeliveryCount);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
        }
    }

    [Fact]
    public async Task Kb17_same_instant_with_different_timezone_offsets_is_allowed()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = NoticeUser("timezone-kb17");
        var instant = DateTimeOffset.UtcNow.AddHours(-2);
        var sentAt = instant.ToOffset(TimeSpan.FromHours(7));
        var receivedAt = instant.ToOffset(TimeSpan.FromHours(-5));
        var fixture = await SeedNoticeCaseAsync(dataSource, actor, 17, sentAt);

        try
        {
            var result = await Repository(dataSource).ExecuteCommandAsync(
                fixture.CaseId,
                ReceiptAction(17),
                ReceiptRequest(fixture, receivedAt, "E2E-WIT-028-TIMEZONE-KB17"),
                actor,
                "127.0.0.1",
                default);

            Assert.Equal(WitnessStatuses.AppealWindow, result.ToStatus);
            var after = await SnapshotAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
            Assert.Equal(1, after.ReceivedDeliveryCount);
            Assert.NotEqual("", after.AppealDeadline);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
        }
    }

    [Fact]
    public async Task Receipt_in_future_is_rejected_without_business_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = NoticeUser("future");
        var fixture = await SeedNoticeCaseAsync(dataSource, actor, 10, DateTimeOffset.UtcNow.AddHours(-1));

        try
        {
            var before = await SnapshotAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
            var error = await Assert.ThrowsAsync<WitnessWorkflowException>(() => Repository(dataSource)
                .ExecuteCommandAsync(
                    fixture.CaseId,
                    ReceiptAction(10),
                    ReceiptRequest(fixture, DateTimeOffset.UtcNow.AddHours(1), "E2E-WIT-028-FUTURE"),
                    actor,
                    "127.0.0.1",
                    default));
            var after = await SnapshotAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);

            Assert.Contains("วันรับหนังสือต้องไม่เป็นเวลาในอนาคต", error.Message);
            Assert.Equal(before, after);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
        }
    }

    [Fact]
    public async Task Missing_latest_delivery_is_rejected_without_creating_sla()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = NoticeUser("missing-delivery");
        var fixture = await SeedNoticeCaseAsync(
            dataSource, actor, 10, DateTimeOffset.UtcNow.AddHours(-2), includeDelivery: false);

        try
        {
            var before = await SnapshotAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
            var error = await Assert.ThrowsAsync<WitnessWorkflowException>(() => Repository(dataSource)
                .ExecuteCommandAsync(
                    fixture.CaseId,
                    ReceiptAction(10),
                    ReceiptRequest(fixture, DateTimeOffset.UtcNow.AddHours(-1), "E2E-WIT-028-NO-DELIVERY"),
                    actor,
                    "127.0.0.1",
                    default));
            var after = await SnapshotAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);

            Assert.Contains("ไม่พบประวัติการส่งหนังสือ", error.Message);
            Assert.Equal(before, after);
            Assert.Equal("", after.AppealDeadline);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
        }
    }

    [Fact]
    public async Task Same_idempotency_key_replays_receipt_without_duplicate_deadline_or_workflow()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = NoticeUser("replay");
        var sentAt = DateTimeOffset.UtcNow.AddHours(-2);
        var fixture = await SeedNoticeCaseAsync(dataSource, actor, 10, sentAt);
        var request = ReceiptRequest(fixture, sentAt.AddMinutes(1), "E2E-WIT-028-REPLAY");

        try
        {
            var first = await Repository(dataSource).ExecuteCommandAsync(
                fixture.CaseId, ReceiptAction(10), request, actor, "127.0.0.1", default);
            var replay = await Repository(dataSource).ExecuteCommandAsync(
                fixture.CaseId, ReceiptAction(10), request, actor, "127.0.0.1", default);

            Assert.Equal(first.CaseId, replay.CaseId);
            Assert.Equal(first.RequestNo, replay.RequestNo);
            Assert.Equal(first.FromStatus, replay.FromStatus);
            Assert.Equal(first.ToStatus, replay.ToStatus);
            Assert.Equal(first.Version, replay.Version);
            Assert.Equal(
                first.AvailableActions.Select(item => item.Code),
                replay.AvailableActions.Select(item => item.Code));
            var after = await SnapshotAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
            Assert.Equal(1, after.WorkflowCount);
            Assert.Equal(1, after.AuditCount);
            Assert.Equal(1, after.ReceivedDeliveryCount);
            Assert.Equal(1, after.IdempotencyCount);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
        }
    }

    [Fact]
    public async Task Concurrent_receipt_retries_create_one_deadline_and_one_workflow_event()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = NoticeUser("concurrent");
        var sentAt = DateTimeOffset.UtcNow.AddHours(-2);
        var fixture = await SeedNoticeCaseAsync(dataSource, actor, 17, sentAt);
        var request = ReceiptRequest(fixture, sentAt.AddMinutes(1), "E2E-WIT-028-CONCURRENT");

        try
        {
            using var gate = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(async () =>
                {
                    gate.Wait();
                    return await Repository(dataSource).ExecuteCommandAsync(
                        fixture.CaseId, ReceiptAction(17), request, actor, "127.0.0.1", default);
                }))
                .ToArray();
            gate.Set();
            var results = await Task.WhenAll(tasks);

            Assert.Single(results.Select(item => item.Version).Distinct());
            Assert.All(results, item => Assert.Equal(WitnessStatuses.AppealWindow, item.ToStatus));
            var after = await SnapshotAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
            Assert.Equal(1, after.WorkflowCount);
            Assert.Equal(1, after.AuditCount);
            Assert.Equal(1, after.ReceivedDeliveryCount);
            Assert.Equal(1, after.IdempotencyCount);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId, fixture.ControlCaseId);
        }
    }

    private static WitnessRepository Repository(NpgsqlDataSource dataSource)
        => new(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());

    private static WitnessUserContext NoticeUser(string suffix)
        => new(
            Guid.NewGuid(),
            $"e2e-wit028-{suffix}",
            $"E2E-TEST WIT028 {suffix}",
            "เจ้าหน้าที่แจ้งผลทดสอบ",
            new HashSet<string> { "notice_officer" },
            new HashSet<string> { WitnessPermissions.NoticeManage });

    private static string ReceiptAction(int formNumber)
        => formNumber == 9
            ? "record-notice-receipt-approved"
            : "record-notice-receipt-rejected";

    private static ExecuteWitnessCommandRequest ReceiptRequest(
        NoticeFixture fixture,
        DateTimeOffset receivedAt,
        string idempotencyKey)
        => new(
            "E2E-TEST บันทึกหลักฐานวันรับหนังสือ",
            7,
            new Dictionary<string, string>
            {
                ["received_at"] = receivedAt.ToString("O"),
                ["receipt_proof_attachment_id"] = fixture.AttachmentId.ToString(),
                ["test_batch_id"] = "E2E-WIT-20260721-028"
            },
            idempotencyKey);

    private static async Task<NoticeFixture> SeedNoticeCaseAsync(
        NpgsqlDataSource dataSource,
        WitnessUserContext actor,
        int formNumber,
        DateTimeOffset sentAt,
        bool includeDelivery = true)
    {
        var caseId = Guid.NewGuid();
        var controlCaseId = Guid.NewGuid();
        var formId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        foreach (var row in new[]
                 {
                     (caseId, $"E2E-WIT028-{suffix}", "E2E-TEST WIT028 target"),
                     (controlCaseId, $"E2E-WIT028-C-{suffix}", "E2E-TEST WIT028 control")
                 })
        {
            await using var caseCmd = new NpgsqlCommand("""
                INSERT INTO witness.cases(
                    id, request_no, intake_form_number, status, urgent_status,
                    current_owner_role, current_owner_user_id, current_owner_name,
                    risk_level, is_urgent, summary_data, row_version,
                    created_by, created_by_name, created_at, updated_at)
                VALUES($1,$2,1,'notice_sent','none','notice_officer',$3,$4,
                       'กลาง',false,jsonb_build_object('test_title',$5),7,
                       $3,$4,NOW(),NOW())
                """, connection, tx);
            caseCmd.Parameters.AddWithValue(row.Item1);
            caseCmd.Parameters.AddWithValue(row.Item2);
            caseCmd.Parameters.AddWithValue(actor.UserId);
            caseCmd.Parameters.AddWithValue(actor.DisplayName);
            caseCmd.Parameters.AddWithValue(row.Item3);
            await caseCmd.ExecuteNonQueryAsync();
        }

        await using (var formCmd = new NpgsqlCommand("""
            INSERT INTO witness.forms(
                id, case_id, form_number, version, status, values_data,
                updated_by, updated_by_name, updated_at)
            VALUES($1,$2,$3,1,'completed','{}'::jsonb,$4,$5,NOW())
            """, connection, tx))
        {
            formCmd.Parameters.AddWithValue(formId);
            formCmd.Parameters.AddWithValue(caseId);
            formCmd.Parameters.AddWithValue(formNumber);
            formCmd.Parameters.AddWithValue(actor.UserId);
            formCmd.Parameters.AddWithValue(actor.DisplayName);
            await formCmd.ExecuteNonQueryAsync();
        }

        await using (var attachmentCmd = new NpgsqlCommand("""
            INSERT INTO witness.attachments(
                id, case_id, form_number, form_version, file_name, content_type,
                size_bytes, sha256, classification, content,
                uploaded_by, uploaded_by_name, uploaded_at)
            VALUES($1,$2,$3,1,'E2E-TEST-receipt.pdf','application/pdf',4,
                   repeat('a',64),'ลับ',decode('25504446','hex'),$4,$5,NOW())
            """, connection, tx))
        {
            attachmentCmd.Parameters.AddWithValue(attachmentId);
            attachmentCmd.Parameters.AddWithValue(caseId);
            attachmentCmd.Parameters.AddWithValue(formNumber);
            attachmentCmd.Parameters.AddWithValue(actor.UserId);
            attachmentCmd.Parameters.AddWithValue(actor.DisplayName);
            await attachmentCmd.ExecuteNonQueryAsync();
        }

        if (includeDelivery)
        {
            await using var deliveryCmd = new NpgsqlCommand("""
                INSERT INTO witness.notice_deliveries(
                    id, case_id, form_number, sent_at, delivery_channel, recipient,
                    tracking_reference, created_by, created_by_name, created_at)
                VALUES($1,$2,$3,$4,'direct','E2E-TEST ผู้รับ','E2E-TEST-TRACK',
                       $5,$6,NOW())
                """, connection, tx);
            deliveryCmd.Parameters.AddWithValue(deliveryId);
            deliveryCmd.Parameters.AddWithValue(caseId);
            deliveryCmd.Parameters.AddWithValue(formNumber);
            deliveryCmd.Parameters.AddWithValue(sentAt.ToUniversalTime());
            deliveryCmd.Parameters.AddWithValue(actor.UserId);
            deliveryCmd.Parameters.AddWithValue(actor.DisplayName);
            await deliveryCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return new NoticeFixture(caseId, controlCaseId, attachmentId);
    }

    private static async Task<ReceiptDbSnapshot> SnapshotAsync(
        NpgsqlDataSource dataSource,
        Guid caseId,
        Guid controlCaseId)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT c.status,
                   c.row_version,
                   COALESCE(c.notice_received_at::text,''),
                   COALESCE(c.appeal_deadline::text,''),
                   (SELECT COUNT(*)::int FROM witness.notice_deliveries d WHERE d.case_id=c.id),
                   (SELECT COUNT(*)::int FROM witness.notice_deliveries d WHERE d.case_id=c.id AND d.received_at IS NOT NULL),
                   (SELECT COUNT(*)::int FROM witness.forms f WHERE f.case_id=c.id),
                   (SELECT COALESCE(SUM(f.version),0)::int FROM witness.forms f WHERE f.case_id=c.id),
                   (SELECT COUNT(*)::int FROM witness.form_signatures s JOIN witness.forms f ON f.id=s.form_id WHERE f.case_id=c.id),
                   (SELECT COUNT(*)::int FROM witness.workflow_events e WHERE e.case_id=c.id),
                   (SELECT COUNT(*)::int FROM witness.audit_events a WHERE a.case_id=c.id),
                   (SELECT COUNT(*)::int FROM witness.idempotency_records i WHERE i.resource_id=c.id),
                   (SELECT row_version FROM witness.cases WHERE id=$2)
            FROM witness.cases c
            WHERE c.id=$1
            """);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(controlCaseId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ReceiptDbSnapshot(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt64(12));
    }

    private static async Task DeleteCasesAsync(NpgsqlDataSource dataSource, params Guid[] caseIds)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=ANY($1)");
        cmd.Parameters.AddWithValue(caseIds.Distinct().ToArray());
        await cmd.ExecuteNonQueryAsync();
    }

    private static string? ConnectionString()
        => Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");

    private sealed record NoticeFixture(Guid CaseId, Guid ControlCaseId, Guid AttachmentId);

    private sealed record ReceiptDbSnapshot(
        string Status,
        long CaseVersion,
        string NoticeReceivedAt,
        string AppealDeadline,
        int DeliveryCount,
        int ReceivedDeliveryCount,
        int FormCount,
        int FormVersionSum,
        int SignatureCount,
        int WorkflowCount,
        int AuditCount,
        int IdempotencyCount,
        long ControlCaseVersion);
}
