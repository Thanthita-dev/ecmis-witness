using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Infrastructure;
using EcmisWitness.Api.Security;
using Npgsql;

namespace EcmisWitness.Tests;

[Collection(WitnessIdempotencyPostgresCollection.Name)]
public sealed class WitnessRbacFieldAuthorizationIntegrationTests
{
    [Fact]
    public async Task Global_administrator_can_view_all_organizations_but_wildcard_cannot_mutate()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = Repository(dataSource);
        var organizationA = Guid.NewGuid();
        var organizationB = Guid.NewGuid();
        var creatorA = User("creator-a", organizationA, WitnessPermissions.Create);
        var creatorB = User("creator-b", organizationB, WitnessPermissions.Create);
        var administrator = User("global-admin", null, "witness.*", role: "super_admin");
        var operationalAdministrator = User(
            "operational-admin", null, WitnessPermissions.Create, role: "super_admin");
        var caseIds = new List<Guid>();

        try
        {
            var caseA = await CreateDraftAsync(repository, creatorA, "หน่วยงานเอ");
            var caseB = await CreateDraftAsync(repository, creatorB, "หน่วยงานบี");
            caseIds.Add(caseA.Case.Id);
            caseIds.Add(caseB.Case.Id);

            var visible = await repository.ListAsync(administrator, null, "E2E-TEST-RBAC", default);
            Assert.Contains(visible, item => item.Id == caseA.Case.Id);
            Assert.Contains(visible, item => item.Id == caseB.Case.Id);
            var detail = await repository.GetDetailAsync(caseA.Case.Id, administrator, default);
            Assert.NotNull(detail);
            Assert.Contains("E2E-TEST-RBAC", detail!.Case.WitnessDisplayName);

            var before = await SnapshotAsync(dataSource, caseA.Case.Id, 1);
            await Assert.ThrowsAsync<WitnessAuthorizationException>(() => repository.CreateAsync(
                DraftRequest("global-denied"), administrator, "127.0.0.1", default));
            await Assert.ThrowsAsync<WitnessAuthorizationException>(() => repository.SaveFormAsync(
                caseA.Case.Id,
                1,
                new SaveWitnessFormRequest(
                    new Dictionary<string, string> { ["witness_first_name"] = "เปลี่ยนโดยผู้ดูแล" },
                    false,
                    1,
                    caseA.Case.Version),
                administrator,
                "127.0.0.1",
                default));
            Assert.Equal(before, await SnapshotAsync(dataSource, caseA.Case.Id, 1));

            var explicitlyAuthorized = await CreateDraftAsync(
                repository, operationalAdministrator, "GLOBAL-EXPLICIT-CREATE");
            caseIds.Add(explicitlyAuthorized.Case.Id);
            Assert.StartsWith("WP-", explicitlyAuthorized.Case.RequestNo, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, caseIds);
        }
    }

    [Fact]
    public async Task Generic_edit_cannot_create_Kb10_but_notice_permission_can_and_deny_has_no_mutation()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = Repository(dataSource);
        var organization = Guid.NewGuid();
        var creator = User("notice-creator", organization, WitnessPermissions.Create);
        var officer = User("r02", organization, WitnessPermissions.Edit, WitnessPermissions.OfficerReview);
        var noticeOfficer = User("r06", organization, WitnessPermissions.NoticeManage);
        Guid caseId = Guid.Empty;

        try
        {
            var created = await CreateDraftAsync(repository, creator, "CASE-088");
            caseId = created.Case.Id;
            await RouteCaseAsync(dataSource, caseId, WitnessStatuses.RejectedPendingNotice,
                "notice_officer", officer, organization, "สำนักทดสอบ");
            var before = await SnapshotAsync(dataSource, caseId, 10);

            await Assert.ThrowsAsync<WitnessAuthorizationException>(() => repository.SaveFormAsync(
                caseId,
                10,
                new SaveWitnessFormRequest(
                    new Dictionary<string, string> { ["rejection_reason"] = "พยายามจัดทำ คบ.10 โดย R02" },
                    false,
                    0,
                    created.Case.Version),
                officer,
                "127.0.0.1",
                default));
            Assert.Equal(before, await SnapshotAsync(dataSource, caseId, 10));

            await RouteCaseAsync(dataSource, caseId, WitnessStatuses.RejectedPendingNotice,
                "notice_officer", noticeOfficer, organization, "สำนักทดสอบ");
            var saved = await repository.SaveFormAsync(
                caseId,
                10,
                new SaveWitnessFormRequest(
                    new Dictionary<string, string> { ["rejection_reason"] = "ผลไม่อนุมัติจาก External Module" },
                    false,
                    0,
                    created.Case.Version),
                noticeOfficer,
                "127.0.0.1",
                default);

            Assert.Equal(10, saved.FormNumber);
            Assert.Equal(1, saved.Version);
            Assert.Equal("ผลไม่อนุมัติจาก External Module", saved.Values["rejection_reason"]);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, [caseId]);
        }
    }

    [Fact]
    public async Task Kb4_field_owner_is_enforced_and_denied_requests_do_not_change_database()
    {
        var connectionString = ConnectionString();
        if (connectionString is null)
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = Repository(dataSource);
        var organization = Guid.NewGuid();
        var officer = User("case-057-officer", organization,
            WitnessPermissions.Create, WitnessPermissions.OfficerReview);
        var director = User("case-057-director", organization, WitnessPermissions.DirectorReview);
        Guid caseId = Guid.Empty;

        try
        {
            var created = await CreateDraftAsync(repository, officer, "CASE-057");
            caseId = created.Case.Id;
            await RouteCaseAsync(dataSource, caseId, WitnessStatuses.StaffReview,
                "officer", officer, organization, "สำนักทดสอบ", "awaiting_kb4");
            var first = await repository.SaveFormAsync(
                caseId,
                4,
                new SaveWitnessFormRequest(
                    new Dictionary<string, string>
                    {
                        ["case_background"] = "ข้อเท็จจริงที่เจ้าหน้าที่บันทึก",
                        ["officer_recommendation"] = "เห็นควรเสนอกรณีเร่งด่วน"
                    },
                    false,
                    0,
                    created.Case.Version),
                officer,
                "127.0.0.1",
                default);
            var before = await SnapshotAsync(dataSource, caseId, 4);

            foreach (var attemptedValue in new[] { "ความเห็น ผอ. ปลอม", "" })
            {
                await Assert.ThrowsAsync<WitnessAuthorizationException>(() => repository.SaveFormAsync(
                    caseId,
                    4,
                    new SaveWitnessFormRequest(
                        new Dictionary<string, string> { ["director_opinion"] = attemptedValue },
                        false,
                        first.Version,
                        first.CaseVersion),
                    officer,
                    "127.0.0.1",
                    default));
                Assert.Equal(before, await SnapshotAsync(dataSource, caseId, 4));
            }

            await RouteCaseAsync(dataSource, caseId, WitnessStatuses.StaffReview,
                "director", director, organization, "สำนักทดสอบ", "director_review");
            var directorSaved = await repository.SaveFormAsync(
                caseId,
                4,
                new SaveWitnessFormRequest(
                    new Dictionary<string, string> { ["director_opinion"] = "ผอ. เห็นชอบ" },
                    false,
                    first.Version,
                    first.CaseVersion),
                director,
                "127.0.0.1",
                default);

            Assert.Equal("ผอ. เห็นชอบ", directorSaved.Values["director_opinion"]);
            Assert.Equal("เห็นควรเสนอกรณีเร่งด่วน", directorSaved.Values["officer_recommendation"]);
            Assert.Equal("ข้อเท็จจริงที่เจ้าหน้าที่บันทึก", directorSaved.Values["case_background"]);
        }
        finally
        {
            await DeleteCasesAsync(dataSource, [caseId]);
        }
    }

    private static WitnessRepository Repository(NpgsqlDataSource dataSource)
        => new(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());

    private static string? ConnectionString()
        => Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");

    private static WitnessUserContext User(
        string suffix,
        Guid? organizationId,
        string permission,
        string? secondPermission = null,
        string role = "test_role")
    {
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { permission };
        if (!string.IsNullOrWhiteSpace(secondPermission))
            permissions.Add(secondPermission);
        return new WitnessUserContext(
            Guid.NewGuid(),
            $"e2e-rbac-{suffix}",
            $"E2E-TEST RBAC {suffix}",
            "ผู้ทดสอบระบบ",
            new HashSet<string> { role },
            permissions,
            organizationId,
            organizationId.HasValue ? "สำนักทดสอบ" : "",
            organizationId.HasValue ? "department" : "");
    }

    private static CreateWitnessCaseRequest DraftRequest(string suffix)
        => new(
            1,
            new Dictionary<string, string>
            {
                ["witness_first_name"] = "E2E-TEST-RBAC",
                ["witness_last_name"] = suffix,
                ["petitioner_first_name"] = "E2E-TEST-RBAC",
                ["petitioner_last_name"] = suffix,
                ["test_batch_id"] = "E2E-WIT-RBAC-FIELD-AUTH-20260721"
            },
            Submit: false,
            IdempotencyKey: $"E2E-RBAC-{suffix}-{Guid.NewGuid():N}");

    private static Task<WitnessCaseDetailDto> CreateDraftAsync(
        WitnessRepository repository,
        WitnessUserContext creator,
        string suffix)
        => repository.CreateAsync(DraftRequest(suffix), creator, "127.0.0.1", default);

    private static async Task RouteCaseAsync(
        NpgsqlDataSource dataSource,
        Guid caseId,
        string status,
        string ownerRole,
        WitnessUserContext owner,
        Guid organizationId,
        string organizationName,
        string urgentStatus = "none")
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE witness.cases
            SET status=$2, urgent_status=$3, current_owner_role=$4,
                current_owner_user_id=$5, current_owner_name=$6,
                owning_org_id=$7, current_owner_org_id=$7,
                owning_org_name=$8, current_owner_org_name=$8
            WHERE id=$1
            """);
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(urgentStatus);
        command.Parameters.AddWithValue(ownerRole);
        command.Parameters.AddWithValue(owner.UserId);
        command.Parameters.AddWithValue(owner.DisplayName);
        command.Parameters.AddWithValue(organizationId);
        command.Parameters.AddWithValue(organizationName);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<MutationSnapshot> SnapshotAsync(
        NpgsqlDataSource dataSource,
        Guid caseId,
        int formNumber)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT witness_case.status,
                   witness_case.row_version,
                   (SELECT COUNT(*) FROM witness.forms form_row WHERE form_row.case_id=witness_case.id),
                   (SELECT COALESCE(MAX(form_row.version),0) FROM witness.forms form_row
                    WHERE form_row.case_id=witness_case.id AND form_row.form_number=$2),
                   (SELECT COUNT(*) FROM witness.form_versions version_row WHERE version_row.case_id=witness_case.id),
                   (SELECT COUNT(*) FROM witness.form_signatures signature_row
                    JOIN witness.forms form_row ON form_row.id=signature_row.form_id
                    WHERE form_row.case_id=witness_case.id),
                   (SELECT COUNT(*) FROM witness.workflow_events event_row WHERE event_row.case_id=witness_case.id),
                   (SELECT COUNT(*) FROM witness.audit_events audit_row WHERE audit_row.case_id=witness_case.id),
                   COALESCE((SELECT form_row.values_data::text FROM witness.forms form_row
                             WHERE form_row.case_id=witness_case.id AND form_row.form_number=$2), '{}')
            FROM witness.cases witness_case
            WHERE witness_case.id=$1
            """);
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(formNumber);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new MutationSnapshot(
            reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetString(8));
    }

    private static async Task DeleteCasesAsync(NpgsqlDataSource dataSource, IEnumerable<Guid> caseIds)
    {
        var ids = caseIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
            return;
        await using var command = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=ANY($1)");
        command.Parameters.AddWithValue(ids);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record MutationSnapshot(
        string CaseStatus,
        long CaseVersion,
        long FormCount,
        int TargetFormVersion,
        long FormVersionCount,
        long SignatureCount,
        long WorkflowCount,
        long BusinessAuditCount,
        string TargetFormValues);
}
