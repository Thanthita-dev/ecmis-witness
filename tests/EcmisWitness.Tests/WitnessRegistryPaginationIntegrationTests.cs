using System.Text.Json;
using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Forms;
using EcmisWitness.Api.Infrastructure;
using EcmisWitness.Api.Security;
using Npgsql;

namespace EcmisWitness.Tests;

[Collection(WitnessIdempotencyPostgresCollection.Name)]
public sealed class WitnessRegistryPaginationIntegrationTests
{
    [Fact]
    public void Registry_contract_centralizes_search_sort_and_validation_rules()
    {
        Assert.Contains("new_case_subject", WitnessRegistryQueryContract.MainCaseSummaryFields);
        Assert.Contains("linked_case_no", WitnessRegistryQueryContract.MainCaseSummaryFields);
        Assert.Contains("complaint.metadata_json.red_case_no", WitnessRegistryQueryContract.MainCaseLinkFields);
        Assert.Equal("updatedAt", WitnessRegistryQueryContract.CanonicalSortBy("UPDATEDAT"));
        Assert.Equal("asc", WitnessRegistryQueryContract.CanonicalSortDirection(" ASC "));
        Assert.Contains("request_no", WitnessRegistryQueryContract.StableTieBreakerSql);
        Assert.Contains("id", WitnessRegistryQueryContract.StableTieBreakerSql);
        Assert.Throws<WitnessWorkflowException>(() => WitnessRegistryQueryContract.CanonicalSortBy("updated_at; DROP TABLE witness.cases"));
        Assert.Throws<WitnessWorkflowException>(() => WitnessRegistryQueryContract.CanonicalSortDirection("sideways"));
    }

