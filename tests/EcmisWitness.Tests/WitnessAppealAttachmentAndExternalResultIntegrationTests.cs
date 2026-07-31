using System.Security.Cryptography;
using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Infrastructure;
using EcmisWitness.Api.Security;
using Npgsql;

namespace EcmisWitness.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WitnessAppealPostgresCollection
{
    public const string Name = "Witness appeal attachment and external result PostgreSQL";
}

[Collection(WitnessAppealPostgresCollection.Name)]
public sealed class WitnessAppealAttachmentAndExternalResultIntegrationTests
{
    private static readonly Guid OrganizationA = Guid.Parse("91000000-0000-4000-8000-000000000001");
    private static readonly Guid OrganizationB = Guid.Parse("91000000-0000-4000-8000-000000000002");
    private static readonly byte[] PdfBytes = "%PDF-1.4\nE2E-TEST appeal evidence - no real PII\n%%EOF"u8.ToArray();

    [Fact]
    public async Task Migration_is_rerun_safe_and_preserves_existing_attachment_hashes()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var before = await AttachmentInventoryAsync(dataSource);

        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();

        Assert.Equal(before, await AttachmentInventoryAsync(dataSource));
        Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
            SELECT COUNT(*) FROM witness.schema_migrations
            WHERE version='013_appeal_attachment_and_revision_state'
            """));
        Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
            SELECT COUNT(*) FROM witness.schema_migrations
            WHERE version='014_appeal_evidence_requires_appeal'
            """));
        Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
            SELECT COUNT(*) FROM pg_constraint
            WHERE conname='ck_witness_attachments_evidence_requires_appeal'
              AND conrelid='witness.attachments'::regclass
            """));
    }

    [Fact]
    public async Task Appeal_evidence_upload_list_download_delete_and_retry_are_consistent()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var user = AppealUser("lifecycle", OrganizationA);
        var fixture = await SeedAppealCaseAsync(dataSource, user, WitnessStatuses.AppealReceived, "received");
        var key = $"E2E-WIT-033-UPLOAD-{Guid.NewGuid():N}";

        try
        {
            var repository = Repository(dataSource);
            var first = await repository.AddAttachmentAsync(
                fixture.CaseId, null, null, fixture.AppealId, "appeal_new_evidence", key,
                "E2E-TEST-appeal-evidence.pdf", "application/pdf", PdfBytes,
                user, "127.0.0.1", default);
            var replay = await repository.AddAttachmentAsync(
                fixture.CaseId, null, null, fixture.AppealId, "appeal_new_evidence", key,
                "E2E-TEST-appeal-evidence.pdf", "application/pdf", PdfBytes,
                user, "127.0.0.1", default);

            Assert.Equal(first.Id, replay.Id);
            Assert.Equal(fixture.AppealId, first.AppealId);
            Assert.Equal("appeal_new_evidence", first.EvidenceType);
            var listed = await repository.ListAppealAttachmentsAsync(
                fixture.CaseId, fixture.AppealId, user, default);
            var listedFile = Assert.Single(listed);
            Assert.Equal(first.Id, listedFile.Id);
            var downloaded = await repository.GetAttachmentContentAsync(
                fixture.CaseId, first.Id, user, "127.0.0.1", default);
            Assert.NotNull(downloaded);
            Assert.Equal(first.Sha256, Sha256(downloaded!.Content));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.attachments
                WHERE case_id=$1 AND appeal_id=$2 AND evidence_type='appeal_new_evidence'
                """, fixture.CaseId, fixture.AppealId));
            Assert.Equal(2L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.audit_events
                WHERE case_id=$1 AND entity_id=$2
                  AND action IN ('attachment.uploaded','attachment.downloaded')
                  AND details->>'appealId'=$3
                  AND details->>'sha256'=$4
                """, fixture.CaseId, first.Id.ToString(), fixture.AppealId.ToString(), first.Sha256));

            await repository.DeleteAttachmentAsync(
                fixture.CaseId, first.Id, "E2E-TEST delete before submit", user, "127.0.0.1", default);
            Assert.Empty(await repository.ListAppealAttachmentsAsync(
                fixture.CaseId, fixture.AppealId, user, default));
            Assert.Null(await repository.GetAttachmentContentAsync(
                fixture.CaseId, first.Id, user, "127.0.0.1", default));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Fact]
    public async Task Legacy_case_attachment_remains_available_and_is_not_listed_as_appeal_evidence()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var user = AppealUser("legacy", OrganizationA);
        var fixture = await SeedAppealCaseAsync(dataSource, user, WitnessStatuses.AppealReceived, "received");

        try
        {
            var repository = Repository(dataSource);
            var legacy = await repository.AddAttachmentAsync(
                fixture.CaseId, null, null,
                "E2E-TEST-case-level.pdf", "application/pdf", PdfBytes,
                user, "127.0.0.1", default);

            Assert.Null(legacy.AppealId);
            Assert.Null(legacy.EvidenceType);
            Assert.Empty(await repository.ListAppealAttachmentsAsync(
                fixture.CaseId, fixture.AppealId, user, default));
            Assert.NotNull(await repository.GetAttachmentContentAsync(
                fixture.CaseId, legacy.Id, user, "127.0.0.1", default));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Fact]
    public async Task Cross_case_closed_cross_org_and_unassigned_uploads_are_denied_without_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var assigned = AppealUser("assigned", OrganizationA);
        var caseA = await SeedAppealCaseAsync(dataSource, assigned, WitnessStatuses.AppealReceived, "received");
        var caseB = await SeedAppealCaseAsync(dataSource, assigned, WitnessStatuses.AppealReceived, "received");
        var closed = await SeedAppealCaseAsync(dataSource, assigned, WitnessStatuses.AppealDecided, "decided");
        var unassigned = await SeedAppealCaseAsync(
            dataSource, assigned, WitnessStatuses.AppealReceived, "received", assignAppealOfficer: false);

        try
        {
            var repository = Repository(dataSource);
            var before = await SnapshotAsync(dataSource, caseA.CaseId, caseB.CaseId, closed.CaseId, unassigned.CaseId);
            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.AddAttachmentAsync(
                caseA.CaseId, null, null, Guid.NewGuid(), "appeal_new_evidence", null,
                "E2E-TEST-missing-appeal.pdf", "application/pdf", PdfBytes,
                assigned, "127.0.0.1", default));
            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.AddAttachmentAsync(
                caseA.CaseId, null, null, caseB.AppealId, "appeal_new_evidence", null,
                "E2E-TEST-cross-case.pdf", "application/pdf", PdfBytes,
                assigned, "127.0.0.1", default));
            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.AddAttachmentAsync(
                closed.CaseId, null, null, closed.AppealId, "appeal_new_evidence", null,
                "E2E-TEST-closed.pdf", "application/pdf", PdfBytes,
                assigned, "127.0.0.1", default));
            await Assert.ThrowsAsync<WitnessAuthorizationException>(() => repository.AddAttachmentAsync(
                unassigned.CaseId, null, null, unassigned.AppealId, "appeal_new_evidence", null,
                "E2E-TEST-unassigned.pdf", "application/pdf", PdfBytes,
                assigned, "127.0.0.1", default));
            await Assert.ThrowsAsync<WitnessAuthorizationException>(() => repository.AddAttachmentAsync(
                caseA.CaseId, null, null, caseA.AppealId, "appeal_new_evidence", null,
                "E2E-TEST-cross-org.pdf", "application/pdf", PdfBytes,
                AppealUser("cross-org", OrganizationB), "127.0.0.1", default));
            Assert.Equal(before, await SnapshotAsync(
                dataSource, caseA.CaseId, caseB.CaseId, closed.CaseId, unassigned.CaseId));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, caseA.CaseId, caseB.CaseId, closed.CaseId, unassigned.CaseId);
        }
    }

    [Fact]
    public async Task Return_for_revision_keeps_same_appeal_open_then_resubmit_and_final_result_decides_it()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var appealUser = AppealUser("revision", OrganizationA);
        var externalUser = ExternalUser("revision");
        var fixture = await SeedAppealCaseAsync(
            dataSource, appealUser, WitnessStatuses.AppealExternalPending, "submitted", includeNotice: true);

        try
        {
            var repository = Repository(dataSource);
            var returnAttachment = await repository.AddAttachmentAsync(
                fixture.CaseId, null, null, fixture.AppealId, "external_result",
                $"E2E-WIT-034-RETURN-FILE-{Guid.NewGuid():N}",
                "E2E-TEST-return-for-revision.pdf", "application/pdf", PdfBytes,
                externalUser, "127.0.0.1", default);
            var returnRequest = ExternalRequest(
                fixture.CaseVersion + 1, "return-for-revision", returnAttachment.Id,
                $"E2E-WIT-034-RETURN-{Guid.NewGuid():N}");
            var returned = await repository.ReceiveExternalResultAsync(
                fixture.CaseId, returnRequest, externalUser, "127.0.0.1", default);
            var replay = await repository.ReceiveExternalResultAsync(
                fixture.CaseId, returnRequest, externalUser, "127.0.0.1", default);

            Assert.Equal(WitnessStatuses.AppealReceived, returned.ToStatus);
            Assert.Equal(returned.CaseId, replay.CaseId);
            Assert.Equal(returned.RequestNo, replay.RequestNo);
            Assert.Equal(returned.FromStatus, replay.FromStatus);
            Assert.Equal(returned.ToStatus, replay.ToStatus);
            Assert.Equal(returned.Version, replay.Version);
            Assert.Equal(
                returned.AvailableActions.Select(item => item.Code),
                replay.AvailableActions.Select(item => item.Code));
            var afterReturn = await AppealStateAsync(dataSource, fixture.AppealId);
            Assert.Equal("received", afterReturn.Status);
            Assert.Null(afterReturn.Decision);
            Assert.Null(afterReturn.DecidedAt);

            var submitted = await repository.ExecuteCommandAsync(
                fixture.CaseId, "submit-appeal",
                new ExecuteWitnessCommandRequest(
                    "E2E-TEST resubmit same appeal", returned.Version, null,
                    $"E2E-WIT-034-RESUBMIT-{Guid.NewGuid():N}"),
                appealUser, "127.0.0.1", default);
            Assert.Equal(WitnessStatuses.AppealExternalPending, submitted.ToStatus);
            Assert.Equal(1L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.appeals WHERE case_id=$1", fixture.CaseId));

            var finalAttachment = await repository.AddAttachmentAsync(
                fixture.CaseId, null, null, fixture.AppealId, "external_result",
                $"E2E-WIT-034-FINAL-FILE-{Guid.NewGuid():N}",
                "E2E-TEST-final-result.pdf", "application/pdf", PdfBytes,
                externalUser, "127.0.0.1", default);
            var finalResult = await repository.ReceiveExternalResultAsync(
                fixture.CaseId,
                ExternalRequest(
                    submitted.Version + 1, "appeal-upheld", finalAttachment.Id,
                    $"E2E-WIT-034-FINAL-{Guid.NewGuid():N}"),
                externalUser, "127.0.0.1", default);

            Assert.Equal(WitnessStatuses.AppealDecided, finalResult.ToStatus);
            var afterFinal = await AppealStateAsync(dataSource, fixture.AppealId);
            Assert.Equal("decided", afterFinal.Status);
            Assert.Equal("appeal-upheld", afterFinal.Decision);
            Assert.NotNull(afterFinal.DecidedAt);
            Assert.Equal(2L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.external_results WHERE case_id=$1", fixture.CaseId));
            Assert.Equal(2L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.workflow_events
                WHERE case_id=$1 AND action='external-result'
                """, fixture.CaseId));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Theory]
    [InlineData("appeal-upheld", "appeal_decided")]
    [InlineData("appeal-reversed", "approved_pending_notice")]
    public async Task Final_appeal_results_decide_appeal(string resultType, string expectedCaseStatus)
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var appealUser = AppealUser($"final-{resultType}", OrganizationA);
        var externalUser = ExternalUser($"final-{resultType}");
        var fixture = await SeedAppealCaseAsync(
            dataSource, appealUser, WitnessStatuses.AppealExternalPending, "submitted", includeNotice: true);

        try
        {
            var repository = Repository(dataSource);
            var attachment = await repository.AddAttachmentAsync(
                fixture.CaseId, null, null, fixture.AppealId, "external_result", Guid.NewGuid().ToString("N"),
                "E2E-TEST-final.pdf", "application/pdf", PdfBytes,
                externalUser, "127.0.0.1", default);
            var result = await repository.ReceiveExternalResultAsync(
                fixture.CaseId,
                ExternalRequest(fixture.CaseVersion + 1, resultType, attachment.Id, Guid.NewGuid().ToString("N")),
                externalUser, "127.0.0.1", default);

            Assert.Equal(expectedCaseStatus, result.ToStatus);
            var appeal = await AppealStateAsync(dataSource, fixture.AppealId);
            Assert.Equal("decided", appeal.Status);
            Assert.Equal(resultType, appeal.Decision);
            Assert.NotNull(appeal.DecidedAt);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, fixture.CaseId);
        }
    }

    [Fact]
    public async Task Concurrent_external_retry_creates_one_result_workflow_and_audit()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var appealUser = AppealUser("concurrent", OrganizationA);
        var externalUser = ExternalUser("concurrent");
        var fixture = await SeedAppealCaseAsync(
            dataSource, appealUser, WitnessStatuses.AppealExternalPending, "submitted");

        try
        {
            var repository = Repository(dataSource);
            var attachment = await repository.AddAttachmentAsync(
                fixture.CaseId, null, null, fixture.AppealId, "external_result", Guid.NewGuid().ToString("N"),
                "E2E-TEST-concurrent.pdf", "application/pdf", PdfBytes,
                externalUser, "127.0.0.1", default);
            var key = $"E2E-WIT-034-CONCURRENT-{Guid.NewGuid():N}";
            var request = ExternalRequest(fixture.CaseVersion + 1, "return-for-revision", attachment.Id, key);
            using var gate = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            {
                gate.Wait();
                return await Repository(dataSource).ReceiveExternalResultAsync(
                    fixture.CaseId, request, externalUser, "127.0.0.1", default);
            })).ToArray();
            gate.Set();
            var results = await Task.WhenAll(tasks);

            Assert.All(results, result => Assert.Equal(WitnessStatuses.AppealReceived, result.ToStatus));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.external_results WHERE case_id=$1", fixture.CaseId));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.workflow_events
                WHERE case_id=$1 AND action='external-result'
                """, fixture.CaseId));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.audit_events
                WHERE case_id=$1 AND action='external.result.received'
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
    public async Task Unsupported_cross_case_and_transaction_failure_have_no_business_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var appealUser = AppealUser("negative", OrganizationA);
        var externalUser = ExternalUser("negative");
        var caseA = await SeedAppealCaseAsync(
            dataSource, appealUser, WitnessStatuses.AppealExternalPending, "submitted");
        var caseB = await SeedAppealCaseAsync(
            dataSource, appealUser, WitnessStatuses.AppealExternalPending, "submitted");

        try
        {
            var repository = Repository(dataSource);
            var attachmentA = await repository.AddAttachmentAsync(
                caseA.CaseId, null, null, caseA.AppealId, "external_result", Guid.NewGuid().ToString("N"),
                "E2E-TEST-negative-a.pdf", "application/pdf", PdfBytes,
                externalUser, "127.0.0.1", default);
            var attachmentB = await repository.AddAttachmentAsync(
                caseB.CaseId, null, null, caseB.AppealId, "external_result", Guid.NewGuid().ToString("N"),
                "E2E-TEST-negative-b.pdf", "application/pdf", PdfBytes,
                externalUser, "127.0.0.1", default);
            var before = await SnapshotAsync(dataSource, caseA.CaseId, caseB.CaseId);

            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.ReceiveExternalResultAsync(
                caseA.CaseId,
                ExternalRequest(caseA.CaseVersion + 1, "unsupported-result", attachmentA.Id, Guid.NewGuid().ToString("N")),
                externalUser, "127.0.0.1", default));
            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.ReceiveExternalResultAsync(
                caseA.CaseId,
                ExternalRequest(caseA.CaseVersion + 1, "return-for-revision", attachmentB.Id, Guid.NewGuid().ToString("N")),
                externalUser, "127.0.0.1", default));
            Assert.Equal(before, await SnapshotAsync(dataSource, caseA.CaseId, caseB.CaseId));

            var rollbackKey = $"E2E-WIT-034-ROLLBACK-{Guid.NewGuid():N}";
            var badActor = externalUser with { DisplayName = new string('ก', 251) };
            await Assert.ThrowsAsync<PostgresException>(() => repository.ReceiveExternalResultAsync(
                caseA.CaseId,
                ExternalRequest(caseA.CaseVersion + 1, "return-for-revision", attachmentA.Id, rollbackKey),
                badActor, "127.0.0.1", default));
            Assert.Equal(before, await SnapshotAsync(dataSource, caseA.CaseId, caseB.CaseId));
            Assert.Equal(0L, await ScalarAsync<long>(dataSource, """
                SELECT COUNT(*) FROM witness.idempotency_records WHERE idempotency_key=$1
                """, rollbackKey));
        }
        finally
        {
            await DeleteCasesAsync(dataSource, caseA.CaseId, caseB.CaseId);
        }
    }

    private static WitnessRepository Repository(NpgsqlDataSource dataSource)
        => new(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());

    private static WitnessUserContext AppealUser(string suffix, Guid organizationId)
        => new(
            Guid.NewGuid(), $"e2e-appeal-{suffix}", $"E2E-TEST Appeal {suffix}", "เจ้าหน้าที่รับอุทธรณ์",
            new HashSet<string> { "appeal_officer" },
            new HashSet<string>
            {
                WitnessPermissions.AppealManage,
                WitnessPermissions.DocumentDownload,
                WitnessPermissions.ViewMasked
            },
            organizationId, "E2E-TEST ORG", "workgroup");

    private static WitnessUserContext ExternalUser(string suffix)
        => new(
            Guid.NewGuid(), $"e2e-external-{suffix}", $"E2E-TEST External {suffix}", "ผู้รับผลภายนอก",
            new HashSet<string> { "external_receiver" },
            new HashSet<string>
            {
                WitnessPermissions.ExternalReceive,
                WitnessPermissions.DocumentDownload,
                WitnessPermissions.ViewMasked
            });

    private static ReceiveExternalResultRequest ExternalRequest(
        long version,
        string resultType,
        Guid attachmentId,
        string key)
        => new(
            resultType,
            $"E2E-TEST-REF-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            $"E2E-TEST {resultType}",
            version,
            new Dictionary<string, string> { ["external_attachment_id"] = attachmentId.ToString() },
            key);

    private static async Task<AppealFixture> SeedAppealCaseAsync(
        NpgsqlDataSource dataSource,
        WitnessUserContext appealUser,
        string caseStatus,
        string appealStatus,
        bool assignAppealOfficer = true,
        bool includeNotice = false)
    {
        var caseId = Guid.NewGuid();
        var appealId = Guid.NewGuid();
        const long version = 10;
        var ownerRole = caseStatus == WitnessStatuses.AppealExternalPending ? "external_module" : "appeal_officer";
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO witness.cases(
                id, request_no, intake_form_number, status, urgent_status,
                current_owner_role, current_owner_user_id, current_owner_name,
                risk_level, is_urgent, summary_data, row_version,
                created_by, created_by_name, owning_org_id, current_owner_org_id,
                owning_org_name, current_owner_org_name, created_at, updated_at)
            VALUES($1,$2,1,$3,'none',$4,$5,$6,'ปานกลาง',false,
                   '{"witness_name":"E2E-TEST"}'::jsonb,$7,$8,$9,$10,$10,
                   'E2E-TEST ORG','E2E-TEST ORG',NOW(),NOW())
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue($"E2E-TEST-{Guid.NewGuid():N}"[..40]);
            cmd.Parameters.AddWithValue(caseStatus);
            cmd.Parameters.AddWithValue(ownerRole);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = caseStatus == WitnessStatuses.AppealExternalPending || !assignAppealOfficer
                    ? DBNull.Value
                    : appealUser.UserId,
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid
            });
            cmd.Parameters.AddWithValue(
                caseStatus == WitnessStatuses.AppealExternalPending || !assignAppealOfficer
                    ? ""
                    : appealUser.DisplayName);
            cmd.Parameters.AddWithValue(version);
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue("E2E-TEST fixture creator");
            cmd.Parameters.AddWithValue(OrganizationA);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO witness.appeals(
                id, case_id, filed_at, filed_channel, statement, late_reason,
                is_late, status, row_version, decision, decided_at,
                created_by, created_at, updated_at)
            VALUES($1,$2,NOW()-INTERVAL '2 day','หนังสือ','E2E-TEST appeal',NULL,
                   false,$3,1,$4,$5,$6,NOW()-INTERVAL '2 day',NOW()-INTERVAL '2 day')
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(appealId);
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(appealStatus);
            cmd.Parameters.AddWithValue((object?)(appealStatus == "decided" ? "appeal-upheld" : null) ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = appealStatus == "decided" ? DateTimeOffset.UtcNow.AddDays(-1) : DBNull.Value,
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz
            });
            cmd.Parameters.AddWithValue(appealUser.UserId);
            await cmd.ExecuteNonQueryAsync();
        }
        if (assignAppealOfficer)
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
            cmd.Parameters.AddWithValue(appealUser.UserId);
            cmd.Parameters.AddWithValue(appealUser.Username);
            cmd.Parameters.AddWithValue(OrganizationA);
            cmd.Parameters.AddWithValue(appealUser.DisplayName);
            await cmd.ExecuteNonQueryAsync();
        }
        if (includeNotice)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO witness.notice_deliveries(
                    id, case_id, form_number, sent_at, delivery_channel, recipient,
                    created_by, created_by_name, created_at)
                VALUES($1,$2,10,NOW()-INTERVAL '10 day','ไปรษณีย์ตอบรับ',
                       'E2E-TEST recipient',$3,$4,NOW()-INTERVAL '10 day')
                """, connection, tx);
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(appealUser.UserId);
            cmd.Parameters.AddWithValue(appealUser.DisplayName);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return new AppealFixture(caseId, appealId, version);
    }

    private static async Task<AppealState> AppealStateAsync(NpgsqlDataSource dataSource, Guid appealId)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT status, decision, decided_at, row_version
            FROM witness.appeals WHERE id=$1
            """);
        cmd.Parameters.AddWithValue(appealId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new AppealState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetInt64(3));
    }

    private static async Task<string> SnapshotAsync(NpgsqlDataSource dataSource, params Guid[] caseIds)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'cases', (SELECT jsonb_agg(jsonb_build_array(id,status,row_version) ORDER BY id)
                          FROM witness.cases WHERE id=ANY($1)),
                'appeals', (SELECT jsonb_agg(jsonb_build_array(id,case_id,status,row_version,decision,decided_at) ORDER BY id)
                            FROM witness.appeals WHERE case_id=ANY($1)),
                'attachments', (SELECT COUNT(*) FROM witness.attachments WHERE case_id=ANY($1)),
                'workflow', (SELECT COUNT(*) FROM witness.workflow_events WHERE case_id=ANY($1)),
                'external', (SELECT COUNT(*) FROM witness.external_results WHERE case_id=ANY($1)),
                'audit', (SELECT COUNT(*) FROM witness.audit_events WHERE case_id=ANY($1)),
                'notification', (SELECT COUNT(*) FROM witness.notifications WHERE case_id=ANY($1)),
                'idempotency', (SELECT COUNT(*) FROM witness.idempotency_records WHERE resource_id=ANY($1))
            )::text
            """);
        cmd.Parameters.AddWithValue(caseIds);
        return (string)(await cmd.ExecuteScalarAsync() ?? "");
    }

    private static async Task<string> AttachmentInventoryAsync(NpgsqlDataSource dataSource)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'count', COUNT(*),
                'hashes', COALESCE(string_agg(id::text || ':' || sha256, ',' ORDER BY id),''),
                'bytes', COALESCE(SUM(size_bytes),0)
            )::text
            FROM witness.attachments
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

    private static string Sha256(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string? ConnectionString()
        => Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");

    private sealed record AppealFixture(Guid CaseId, Guid AppealId, long CaseVersion);
    private sealed record AppealState(string Status, string? Decision, DateTimeOffset? DecidedAt, long Version);
}
