using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Infrastructure;
using EcmisWitness.Api.Security;
using Npgsql;

namespace EcmisWitness.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WitnessAppealResultNoticePostgresCollection
{
    public const string Name = "Witness appeal result notice PostgreSQL";
}

[Collection(WitnessAppealResultNoticePostgresCollection.Name)]
public sealed class WitnessAppealResultNoticeIntegrationTests
{
    private static readonly Guid OrganizationA = Guid.Parse("92000000-0000-4000-8000-000000000001");
    private static readonly Guid OrganizationB = Guid.Parse("92000000-0000-4000-8000-000000000002");
    private static readonly byte[] ProofBytes = "%PDF-1.4\nE2E-TEST appeal result delivery proof - no real PII\n%%EOF"u8.ToArray();

    [Fact]
    public async Task Migration_is_rerun_safe_and_preserves_existing_notice_and_attachment_rows()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var before = await InventoryAsync(dataSource);

        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();

        Assert.Equal(before, await InventoryAsync(dataSource));
        Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
            SELECT COUNT(*) FROM witness.schema_migrations
            WHERE version='015_appeal_result_notice_lifecycle'
            """));
        Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
            SELECT COUNT(*) FROM pg_constraint
            WHERE conname='fk_witness_appeal_result_notice_proof'
              AND conrelid='witness.appeal_result_notices'::regclass
            """));
    }

    [Theory]
    [InlineData("appeal-upheld", "appeal_decided", "closed")]
    [InlineData("appeal-reversed", "approved_pending_notice", "approved_pending_notice")]
    public async Task Final_result_can_be_notified_received_and_completed_without_new_appeal_deadline(
        string decision,
        string initialStatus,
        string expectedFinalStatus)
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = AppealUser($"lifecycle-{decision}", OrganizationA);
        var fixture = await SeedFinalAppealCaseAsync(dataSource, actor, initialStatus, decision);

        try
        {
            var repository = Repository(dataSource);
            var proof = await UploadProofAsync(repository, fixture, actor, "send");
            var version = await CaseVersionAsync(dataSource, fixture.CaseId);
            var sentAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            var sent = await repository.ExecuteCommandAsync(
                fixture.CaseId,
                "notify-appeal-result",
                NotifyRequest(fixture, proof.Id, sentAt, version, "ไปรษณีย์ตอบรับ"),
                actor,
                "127.0.0.1",
                default);
            Assert.Equal(WitnessStatuses.AppealResultNoticeSent, sent.ToStatus);

            var receiptProof = await UploadProofAsync(repository, fixture, actor, "receipt");
            version = await CaseVersionAsync(dataSource, fixture.CaseId);
            var received = await repository.ExecuteCommandAsync(
                fixture.CaseId,
                "record-appeal-result-receipt",
                ReceiptRequest(fixture, sentAt.AddMinutes(2), version, receiptProof.Id),
                actor,
                "127.0.0.1",
                default);
            Assert.Equal(WitnessStatuses.AppealResultReceived, received.ToStatus);

            var completed = await repository.ExecuteCommandAsync(
                fixture.CaseId,
                "complete-appeal-result-notification",
                CompleteRequest(fixture, received.Version),
                actor,
                "127.0.0.1",
                default);
            Assert.Equal(expectedFinalStatus, completed.ToStatus);

            var state = await StateAsync(dataSource, fixture.CaseId, fixture.AppealId);
            Assert.Equal(expectedFinalStatus, state.CaseStatus);
            Assert.Equal("decided", state.AppealStatus);
            Assert.Equal(decision, state.Decision);
            Assert.Equal("completed", state.NoticeStatus);
            Assert.NotEqual("", state.ReceivedAt);
            Assert.Equal("", state.AppealDeadline);
            Assert.Equal(1, state.NoticeCount);
            Assert.Equal(3, state.LifecycleWorkflowCount);
            Assert.Equal(3, state.LifecycleAuditCount);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Theory]
    [InlineData("ส่งมอบด้วยตนเอง")]
    [InlineData("ไปรษณีย์ตอบรับ")]
    [InlineData("อีเมลและหนังสือตาม")]
    public async Task Existing_delivery_channels_are_supported(string channel)
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = AppealUser($"channel-{Guid.NewGuid():N}", OrganizationA);
        var fixture = await SeedFinalAppealCaseAsync(
            dataSource, actor, WitnessStatuses.AppealDecided, "appeal-upheld");

        try
        {
            var repository = Repository(dataSource);
            var proof = await UploadProofAsync(repository, fixture, actor, "channel");
            var result = await repository.ExecuteCommandAsync(
                fixture.CaseId,
                "notify-appeal-result",
                NotifyRequest(
                    fixture, proof.Id, DateTimeOffset.UtcNow.AddMinutes(-1),
                    await CaseVersionAsync(dataSource, fixture.CaseId), channel),
                actor,
                "127.0.0.1",
                default);

            Assert.Equal(WitnessStatuses.AppealResultNoticeSent, result.ToStatus);
            Assert.Equal(channel, await ScalarAsync<string>(dataSource, """
                SELECT delivery_channel FROM witness.appeal_result_notices WHERE appeal_id=$1
                """, fixture.AppealId));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Fact]
    public async Task Return_for_revision_cannot_be_notified_and_negative_request_has_no_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = AppealUser("revision", OrganizationA);
        var fixture = await SeedNonFinalAppealCaseAsync(dataSource, actor);

        try
        {
            var before = await SnapshotAsync(dataSource, fixture.CaseId);
            var error = await Assert.ThrowsAsync<WitnessWorkflowException>(() => Repository(dataSource)
                .ExecuteCommandAsync(
                    fixture.CaseId,
                    "notify-appeal-result",
                    new ExecuteWitnessCommandRequest(
                        "E2E-TEST invalid final notification",
                        fixture.CaseVersion,
                        new Dictionary<string, string>
                        {
                            ["appeal_id"] = fixture.AppealId.ToString(),
                            ["recipient"] = "E2E-TEST recipient",
                            ["delivery_channel"] = "ส่งมอบด้วยตนเอง",
                            ["sent_at"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
                            ["proof_attachment_id"] = Guid.NewGuid().ToString()
                        },
                        $"E2E-WIT-036-REVISION-{Guid.NewGuid():N}"),
                    actor,
                    "127.0.0.1",
                    default));

            Assert.Contains("ผลอุทธรณ์ยังไม่เป็นผลชี้ขาด", error.Message);
            Assert.Equal(before, await SnapshotAsync(dataSource, fixture.CaseId));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Fact]
    public async Task Receipt_chronology_future_missing_notice_and_early_close_are_rejected_without_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = AppealUser("chronology", OrganizationA);
        var fixture = await SeedFinalAppealCaseAsync(
            dataSource, actor, WitnessStatuses.AppealDecided, "appeal-upheld");

        try
        {
            var beforeNotice = await SnapshotAsync(dataSource, fixture.CaseId);
            var missing = await Assert.ThrowsAsync<WitnessWorkflowException>(() => Repository(dataSource)
                .ExecuteCommandAsync(
                    fixture.CaseId,
                    "record-appeal-result-receipt",
                    ReceiptRequest(fixture, DateTimeOffset.UtcNow.AddMinutes(-1), fixture.CaseVersion),
                    actor,
                    "127.0.0.1",
                    default));
            Assert.Contains("ยังไม่ได้ส่งหนังสือแจ้งผลอุทธรณ์", missing.Message);
            Assert.Equal(beforeNotice, await SnapshotAsync(dataSource, fixture.CaseId));

            var prematureClose = await Assert.ThrowsAsync<WitnessWorkflowException>(() => Repository(dataSource)
                .ExecuteCommandAsync(
                    fixture.CaseId,
                    "complete-appeal-result-notification",
                    CompleteRequest(fixture, fixture.CaseVersion),
                    actor,
                    "127.0.0.1",
                    default));
            Assert.Contains("ยังไม่ได้ส่งหนังสือแจ้งผลอุทธรณ์", prematureClose.Message);
            Assert.Equal(beforeNotice, await SnapshotAsync(dataSource, fixture.CaseId));

            await Assert.ThrowsAsync<WitnessWorkflowException>(() => Repository(dataSource)
                .ExecuteCommandAsync(
                    fixture.CaseId,
                    "close-no-appeal",
                    new ExecuteWitnessCommandRequest(
                        "E2E-TEST ต้องห้ามปิดด้วย action เดิม",
                        fixture.CaseVersion,
                        null,
                        $"E2E-WIT-036-OLD-CLOSE-{Guid.NewGuid():N}"),
                    actor,
                    "127.0.0.1",
                    default));
            Assert.Equal(beforeNotice, await SnapshotAsync(dataSource, fixture.CaseId));

            var repository = Repository(dataSource);
            var proof = await UploadProofAsync(repository, fixture, actor, "chronology");
            var sentAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var sent = await repository.ExecuteCommandAsync(
                fixture.CaseId,
                "notify-appeal-result",
                NotifyRequest(
                    fixture, proof.Id, sentAt,
                    await CaseVersionAsync(dataSource, fixture.CaseId), "ส่งมอบด้วยตนเอง"),
                actor,
                "127.0.0.1",
                default);
            var afterSend = await SnapshotAsync(dataSource, fixture.CaseId);

            var before = await Assert.ThrowsAsync<WitnessWorkflowException>(() => Repository(dataSource)
                .ExecuteCommandAsync(
                    fixture.CaseId,
                    "record-appeal-result-receipt",
                    ReceiptRequest(fixture, sentAt.AddMinutes(-1), sent.Version),
                    actor,
                    "127.0.0.1",
                    default));
            Assert.Contains("วันรับผลอุทธรณ์ต้องไม่ก่อนวันที่และเวลาส่งหนังสือ", before.Message);
            Assert.Equal(afterSend, await SnapshotAsync(dataSource, fixture.CaseId));

            var future = await Assert.ThrowsAsync<WitnessWorkflowException>(() => Repository(dataSource)
                .ExecuteCommandAsync(
                    fixture.CaseId,
                    "record-appeal-result-receipt",
                    ReceiptRequest(fixture, DateTimeOffset.UtcNow.AddHours(1), sent.Version),
                    actor,
                    "127.0.0.1",
                    default));
            Assert.Contains("วันรับผลอุทธรณ์ต้องไม่เป็นเวลาในอนาคต", future.Message);
            Assert.Equal(afterSend, await SnapshotAsync(dataSource, fixture.CaseId));

            var earlyClose = await Assert.ThrowsAsync<WitnessWorkflowException>(() => Repository(dataSource)
                .ExecuteCommandAsync(
                    fixture.CaseId,
                    "complete-appeal-result-notification",
                    CompleteRequest(fixture, sent.Version),
                    actor,
                    "127.0.0.1",
                    default));
            Assert.Contains("ยังไม่ได้บันทึกหลักฐานการรับผลอุทธรณ์", earlyClose.Message);
            Assert.Equal(afterSend, await SnapshotAsync(dataSource, fixture.CaseId));

            var equal = await repository.ExecuteCommandAsync(
                fixture.CaseId,
                "record-appeal-result-receipt",
                ReceiptRequest(fixture, sentAt, sent.Version),
                actor,
                "127.0.0.1",
                default);
            Assert.Equal(WitnessStatuses.AppealResultReceived, equal.ToStatus);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Fact]
    public async Task Same_instant_with_different_timezone_offsets_is_allowed()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = AppealUser("timezone", OrganizationA);
        var fixture = await SeedFinalAppealCaseAsync(
            dataSource, actor, WitnessStatuses.AppealDecided, "appeal-upheld");

        try
        {
            var repository = Repository(dataSource);
            var proof = await UploadProofAsync(repository, fixture, actor, "timezone");
            var instant = DateTimeOffset.UtcNow.AddMinutes(-5);
            var sent = await repository.ExecuteCommandAsync(
                fixture.CaseId,
                "notify-appeal-result",
                NotifyRequest(
                    fixture, proof.Id, instant.ToOffset(TimeSpan.FromHours(7)),
                    await CaseVersionAsync(dataSource, fixture.CaseId), "ส่งมอบด้วยตนเอง"),
                actor,
                "127.0.0.1",
                default);
            var received = await repository.ExecuteCommandAsync(
                fixture.CaseId,
                "record-appeal-result-receipt",
                ReceiptRequest(fixture, instant.ToOffset(TimeSpan.FromHours(-5)), sent.Version),
                actor,
                "127.0.0.1",
                default);

            Assert.Equal(WitnessStatuses.AppealResultReceived, received.ToStatus);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Fact]
    public async Task Future_sent_at_is_rejected_without_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = AppealUser("future-sent", OrganizationA);
        var fixture = await SeedFinalAppealCaseAsync(
            dataSource, actor, WitnessStatuses.AppealDecided, "appeal-upheld");

        try
        {
            var repository = Repository(dataSource);
            var proof = await UploadProofAsync(repository, fixture, actor, "future-sent");
            var before = await SnapshotAsync(dataSource, fixture.CaseId);
            var version = await CaseVersionAsync(dataSource, fixture.CaseId);
            var error = await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.ExecuteCommandAsync(
                fixture.CaseId,
                "notify-appeal-result",
                NotifyRequest(
                    fixture, proof.Id, DateTimeOffset.UtcNow.AddHours(1),
                    version, "ส่งมอบด้วยตนเอง"),
                actor,
                "127.0.0.1",
                default));

            Assert.Contains("วันส่งหนังสือแจ้งผลอุทธรณ์ต้องไม่เป็นเวลาในอนาคต", error.Message);
            Assert.Equal(before, await SnapshotAsync(dataSource, fixture.CaseId));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Fact]
    public async Task Exact_receipt_retry_replays_without_duplicate_workflow_or_audit()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = AppealUser("receipt-retry", OrganizationA);
        var fixture = await SeedFinalAppealCaseAsync(
            dataSource, actor, WitnessStatuses.AppealDecided, "appeal-upheld");

        try
        {
            var repository = Repository(dataSource);
            var proof = await UploadProofAsync(repository, fixture, actor, "receipt-retry");
            var sentAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var sent = await repository.ExecuteCommandAsync(
                fixture.CaseId,
                "notify-appeal-result",
                NotifyRequest(
                    fixture, proof.Id, sentAt,
                    await CaseVersionAsync(dataSource, fixture.CaseId), "ส่งมอบด้วยตนเอง"),
                actor,
                "127.0.0.1",
                default);
            var key = $"E2E-WIT-036-RECEIPT-RETRY-{Guid.NewGuid():N}";
            var request = ReceiptRequest(fixture, sentAt.AddMinutes(1), sent.Version, key: key);
            var first = await repository.ExecuteCommandAsync(
                fixture.CaseId, "record-appeal-result-receipt", request,
                actor, "127.0.0.1", default);
            var replay = await repository.ExecuteCommandAsync(
                fixture.CaseId, "record-appeal-result-receipt", request,
                actor, "127.0.0.1", default);

            Assert.Equal(first.Version, replay.Version);
            Assert.Equal(first.ToStatus, replay.ToStatus);
            Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.workflow_events
                WHERE case_id=$1 AND action='record-appeal-result-receipt'
                """, fixture.CaseId));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.audit_events
                WHERE case_id=$1 AND action='appeal.result.notice.received'
                """, fixture.CaseId));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.idempotency_records
                WHERE resource_id=$1 AND idempotency_key=$2 AND status='completed'
                """, fixture.CaseId, key));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Fact]
    public async Task Unauthorized_cross_org_and_cross_appeal_proof_are_rejected_without_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = AppealUser("security", OrganizationA);
        var caseA = await SeedFinalAppealCaseAsync(
            dataSource, actor, WitnessStatuses.AppealDecided, "appeal-upheld");
        var caseB = await SeedFinalAppealCaseAsync(
            dataSource, actor, WitnessStatuses.AppealDecided, "appeal-upheld");

        try
        {
            var repository = Repository(dataSource);
            var proofB = await UploadProofAsync(repository, caseB, actor, "case-b");
            var before = await SnapshotAsync(dataSource, caseA.CaseId, caseB.CaseId);
            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.ExecuteCommandAsync(
                caseA.CaseId,
                "notify-appeal-result",
                NotifyRequest(
                    caseA, proofB.Id, DateTimeOffset.UtcNow.AddMinutes(-1), caseA.CaseVersion,
                    "ส่งมอบด้วยตนเอง"),
                actor,
                "127.0.0.1",
                default));
            Assert.Equal(before, await SnapshotAsync(dataSource, caseA.CaseId, caseB.CaseId));

            var readOnly = actor with
            {
                UserId = Guid.NewGuid(),
                Username = "e2e-view-only",
                Permissions = new HashSet<string> { WitnessPermissions.ViewMasked },
                Roles = new HashSet<string> { "super_admin" }
            };
            await Assert.ThrowsAsync<WitnessAuthorizationException>(() => repository.ExecuteCommandAsync(
                caseA.CaseId,
                "notify-appeal-result",
                NotifyRequest(
                    caseA, proofB.Id, DateTimeOffset.UtcNow.AddMinutes(-1), caseA.CaseVersion,
                    "ส่งมอบด้วยตนเอง"),
                readOnly,
                "127.0.0.1",
                default));
            Assert.Equal(before, await SnapshotAsync(dataSource, caseA.CaseId, caseB.CaseId));

            var crossOrg = AppealUser("cross-org", OrganizationB);
            await Assert.ThrowsAsync<WitnessAuthorizationException>(() => repository.ExecuteCommandAsync(
                caseA.CaseId,
                "notify-appeal-result",
                NotifyRequest(
                    caseA, proofB.Id, DateTimeOffset.UtcNow.AddMinutes(-1), caseA.CaseVersion,
                    "ส่งมอบด้วยตนเอง"),
                crossOrg,
                "127.0.0.1",
                default));
            Assert.Equal(before, await SnapshotAsync(dataSource, caseA.CaseId, caseB.CaseId));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, caseA.CaseId, caseB.CaseId);
        }
    }

    [Fact]
    public async Task Exact_and_concurrent_retries_create_one_notice_workflow_and_audit()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = AppealUser("idempotency", OrganizationA);
        var fixture = await SeedFinalAppealCaseAsync(
            dataSource, actor, WitnessStatuses.AppealDecided, "appeal-upheld");

        try
        {
            var repository = Repository(dataSource);
            var proof = await UploadProofAsync(repository, fixture, actor, "idempotency");
            var key = $"E2E-WIT-036-NOTIFY-{Guid.NewGuid():N}";
            var request = NotifyRequest(
                fixture, proof.Id, DateTimeOffset.UtcNow.AddMinutes(-1),
                await CaseVersionAsync(dataSource, fixture.CaseId), "ไปรษณีย์ตอบรับ", key);
            using var gate = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            {
                gate.Wait();
                return await Repository(dataSource).ExecuteCommandAsync(
                    fixture.CaseId, "notify-appeal-result", request,
                    actor, "127.0.0.1", default);
            })).ToArray();
            gate.Set();
            var results = await Task.WhenAll(tasks);

            Assert.All(results, item => Assert.Equal(WitnessStatuses.AppealResultNoticeSent, item.ToStatus));
            Assert.Single(results.Select(item => item.Version).Distinct());
            Assert.Equal(1L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.appeal_result_notices WHERE case_id=$1", fixture.CaseId));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.workflow_events
                WHERE case_id=$1 AND action='notify-appeal-result'
                """, fixture.CaseId));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.audit_events
                WHERE case_id=$1 AND action='appeal.result.notice.sent'
                """, fixture.CaseId));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.idempotency_records
                WHERE resource_id=$1 AND idempotency_key=$2 AND status='completed'
                """, fixture.CaseId, key));

            var replay = await repository.ExecuteCommandAsync(
                fixture.CaseId, "notify-appeal-result", request,
                actor, "127.0.0.1", default);
            Assert.Equal(results[0].Version, replay.Version);

            var conflict = request with
            {
                Data = new Dictionary<string, string>(request.Data!)
                {
                    ["recipient"] = "E2E-TEST different recipient"
                }
            };
            await Assert.ThrowsAsync<WitnessIdempotencyConflictException>(() => repository.ExecuteCommandAsync(
                fixture.CaseId, "notify-appeal-result", conflict,
                actor, "127.0.0.1", default));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Fact]
    public async Task Failure_mid_transaction_rolls_back_notice_workflow_audit_and_idempotency()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var actor = AppealUser("rollback", OrganizationA);
        var fixture = await SeedFinalAppealCaseAsync(
            dataSource, actor, WitnessStatuses.AppealDecided, "appeal-upheld");

        try
        {
            var repository = Repository(dataSource);
            var proof = await UploadProofAsync(repository, fixture, actor, "rollback");
            var before = await SnapshotAsync(dataSource, fixture.CaseId);
            var key = $"E2E-WIT-036-ROLLBACK-{Guid.NewGuid():N}";
            var badActor = actor with { DisplayName = new string('ก', 251) };
            var version = await CaseVersionAsync(dataSource, fixture.CaseId);

            await Assert.ThrowsAsync<PostgresException>(() => repository.ExecuteCommandAsync(
                fixture.CaseId,
                "notify-appeal-result",
                NotifyRequest(
                    fixture, proof.Id, DateTimeOffset.UtcNow.AddMinutes(-1),
                    version, "ส่งมอบด้วยตนเอง", key),
                badActor,
                "127.0.0.1",
                default));

            Assert.Equal(before, await SnapshotAsync(dataSource, fixture.CaseId));
            Assert.Equal(0L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.idempotency_records WHERE idempotency_key=$1
                """, key));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    private static WitnessRepository Repository(NpgsqlDataSource dataSource)
        => new(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());

    private static WitnessUserContext AppealUser(string suffix, Guid organizationId)
        => new(
            Guid.NewGuid(), $"e2e-appeal-result-{suffix}", $"E2E-TEST Appeal Result {suffix}",
            "เจ้าหน้าที่รับอุทธรณ์",
            new HashSet<string> { "appeal_officer" },
            new HashSet<string>
            {
                WitnessPermissions.AppealManage,
                WitnessPermissions.DocumentDownload,
                WitnessPermissions.ViewMasked
            },
            organizationId, "E2E-TEST ORG", "workgroup");

    private static async Task<WitnessAttachmentDto> UploadProofAsync(
        WitnessRepository repository,
        AppealFixture fixture,
        WitnessUserContext actor,
        string suffix)
        => await repository.AddAttachmentAsync(
            fixture.CaseId, null, null, fixture.AppealId,
            "appeal_result_notice_proof", $"E2E-WIT-036-PROOF-{suffix}-{Guid.NewGuid():N}",
            $"E2E-TEST-appeal-result-{suffix}.pdf", "application/pdf", ProofBytes,
            actor, "127.0.0.1", default);

    private static ExecuteWitnessCommandRequest NotifyRequest(
        AppealFixture fixture,
        Guid proofAttachmentId,
        DateTimeOffset sentAt,
        long version,
        string channel,
        string? key = null)
        => new(
            "E2E-TEST ส่งหนังสือแจ้งผลอุทธรณ์",
            version,
            new Dictionary<string, string>
            {
                ["appeal_id"] = fixture.AppealId.ToString(),
                ["recipient"] = "E2E-TEST ผู้ยื่นอุทธรณ์",
                ["delivery_channel"] = channel,
                ["sent_at"] = sentAt.ToString("O"),
                ["proof_attachment_id"] = proofAttachmentId.ToString(),
                ["external_reference"] = fixture.ExternalReference
            },
            key ?? $"E2E-WIT-036-NOTIFY-{Guid.NewGuid():N}");

    private static ExecuteWitnessCommandRequest ReceiptRequest(
        AppealFixture fixture,
        DateTimeOffset receivedAt,
        long version,
        Guid? proofAttachmentId = null,
        string? key = null)
    {
        var data = new Dictionary<string, string>
        {
            ["appeal_id"] = fixture.AppealId.ToString(),
            ["received_at"] = receivedAt.ToString("O"),
            ["actual_recipient"] = "E2E-TEST ผู้รับจริง",
            ["receipt_note"] = "E2E-TEST รับหนังสือแล้ว"
        };
        if (proofAttachmentId.HasValue)
            data["receipt_proof_attachment_id"] = proofAttachmentId.Value.ToString();
        return new ExecuteWitnessCommandRequest(
            "E2E-TEST บันทึกวันรับผลอุทธรณ์",
            version,
            data,
            key ?? $"E2E-WIT-036-RECEIPT-{Guid.NewGuid():N}");
    }

    private static ExecuteWitnessCommandRequest CompleteRequest(
        AppealFixture fixture,
        long version,
        string? key = null)
        => new(
            "E2E-TEST ปิดกระบวนการแจ้งผลอุทธรณ์",
            version,
            new Dictionary<string, string> { ["appeal_id"] = fixture.AppealId.ToString() },
            key ?? $"E2E-WIT-036-COMPLETE-{Guid.NewGuid():N}");

    private static async Task<AppealFixture> SeedFinalAppealCaseAsync(
        NpgsqlDataSource dataSource,
        WitnessUserContext actor,
        string caseStatus,
        string decision)
    {
        var caseId = Guid.NewGuid();
        var appealId = Guid.NewGuid();
        var externalResultId = Guid.NewGuid();
        var externalReference = $"E2E-TEST-EXT-{Guid.NewGuid():N}";
        const long version = 10;
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await InsertCaseAsync(connection, tx, caseId, actor, caseStatus, version);
        await using (var appeal = new NpgsqlCommand("""
            INSERT INTO witness.appeals(
                id, case_id, filed_at, filed_channel, statement, late_reason,
                is_late, status, external_reference, decision, row_version,
                decided_at, created_by, created_at, updated_at)
            VALUES($1,$2,NOW()-INTERVAL '3 day','หนังสือ','E2E-TEST appeal',NULL,
                   false,'decided',$3,$4,2,NOW()-INTERVAL '1 day',$5,
                   NOW()-INTERVAL '3 day',NOW()-INTERVAL '1 day')
            """, connection, tx))
        {
            appeal.Parameters.AddWithValue(appealId);
            appeal.Parameters.AddWithValue(caseId);
            appeal.Parameters.AddWithValue(externalReference);
            appeal.Parameters.AddWithValue(decision);
            appeal.Parameters.AddWithValue(actor.UserId);
            await appeal.ExecuteNonQueryAsync();
        }
        await using (var result = new NpgsqlCommand("""
            INSERT INTO witness.external_results(
                id, case_id, result_type, reference_no, decision_at, reason,
                payload, received_by, received_by_name, received_at)
            VALUES($1,$2,$3,$4,NOW()-INTERVAL '1 day','E2E-TEST final result',
                   '{}'::jsonb,$5,$6,NOW()-INTERVAL '1 day')
            """, connection, tx))
        {
            result.Parameters.AddWithValue(externalResultId);
            result.Parameters.AddWithValue(caseId);
            result.Parameters.AddWithValue(decision);
            result.Parameters.AddWithValue(externalReference);
            result.Parameters.AddWithValue(actor.UserId);
            result.Parameters.AddWithValue(actor.DisplayName);
            await result.ExecuteNonQueryAsync();
        }
        await InsertAssignmentAsync(connection, tx, caseId, actor);
        await tx.CommitAsync();
        return new AppealFixture(caseId, appealId, version, externalReference);
    }

    private static async Task<AppealFixture> SeedNonFinalAppealCaseAsync(
        NpgsqlDataSource dataSource,
        WitnessUserContext actor)
    {
        var caseId = Guid.NewGuid();
        var appealId = Guid.NewGuid();
        const long version = 10;
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await InsertCaseAsync(connection, tx, caseId, actor, WitnessStatuses.AppealReceived, version);
        await using (var appeal = new NpgsqlCommand("""
            INSERT INTO witness.appeals(
                id, case_id, filed_at, filed_channel, statement, late_reason,
                is_late, status, external_reference, decision, row_version,
                decided_at, created_by, created_at, updated_at)
            VALUES($1,$2,NOW()-INTERVAL '3 day','หนังสือ','E2E-TEST appeal revision',NULL,
                   false,'received','E2E-TEST-RETURN-REF',NULL,2,NULL,$3,
                   NOW()-INTERVAL '3 day',NOW()-INTERVAL '1 day')
            """, connection, tx))
        {
            appeal.Parameters.AddWithValue(appealId);
            appeal.Parameters.AddWithValue(caseId);
            appeal.Parameters.AddWithValue(actor.UserId);
            await appeal.ExecuteNonQueryAsync();
        }
        await InsertAssignmentAsync(connection, tx, caseId, actor);
        await tx.CommitAsync();
        return new AppealFixture(caseId, appealId, version, "E2E-TEST-RETURN-REF");
    }

    private static async Task InsertCaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        WitnessUserContext actor,
        string status,
        long version)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO witness.cases(
                id, request_no, intake_form_number, status, urgent_status,
                current_owner_role, current_owner_user_id, current_owner_name,
                risk_level, is_urgent, summary_data, row_version,
                created_by, created_by_name, owning_org_id, current_owner_org_id,
                owning_org_name, current_owner_org_name, created_at, updated_at)
            VALUES($1,$2,1,$3,'none','appeal_officer',$4,$5,'ปานกลาง',false,
                   '{"witness_name":"E2E-TEST"}'::jsonb,$6,$4,$5,$7,$7,
                   'E2E-TEST ORG','E2E-TEST ORG',NOW(),NOW())
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue($"E2E-TEST-{Guid.NewGuid():N}"[..40]);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue(actor.UserId);
        cmd.Parameters.AddWithValue(actor.DisplayName);
        cmd.Parameters.AddWithValue(version);
        cmd.Parameters.AddWithValue(OrganizationA);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        WitnessUserContext actor)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO witness.case_assignments(
                id, case_id, user_id, target_username, assignment_role, org_id,
                organization_name, reason, assigned_by, assigned_by_name, assigned_at)
            VALUES($1,$2,$3,$4,'appeal_officer',$5,'E2E-TEST ORG',
                   'E2E-TEST appeal assignment',$3,$6,NOW())
            """, connection, tx);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(actor.UserId);
        cmd.Parameters.AddWithValue(actor.Username);
        cmd.Parameters.AddWithValue(OrganizationA);
        cmd.Parameters.AddWithValue(actor.DisplayName);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CaseVersionAsync(NpgsqlDataSource dataSource, Guid caseId)
        => await ScalarAsync<long>(dataSource,
            "SELECT row_version FROM witness.cases WHERE id=$1", caseId);

    private static async Task<LifecycleState> StateAsync(
        NpgsqlDataSource dataSource,
        Guid caseId,
        Guid appealId)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT c.status,
                   a.status,
                   COALESCE(a.decision,''),
                   COALESCE(n.delivery_status,''),
                   COALESCE(n.received_at::text,''),
                   COALESCE(c.appeal_deadline::text,''),
                   (SELECT COUNT(*) FROM witness.appeal_result_notices WHERE case_id=$1),
                   (SELECT COUNT(*) FROM witness.workflow_events
                    WHERE case_id=$1 AND action IN (
                        'notify-appeal-result','record-appeal-result-receipt',
                        'complete-appeal-result-notification')),
                   (SELECT COUNT(*) FROM witness.audit_events
                    WHERE case_id=$1 AND action IN (
                        'appeal.result.notice.sent','appeal.result.notice.received',
                        'appeal.result.notice.completed'))
            FROM witness.cases c
            JOIN witness.appeals a ON a.id=$2 AND a.case_id=c.id
            LEFT JOIN witness.appeal_result_notices n ON n.appeal_id=a.id
            WHERE c.id=$1
            """);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(appealId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new LifecycleState(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt64(6),
            reader.GetInt64(7), reader.GetInt64(8));
    }

    private static async Task<string> SnapshotAsync(NpgsqlDataSource dataSource, params Guid[] caseIds)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'cases', (SELECT jsonb_agg(jsonb_build_array(id,status,row_version,appeal_deadline) ORDER BY id)
                          FROM witness.cases WHERE id=ANY($1)),
                'appeals', (SELECT jsonb_agg(jsonb_build_array(id,case_id,status,row_version,decision,decided_at) ORDER BY id)
                            FROM witness.appeals WHERE case_id=ANY($1)),
                'notices', (SELECT jsonb_agg(jsonb_build_array(id,case_id,appeal_id,delivery_status,sent_at,received_at) ORDER BY id)
                            FROM witness.appeal_result_notices WHERE case_id=ANY($1)),
                'attachments', (SELECT COUNT(*) FROM witness.attachments WHERE case_id=ANY($1)),
                'workflow', (SELECT COUNT(*) FROM witness.workflow_events WHERE case_id=ANY($1)),
                'audit', (SELECT COUNT(*) FROM witness.audit_events WHERE case_id=ANY($1)),
                'notification', (SELECT COUNT(*) FROM witness.notifications WHERE case_id=ANY($1)),
                'deadlines', (SELECT COUNT(*) FROM witness.cases WHERE id=ANY($1) AND appeal_deadline IS NOT NULL),
                'idempotency', (SELECT COUNT(*) FROM witness.idempotency_records WHERE resource_id=ANY($1))
            )::text
            """);
        cmd.Parameters.AddWithValue(caseIds);
        return (string)(await cmd.ExecuteScalarAsync() ?? "");
    }

    private static async Task<string> InventoryAsync(NpgsqlDataSource dataSource)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'legacyNotices', (SELECT COUNT(*) FROM witness.notice_deliveries),
                'appealNotices', (SELECT COUNT(*) FROM witness.appeal_result_notices),
                'attachmentCount', (SELECT COUNT(*) FROM witness.attachments),
                'attachmentHashes', (SELECT COALESCE(string_agg(id::text || ':' || sha256, ',' ORDER BY id),'')
                                     FROM witness.attachments)
            )::text
            """);
        return (string)(await cmd.ExecuteScalarAsync() ?? "");
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlDataSource dataSource,
        string sql,
        params object[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters) cmd.Parameters.AddWithValue(parameter);
        var value = await cmd.ExecuteScalarAsync();
        Assert.NotNull(value);
        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task DeleteCasesAsync(NpgsqlDataSource dataSource, params Guid[] caseIds)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=ANY($1)");
        cmd.Parameters.AddWithValue(caseIds.Distinct().ToArray());
        await cmd.ExecuteNonQueryAsync();
    }

    private static string? ConnectionString()
        => Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");

    private sealed record AppealFixture(
        Guid CaseId,
        Guid AppealId,
        long CaseVersion,
        string ExternalReference);

    private sealed record LifecycleState(
        string CaseStatus,
        string AppealStatus,
        string Decision,
        string NoticeStatus,
        string ReceivedAt,
        string AppealDeadline,
        long NoticeCount,
        long LifecycleWorkflowCount,
        long LifecycleAuditCount);
}