    [Fact]
    public void Paged_response_contract_contains_required_metadata()
    {
        var payload = new WitnessPagedResultDto<string>(
            ["one"], 2, 10, 21, 3, "requestNumber", "asc",
            new Dictionary<string, long> { ["staff_review"] = 21 });
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.Equal(10, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(21, root.GetProperty("total").GetInt64());
        Assert.Equal(3, root.GetProperty("totalPages").GetInt32());
        Assert.Equal("requestNumber", root.GetProperty("sortBy").GetString());
        Assert.Equal("asc", root.GetProperty("sortDirection").GetString());
        Assert.Single(root.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Pagination_main_case_search_scope_masking_and_no_mutation_are_enforced_by_postgresql()
    {
        var connectionString = ConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString),
            "ต้องกำหนด ConnectionStrings__Ecmis เพื่อรัน PostgreSQL integration test");

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        await new WitnessDatabaseInitializer(dataSource).InitializeAsync();
        var repository = new WitnessRepository(dataSource, new WitnessWorkflowStateMachine(), new WitnessFormPolicy());
        var batch = $"E2E-WIT-20260722-{Guid.NewGuid():N}";
        var organizationA = Guid.NewGuid();
        var organizationB = Guid.NewGuid();
        var creatorA = Officer("registry-a", organizationA, "สำนัก E2E A", create: true);
        var creatorB = Officer("registry-b", organizationB, "สำนัก E2E B", create: true);
        var reviewerA = Reviewer("reviewer-a", organizationA, "สำนัก E2E A", pii: true);
        var reviewerB = Reviewer("reviewer-b", organizationB, "สำนัก E2E B", pii: true);
        var maskedA = Reviewer("masked-a", organizationA, "สำนัก E2E A", pii: false);
        var globalReader = new WitnessUserContext(
            Guid.NewGuid(), "registry-global", "E2E-TEST Global Reader", "ผู้ดูแลระบบ",
            new HashSet<string> { "super_admin" },
            new HashSet<string> { WitnessPermissions.ViewPii });
        var caseIds = new List<Guid>();

        try
        {
            for (var index = 1; index <= 26; index++)
            {
                var created = await CreateSubmittedAsync(repository, creatorA, batch, index);
                caseIds.Add(created.Case.Id);
            }
            for (var index = 27; index <= 29; index++)
            {
                var created = await CreateSubmittedAsync(repository, creatorB, batch, index);
                caseIds.Add(created.Case.Id);
            }

            var mainCaseId = caseIds[5];
            const string subject = "E2E-TEST คดีหลักใหม่ MainCase %_ O'Reilly ภาษาไทย";
            await repository.RecordNewMainCaseAsync(mainCaseId,
                new RecordNewMainCaseRequest(subject, "ไม่พบคดีเดิมในระบบ E2E", "สูง", 1),
                creatorA, "127.0.0.1", default);

            var before = await SnapshotAsync(dataSource, caseIds);

            var first = await repository.ListPagedAsync(reviewerA,
                new WitnessCaseSearchQuery(Search: batch, Page: 1, PageSize: 10,
                    SortBy: "requestNumber", SortDirection: "asc"), default);
            var middle = await repository.ListPagedAsync(reviewerA,
                new WitnessCaseSearchQuery(Search: batch, Page: 2, PageSize: 10,
                    SortBy: "requestNumber", SortDirection: "asc"), default);
            var last = await repository.ListPagedAsync(reviewerA,
                new WitnessCaseSearchQuery(Search: batch, Page: 3, PageSize: 10,
                    SortBy: "requestNumber", SortDirection: "asc"), default);
            var beyond = await repository.ListPagedAsync(reviewerA,
                new WitnessCaseSearchQuery(Search: batch, Page: 4, PageSize: 10,
                    SortBy: "requestNumber", SortDirection: "asc"), default);

            Assert.Equal(26, first.Total);
            Assert.Equal(3, first.TotalPages);
            Assert.Equal(10, first.Items.Count);
            Assert.Equal(10, middle.Items.Count);
            Assert.Equal(6, last.Items.Count);
            Assert.Empty(beyond.Items);
            Assert.Equal(26, first.StatusCounts[WitnessStatuses.StaffReview]);
            Assert.Empty(first.Items.Select(item => item.Id)
                .Intersect(middle.Items.Select(item => item.Id)));
            Assert.Empty(middle.Items.Select(item => item.Id)
                .Intersect(last.Items.Select(item => item.Id)));
            Assert.Equal(26, first.Items.Concat(middle.Items).Concat(last.Items)
                .Select(item => item.Id).Distinct().Count());
            Assert.True(first.Items.SequenceEqual(first.Items.OrderBy(item => item.RequestNo), SummaryIdComparer.Instance));

            var sameSnapshot = await repository.ListPagedAsync(reviewerA,
                new WitnessCaseSearchQuery(Search: batch, Page: 1, PageSize: 10,
                    SortBy: "updatedAt", SortDirection: "desc"), default);
            var sameSnapshotAgain = await repository.ListPagedAsync(reviewerA,
                new WitnessCaseSearchQuery(Search: batch, Page: 1, PageSize: 10,
                    SortBy: "updatedAt", SortDirection: "desc"), default);
            Assert.Equal(sameSnapshot.Items.Select(item => item.Id), sameSnapshotAgain.Items.Select(item => item.Id));

            var maximum = await repository.ListPagedAsync(globalReader,
                new WitnessCaseSearchQuery(Search: batch, Page: 1,
                    PageSize: WitnessRegistryQueryContract.MaximumPageSize), default);
            Assert.Equal(29, maximum.Total);
            Assert.Equal(29, maximum.Items.Count);
            Assert.All(maximum.Items, item => Assert.Contains("E2E-TEST", item.WitnessDisplayName));

            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.ListPagedAsync(reviewerA,
                new WitnessCaseSearchQuery(Page: 0), default));
            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.ListPagedAsync(reviewerA,
                new WitnessCaseSearchQuery(PageSize: 101), default));
            await Assert.ThrowsAsync<WitnessWorkflowException>(() => repository.ListPagedAsync(reviewerA,
                new WitnessCaseSearchQuery(SortBy: "updated_at;select pg_sleep(1)"), default));

            foreach (var term in new[] { "maincase", "คดีหลักใหม่", "  MainCase  ", "%_ O'Reilly", "%", "_" })
            {
                var found = await repository.ListPagedAsync(reviewerA,
                    new WitnessCaseSearchQuery(MainCase: term, PageSize: 10), default);
                Assert.Equal(1, found.Total);
                Assert.Equal(mainCaseId, Assert.Single(found.Items).Id);
            }
            foreach (var term in new[] { "ไม่มีข้อมูลนี้", "' OR 1=1 --" })
            {
                var notFound = await repository.ListPagedAsync(reviewerA,
                    new WitnessCaseSearchQuery(MainCase: term, PageSize: 10), default);
                Assert.Equal(0, notFound.Total);
                Assert.Empty(notFound.Items);
            }

            var crossOrganization = await repository.ListPagedAsync(reviewerB,
                new WitnessCaseSearchQuery(MainCase: "MainCase", PageSize: 10), default);
            Assert.Equal(0, crossOrganization.Total);
            Assert.Empty(crossOrganization.Items);

            var masked = await repository.ListPagedAsync(maskedA,
                new WitnessCaseSearchQuery(Search: batch, Page: 1, PageSize: 10), default);
            Assert.Equal(26, masked.Total);
            Assert.All(masked.Items, item => Assert.DoesNotContain(batch, item.WitnessDisplayName));

            var grouped = await repository.ListPagedAsync(reviewerA,
                new WitnessCaseSearchQuery(Search: batch, Page: 1, PageSize: 10,
                    StatusGroup: "staff_review"), default);
            Assert.Equal(26, grouped.Total);
            Assert.All(grouped.Items, item => Assert.Equal(WitnessStatuses.StaffReview, item.Status));

            var after = await SnapshotAsync(dataSource, caseIds);
            Assert.Equal(before, after);
        }
        finally
        {
            if (caseIds.Count > 0) await DeleteCasesAsync(dataSource, caseIds);
        }
    }

    private static async Task<WitnessCaseDetailDto> CreateSubmittedAsync(
        WitnessRepository repository,
        WitnessUserContext actor,
        string batch,
        int index)
    {
        var values = CompleteValues(1);
        values["petitioner_first_name"] = "E2E-TEST";
        values["petitioner_last_name"] = $"{batch}-{index:000}";
        values["witness_first_name"] = "E2E-TEST";
        values["witness_last_name"] = $"{batch}-{index:000}";
        return await repository.CreateAsync(new CreateWitnessCaseRequest(
            1, values, Submit: true,
            IdempotencyKey: $"registry-{batch}-{index:000}"), actor, "127.0.0.1", default);
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
                    WitnessFormFieldType.MultiSelect => JsonSerializer.Serialize(new[] { field.Options?.FirstOrDefault() ?? "ตัวเลือกทดสอบ" }),
                    WitnessFormFieldType.Select => field.Options?.FirstOrDefault() ?? "ข้อมูลทดสอบ",
                    WitnessFormFieldType.Address => JsonSerializer.Serialize((field.Columns ?? []).ToDictionary(item => item.Key, _ => "ข้อมูลทดสอบ")),
                    WitnessFormFieldType.Repeating => JsonSerializer.Serialize(new[] { (field.Columns ?? []).ToDictionary(item => item.Key, _ => "ข้อมูลทดสอบ") }),
                    WitnessFormFieldType.Date => "2026-07-20",
                    WitnessFormFieldType.Number => "1",
                    _ => "ข้อมูลทดสอบ"
                };
        }
        values["related_people"] = "[]";
        values["threat_status"] = "ไม่มี";
        values["petitioner_officer_id"] = "";
        values["witness_officer_id"] = "";
        return values;
    }

    private static WitnessUserContext Officer(string name, Guid organization, string organizationName, bool create)
        => new(Guid.NewGuid(), name, $"E2E-TEST {name}", "เจ้าหน้าที่ ป.ป.ท.",
            new HashSet<string> { "witness_officer" },
            new HashSet<string>(new[]
            {
                WitnessPermissions.ViewMasked,
                WitnessPermissions.ViewPii,
                WitnessPermissions.OfficerReview
            }.Concat(create ? new[] { WitnessPermissions.Create } : [])),
            organization, organizationName, "department");

    private static WitnessUserContext Reviewer(string name, Guid organization, string organizationName, bool pii)
        => new(Guid.NewGuid(), name, $"E2E-TEST {name}", "ผู้บังคับบัญชาชั้นต้น",
            new HashSet<string> { "witness_supervisor" },
            new HashSet<string>(new[]
            {
                WitnessPermissions.ViewMasked,
                WitnessPermissions.SupervisorReview
            }.Concat(pii ? new[] { WitnessPermissions.ViewPii } : [])),
            organization, organizationName, "department");

    private static async Task<string> SnapshotAsync(NpgsqlDataSource dataSource, IReadOnlyList<Guid> caseIds)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'cases', (SELECT jsonb_agg(jsonb_build_array(id,status,row_version) ORDER BY id) FROM witness.cases WHERE id=ANY($1)),
                'forms', (SELECT jsonb_agg(jsonb_build_array(id,case_id,version,status) ORDER BY id) FROM witness.forms WHERE case_id=ANY($1)),
                'workflow', (SELECT COUNT(*) FROM witness.workflow_events WHERE case_id=ANY($1)),
                'audit', (SELECT COUNT(*) FROM witness.audit_events WHERE case_id=ANY($1)),
                'notifications', (SELECT COUNT(*) FROM witness.notifications WHERE case_id=ANY($1)),
                'idempotency', (SELECT COUNT(*) FROM witness.idempotency_records WHERE resource_id=ANY($1))
            )::text
            """);
        cmd.Parameters.AddWithValue(caseIds.ToArray());
        return (string)(await cmd.ExecuteScalarAsync() ?? "");
    }

    private static async Task DeleteCasesAsync(NpgsqlDataSource dataSource, IReadOnlyList<Guid> caseIds)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM witness.cases WHERE id=ANY($1)");
        cmd.Parameters.AddWithValue(caseIds.ToArray());
        await cmd.ExecuteNonQueryAsync();
    }

    private static string? ConnectionString()
        => Environment.GetEnvironmentVariable("ConnectionStrings__Ecmis");

    private sealed class SummaryIdComparer : IEqualityComparer<WitnessCaseSummaryDto>
    {
        public static SummaryIdComparer Instance { get; } = new();
        public bool Equals(WitnessCaseSummaryDto? x, WitnessCaseSummaryDto? y) => x?.Id == y?.Id;
        public int GetHashCode(WitnessCaseSummaryDto obj) => obj.Id.GetHashCode();
    }
}
