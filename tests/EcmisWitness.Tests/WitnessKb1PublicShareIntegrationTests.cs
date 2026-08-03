using System.Text.Json;
using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Forms;
using EcmisWitness.Api.Infrastructure;
using EcmisWitness.Api.Security;
using Npgsql;

namespace EcmisWitness.Tests;

[Collection(WitnessIdempotencyPostgresCollection.Name)]
public sealed class WitnessKb1PublicShareIntegrationTests
{
    private const string SignatureDataUrl =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task Public_Kb1_share_saves_replays_and_submits_into_the_original_case()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = new WitnessRepository(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());
        var officer = Officer();
        Guid caseId = Guid.Empty;
        Guid shareId = Guid.Empty;
        try
        {
            var created = await repository.CreateAsync(new CreateWitnessCaseRequest(
                1,
                new Dictionary<string, string> { ["request_date"] = "2026-07-29" },
                Submit: false,
                IdempotencyKey: $"E2E-KB1-SHARE-CREATE-{Guid.NewGuid():N}"),
                officer, "127.0.0.1", default);
            caseId = created.Case.Id;

            var share = await repository.CreateKb1ShareLinkAsync(caseId, officer, "127.0.0.1", default);
            shareId = share.Id;
            Assert.Equal(64, share.AccessToken!.Length);
            var storedToken = await ScalarAsync<string>(dataSource,
                "SELECT token_sha256 FROM witness.kb1_share_links WHERE id=$1", share.Id);
            Assert.NotEqual(share.AccessToken, storedToken);

            var opened = await repository.GetPublicKb1DraftAsync(share.AccessToken!, "127.0.0.1", default);
            Assert.Equal(created.Case.RequestNo, opened.RequestNo);
            Assert.Equal("active", opened.Status);

            var validValues = ValidKb1Values();
            var saveKey = $"E2E-KB1-SHARE-SAVE-{Guid.NewGuid():N}";
            var request = new SaveWitnessPublicKb1Request(
                validValues, false, false, opened.FormVersion, opened.CaseVersion, saveKey);
            var concurrent = await Task.WhenAll(
                repository.SavePublicKb1DraftAsync(share.AccessToken!, request, "127.0.0.1", default),
                repository.SavePublicKb1DraftAsync(share.AccessToken!, request, "127.0.0.1", default));
            Assert.Equal(concurrent[0].FormVersion, concurrent[1].FormVersion);
            Assert.Equal(2, concurrent[0].FormVersion);
            Assert.Equal(2, await ScalarAsync<int>(dataSource,
                "SELECT version FROM witness.forms WHERE case_id=$1 AND form_number=1", caseId));
            Assert.Equal(2L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.form_versions WHERE case_id=$1 AND form_number=1", caseId));

            var submitKey = $"E2E-KB1-SHARE-SUBMIT-{Guid.NewGuid():N}";
            var submitRequest = new SaveWitnessPublicKb1Request(validValues, true, true,
                    concurrent[0].FormVersion, concurrent[0].CaseVersion,
                    submitKey,
                    ConsentToElectronicSignature: true,
                    SignatureDataUrl: SignatureDataUrl);
            var completed = await repository.SavePublicKb1DraftAsync(share.AccessToken!, submitRequest,
                "127.0.0.1", default);
            var replayed = await repository.SavePublicKb1DraftAsync(share.AccessToken!, submitRequest,
                "127.0.0.1", default);
            Assert.Equal("submitted", completed.Status);
            Assert.Equal(completed.RequestNo, replayed.RequestNo);
            Assert.Equal(completed.Status, replayed.Status);
            Assert.Equal(completed.FormVersion, replayed.FormVersion);
            Assert.Equal(completed.CaseVersion, replayed.CaseVersion);
            Assert.Equal(completed.Attachments.Select(item => item.Id), replayed.Attachments.Select(item => item.Id));
            Assert.Equal("staff_review", await ScalarAsync<string>(dataSource,
                "SELECT status FROM witness.cases WHERE id=$1", caseId));
            Assert.Equal("submitted", await ScalarAsync<string>(dataSource,
                "SELECT status FROM witness.kb1_share_links WHERE id=$1", share.Id));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.workflow_events WHERE case_id=$1 AND action='public-kb1-submitted'", caseId));
            Assert.True(await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.audit_events WHERE case_id=$1 AND action='kb1.public.submitted'", caseId) == 1);
            Assert.Equal(1L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.form_signatures WHERE case_id=$1 AND signer_purpose='ผู้ยื่นคำร้อง' AND evidence_attachment_id IS NOT NULL", caseId));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.attachments WHERE case_id=$1 AND content_type='image/png' AND deleted_at IS NULL", caseId));
            var signatureId = Guid.Parse(await ScalarAsync<string>(dataSource,
                "SELECT id::text FROM witness.form_signatures WHERE case_id=$1 AND signer_purpose='ผู้ยื่นคำร้อง'", caseId));
            var signatureEvidence = await repository.GetSignatureEvidenceContentAsync(
                caseId, 1, signatureId, officer, "127.0.0.1", default);
            Assert.NotNull(signatureEvidence);
            Assert.Equal("image/png", signatureEvidence.ContentType);
            Assert.True(signatureEvidence.Content.AsSpan(0, 8)
                .SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.audit_events WHERE case_id=$1 AND action='signature.evidence.viewed'", caseId));

            var submittedPage = await repository.GetPublicKb1DraftAsync(share.AccessToken!, "127.0.0.1", default);
            Assert.Equal("submitted", submittedPage.Status);
            Assert.Empty(submittedPage.Values);
            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.SavePublicKb1DraftAsync(
                share.AccessToken!,
                new SaveWitnessPublicKb1Request(validValues, false, false,
                    completed.FormVersion, completed.CaseVersion, Guid.NewGuid().ToString("N")),
                "127.0.0.1", default));
        }
        finally
        {
            if (shareId != Guid.Empty)
            {
                await using var cleanIdempotency = dataSource.CreateCommand(
                    "DELETE FROM witness.idempotency_records WHERE actor_user_id=$1");
                cleanIdempotency.Parameters.AddWithValue(shareId);
                await cleanIdempotency.ExecuteNonQueryAsync();
            }
            if (caseId != Guid.Empty)
            {
                await using var cleanup = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=$1");
                cleanup.Parameters.AddWithValue(caseId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Public_Kb1_submit_without_petitioner_signature_is_rejected_without_business_mutation()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = new WitnessRepository(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());
        var officer = Officer();
        Guid caseId = Guid.Empty;
        try
        {
            var created = await repository.CreateAsync(new CreateWitnessCaseRequest(
                    1, new Dictionary<string, string>(), Submit: false,
                    IdempotencyKey: $"E2E-KB1-NO-SIGN-CREATE-{Guid.NewGuid():N}"),
                officer, "127.0.0.1", default);
            caseId = created.Case.Id;
            var share = await repository.CreateKb1ShareLinkAsync(caseId, officer, "127.0.0.1", default);
            var opened = await repository.GetPublicKb1DraftAsync(share.AccessToken!, "127.0.0.1", default);

            var error = await Assert.ThrowsAsync<WitnessWorkflowException>(() =>
                repository.SavePublicKb1DraftAsync(share.AccessToken!,
                    new SaveWitnessPublicKb1Request(
                        ValidKb1Values(), true, true, opened.FormVersion, opened.CaseVersion,
                        $"E2E-KB1-NO-SIGN-SUBMIT-{Guid.NewGuid():N}"),
                    "127.0.0.1", default));

            Assert.Contains("ลายมือชื่อ", error.Message, StringComparison.Ordinal);
            Assert.Equal(opened.FormVersion, await ScalarAsync<int>(dataSource,
                "SELECT version FROM witness.forms WHERE case_id=$1 AND form_number=1", caseId));
            Assert.Equal(opened.CaseVersion, await ScalarAsync<long>(dataSource,
                "SELECT row_version FROM witness.cases WHERE id=$1", caseId));
            Assert.Equal(0L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.form_signatures WHERE case_id=$1", caseId));
            Assert.Equal(0L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.workflow_events WHERE case_id=$1 AND action='public-kb1-submitted'", caseId));
        }
        finally
        {
            if (caseId != Guid.Empty)
            {
                await using var cleanup = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=$1");
                cleanup.Parameters.AddWithValue(caseId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task Public_Kb1_attachments_are_scoped_to_the_share_and_revoked_link_cannot_mutate()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = new WitnessRepository(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());
        var officer = Officer();
        Guid caseId = Guid.Empty;
        Guid shareId = Guid.Empty;
        try
        {
            var created = await repository.CreateAsync(new CreateWitnessCaseRequest(1,
                    new Dictionary<string, string>(), Submit: false,
                    IdempotencyKey: $"E2E-KB1-FILE-{Guid.NewGuid():N}"),
                officer, "127.0.0.1", default);
            caseId = created.Case.Id;
            var share = await repository.CreateKb1ShareLinkAsync(caseId, officer, "127.0.0.1", default);
            shareId = share.Id;
            var content = "%PDF-1.7\nเอกสารทดสอบระบบ E2E ไม่มีข้อมูลบุคคลจริง"u8.ToArray();
            var attachment = await repository.AddPublicKb1AttachmentAsync(
                share.AccessToken!, Guid.NewGuid().ToString("N"), "E2E-TEST.pdf", "application/pdf",
                content, "127.0.0.1", default);
            var reopened = await repository.GetPublicKb1DraftAsync(share.AccessToken!, "127.0.0.1", default);
            Assert.Equal(attachment.Id, Assert.Single(reopened.Attachments).Id);
            var downloaded = await repository.GetPublicKb1AttachmentContentAsync(
                share.AccessToken!, attachment.Id, "127.0.0.1", default);
            Assert.Equal(content, downloaded!.Content);

            await repository.RevokeKb1ShareLinkAsync(caseId, officer, "127.0.0.1", default);
            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.AddPublicKb1AttachmentAsync(
                share.AccessToken!, Guid.NewGuid().ToString("N"), "E2E-TEST-2.pdf", "application/pdf",
                content, "127.0.0.1", default));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource,
                "SELECT COUNT(*) FROM witness.attachments WHERE case_id=$1 AND deleted_at IS NULL", caseId));
        }
        finally
        {
            if (shareId != Guid.Empty)
            {
                await using var cleanIdempotency = dataSource.CreateCommand(
                    "DELETE FROM witness.idempotency_records WHERE actor_user_id=$1");
                cleanIdempotency.Parameters.AddWithValue(shareId);
                await cleanIdempotency.ExecuteNonQueryAsync();
            }
            if (caseId != Guid.Empty)
            {
                await using var cleanup = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=$1");
                cleanup.Parameters.AddWithValue(caseId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    private static WitnessUserContext Officer()
        => new(Guid.NewGuid(), $"kb1-share-{Guid.NewGuid():N}", "เจ้าหน้าที่ E2E-TEST", "เจ้าหน้าที่รับคำร้อง",
            new HashSet<string> { "witness_officer" },
            new HashSet<string> { WitnessPermissions.Create, WitnessPermissions.ViewMasked, WitnessPermissions.ViewPii });

    private static Dictionary<string, string> ValidKb1Values()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in WitnessProtectionFormCatalog.Get(1).Fields.Where(field => field.Required))
        {
            result[field.Key] = field.Type switch
            {
                WitnessFormFieldType.Checkbox => "true",
                WitnessFormFieldType.Date => "2026-07-29",
                WitnessFormFieldType.Number => "1",
                WitnessFormFieldType.Select => field.Options![0],
                WitnessFormFieldType.MultiSelect => JsonSerializer.Serialize(new[] { field.Options![0] }),
                WitnessFormFieldType.Address => JsonSerializer.Serialize((field.Columns ?? [])
                    .ToDictionary(column => column.Key, column => column.Required ? "E2E-TEST" : "")),
                WitnessFormFieldType.Repeating => JsonSerializer.Serialize(new[]
                {
                    (field.Columns ?? []).ToDictionary(column => column.Key, _ => "E2E-TEST")
                }),
                WitnessFormFieldType.ReadOnly => "",
                _ => "E2E-TEST"
            };
        }
        result["petitioner_citizen_id"] = "1101700203450";
        result["witness_citizen_id"] = "1101700203450";
        result["threat_status"] = "ไม่มี";
        return result;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql, Guid id)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(id);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }
}
