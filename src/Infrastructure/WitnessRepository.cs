using System.Security.Cryptography;
using System.Globalization;
using System.Data;
using System.Text.Json;
using EcmisWitness.Api.Contracts;
using EcmisWitness.Api.Domain;
using EcmisWitness.Api.Security;
using EcmisWitness.Api.Forms;
using Npgsql;
using NpgsqlTypes;

namespace EcmisWitness.Api.Infrastructure;

public sealed class WitnessRepository(
    NpgsqlDataSource dataSource,
    WitnessWorkflowStateMachine stateMachine,
    WitnessFormPolicy formPolicy,
    ILogger<WitnessRepository>? logger = null,
    IConfiguration? configuration = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string AppealNewEvidence = "appeal_new_evidence";
    private const string AppealLateFilingReason = "late_filing_reason";
    private const string AppealExternalResult = "external_result";
    private const string AppealResultNoticeProof = "appeal_result_notice_proof";
    private readonly TimeSpan kb1ShareLifetime = TimeSpan.FromMinutes(
        Math.Clamp(configuration?.GetValue("Witness:Kb1ShareLinkLifetimeMinutes", 60) ?? 60, 10, 1440));

    public Task<IReadOnlyList<WitnessCaseSummaryDto>> ListAsync(
        WitnessUserContext user,
        string? status,
        string? search,
        CancellationToken ct)
        => ListAsync(user, new WitnessCaseSearchQuery(status, search), ct);

    public Task<IReadOnlyList<WitnessCaseSummaryDto>> ListAsync(
        WitnessUserContext user,
        WitnessCaseSearchQuery query,
        CancellationToken ct)
        => ExecuteReadWithRetryAsync(
            () => ListAllCoreAsync(user, query, ct),
            nameof(ListAsync),
            ct);

    public Task<WitnessPagedResultDto<WitnessCaseSummaryDto>> ListPagedAsync(
        WitnessUserContext user,
        WitnessCaseSearchQuery query,
        CancellationToken ct)
        => ExecuteReadWithRetryAsync(
            () => ListCoreAsync(user, query, paginate: true, ct),
            nameof(ListPagedAsync),
            ct);

    private async Task<IReadOnlyList<WitnessCaseSummaryDto>> ListAllCoreAsync(
        WitnessUserContext user,
        WitnessCaseSearchQuery query,
        CancellationToken ct)
        => (await ListCoreAsync(user, query, paginate: false, ct)).Items;

    public async Task<IReadOnlyList<WitnessNotificationDto>> ListNotificationsAsync(
        WitnessUserContext user,
        bool unreadOnly,
        CancellationToken ct)
    {
        EnsureView(user);
        await using var cmd = dataSource.CreateCommand("""
            SELECT notification.id, notification.case_id, witness_case.request_no,
                   notification.alert_type, notification.due_at, notification.severity,
                   notification.title, notification.message, notification.status,
                   notification.created_at
            FROM witness.notifications notification
            JOIN witness.cases witness_case ON witness_case.id=notification.case_id
            WHERE (NOT $1 OR notification.status='unread')
              AND (
                    $2
                    OR witness_case.created_by=$3
                    OR witness_case.current_owner_user_id=$3
                    OR EXISTS (
                        SELECT 1 FROM witness.case_assignments assignment
                        WHERE assignment.case_id=witness_case.id
                          AND assignment.user_id=$3
                          AND assignment.ended_at IS NULL)
                    OR ($4 AND (
                        witness_case.owning_org_id=$5::uuid
                        OR witness_case.current_owner_org_id=$5::uuid))
                    OR ($6 AND witness_case.current_owner_role='external_module')
                  )
            ORDER BY CASE notification.severity WHEN 'critical' THEN 0 WHEN 'high' THEN 1 ELSE 2 END,
                     notification.due_at NULLS LAST, notification.created_at DESC
            LIMIT 100
            """);
        cmd.Parameters.AddWithValue(unreadOnly);
        cmd.Parameters.AddWithValue(user.IsGlobalAdministrator);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(CanReviewOrganizationScope(user));
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)user.OrganizationId ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Uuid
        });
        cmd.Parameters.AddWithValue(user.HasPermission(WitnessPermissions.ExternalReceive));

        var results = new List<WitnessNotificationDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new WitnessNotificationDto(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        return results;
    }

    public async Task AcknowledgeNotificationAsync(
        Guid notificationId,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        EnsureView(user);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        Guid caseId;
        await using (var lookup = new NpgsqlCommand(
            "SELECT case_id FROM witness.notifications WHERE id=$1 FOR UPDATE", connection, tx))
        {
            lookup.Parameters.AddWithValue(notificationId);
            var raw = await lookup.ExecuteScalarAsync(ct)
                ?? throw new WitnessWorkflowException("ไม่พบรายการแจ้งเตือน");
            caseId = (Guid)raw;
        }
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        var now = DateTimeOffset.UtcNow;
        await using (var update = new NpgsqlCommand("""
            UPDATE witness.notifications
            SET status='acknowledged', acknowledged_by=$2, acknowledged_at=$3, updated_at=$3
            WHERE id=$1
            """, connection, tx))
        {
            update.Parameters.AddWithValue(notificationId);
            update.Parameters.AddWithValue(user.UserId);
            update.Parameters.AddWithValue(now);
            await update.ExecuteNonQueryAsync(ct);
        }
        await InsertAuditAsync(connection, tx, caseId, "notification.acknowledged", "notification",
            notificationId.ToString(), user, ipAddress, "{}", now, ct);
        await tx.CommitAsync(ct);
    }

    private async Task<WitnessPagedResultDto<WitnessCaseSummaryDto>> ListCoreAsync(
        WitnessUserContext user,
        WitnessCaseSearchQuery query,
        bool paginate,
        CancellationToken ct)
    {
        EnsureView(user);
        if (query.Page < 1)
            throw new WitnessWorkflowException("หมายเลขหน้าต้องตั้งแต่ 1 ขึ้นไป");
        if (query.PageSize < 1 || query.PageSize > WitnessRegistryQueryContract.MaximumPageSize)
            throw new WitnessWorkflowException(
                $"จำนวนรายการต่อหน้าต้องอยู่ระหว่าง 1 ถึง {WitnessRegistryQueryContract.MaximumPageSize}");
        var sortBy = WitnessRegistryQueryContract.CanonicalSortBy(query.SortBy);
        var sortDirection = WitnessRegistryQueryContract.CanonicalSortDirection(query.SortDirection);
        var statusGroup = WitnessRegistryQueryContract.CanonicalStatusGroup(query.StatusGroup);
        var sortColumn = WitnessRegistryQueryContract.SortColumns[sortBy];
        var sortSql = $"visible_case.{sortColumn} {sortDirection.ToUpperInvariant()} NULLS LAST, {WitnessRegistryQueryContract.StableTieBreakerSql}";
        var offset = checked((long)(query.Page - 1) * query.PageSize);

        const string visibleCasesCte = """
            WITH visible_cases AS (
                SELECT candidate.*
                FROM witness.cases candidate
                WHERE (candidate.status <> 'intake_draft' OR candidate.created_by = $3 OR $4)
                  AND (
                        $4
                        OR candidate.created_by = $3
                        OR candidate.current_owner_user_id = $3
                        OR EXISTS (
                            SELECT 1 FROM witness.case_assignments assignment
                            WHERE assignment.case_id = candidate.id
                              AND assignment.user_id = $3
                              AND assignment.ended_at IS NULL)
                        OR ($5 AND (
                            candidate.owning_org_id = $6::uuid
                            OR candidate.current_owner_org_id = $6::uuid))
                        OR ($7 AND candidate.current_owner_role = 'external_module')
                      )
            )
            """;
        var commonFilters = $"""
              ($2::text IS NULL
                   OR visible_case.request_no ILIKE '%' || $2 || '%' ESCAPE '\'
                   OR visible_case.summary_data->>'witness_code' ILIKE '%' || $2 || '%' ESCAPE '\'
                   OR visible_case.summary_data->>'witness_name' ILIKE '%' || $2 || '%' ESCAPE '\'
                   OR visible_case.summary_data->>'petitioner_name' ILIKE '%' || $2 || '%' ESCAPE '\')
              AND ($8::int IS NULL OR visible_case.intake_form_number=$8 OR EXISTS(
                    SELECT 1 FROM witness.forms searched_form
                    WHERE searched_form.case_id=visible_case.id AND searched_form.form_number=$8))
              AND ($9::date IS NULL OR visible_case.created_at >= $9::date)
              AND ($10::date IS NULL OR visible_case.created_at < ($10::date + 1))
              AND ($11::boolean IS NULL OR visible_case.is_urgent=$11)
              AND ($12::text IS NULL OR visible_case.risk_level=$12)
              AND ($13::text IS NULL OR visible_case.current_owner_name ILIKE '%' || $13 || '%' ESCAPE '\')
              AND ($14::text IS NULL OR {WitnessRegistryQueryContract.MainCasePredicate("visible_case", "$14")})
              AND ($15::text IS NULL OR
                    ($15='within-30-days' AND visible_case.status='appeal_window' AND visible_case.appeal_deadline >= (NOW() AT TIME ZONE 'Asia/Bangkok')::date)
                    OR ($15='overdue' AND visible_case.status='appeal_window' AND visible_case.appeal_deadline < (NOW() AT TIME ZONE 'Asia/Bangkok')::date)
                    OR ($15='submitted' AND visible_case.status='appeal_external_pending')
                    OR ($15='decided' AND visible_case.status IN ('appeal_decided','appeal_result_notice_sent','appeal_result_received')))
              AND ($16::date IS NULL OR EXISTS(
                    SELECT 1 FROM witness.protection_periods expiry
                    WHERE expiry.case_id=visible_case.id AND expiry.end_date <= $16))
              AND ($17::text IS NULL OR visible_case.status=$17)
              AND ($18::text IS NULL OR visible_case.owning_org_name ILIKE '%' || $18 || '%' ESCAPE '\'
                                      OR visible_case.current_owner_org_name ILIKE '%' || $18 || '%' ESCAPE '\')
            """;
        var fullFilters = $"""
            ($1::text IS NULL OR visible_case.status=$1)
            AND {WitnessRegistryQueryContract.StatusGroupPredicate("visible_case", "$19")}
            AND {commonFilters}
            """;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        long total;
        await using (var count = new NpgsqlCommand(
                         $"{visibleCasesCte} SELECT COUNT(*) FROM visible_cases visible_case WHERE {fullFilters}",
                         connection, tx))
        {
            AddRegistryParameters(count, user, query, statusGroup);
            total = (long)(await count.ExecuteScalarAsync(ct) ?? 0L);
        }

        var statusCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using (var counts = new NpgsqlCommand(
                         $"{visibleCasesCte} SELECT visible_case.status, COUNT(*) FROM visible_cases visible_case WHERE ($1::text IS NULL OR $1 IS NOT NULL) AND {commonFilters} AND ($19::text IS NULL OR $19 IS NOT NULL) GROUP BY visible_case.status",
                         connection, tx))
        {
            AddRegistryParameters(counts, user, query, statusGroup);
            await using var countReader = await counts.ExecuteReaderAsync(ct);
            while (await countReader.ReadAsync(ct))
                statusCounts[countReader.GetString(0)] = countReader.GetInt64(1);
        }

        var pagingSql = paginate ? "LIMIT $20 OFFSET $21" : "";
        await using var cmd = new NpgsqlCommand($"""
            {visibleCasesCte}
            SELECT visible_case.id, visible_case.request_no, visible_case.intake_form_number,
                   visible_case.status, visible_case.urgent_status,
                   visible_case.current_owner_role, visible_case.current_owner_name,
                   visible_case.risk_level, visible_case.is_urgent,
                   visible_case.summary_data, visible_case.row_version,
                   visible_case.created_at, visible_case.updated_at, visible_case.appeal_deadline,
                   (SELECT MAX(period.end_date) FROM witness.protection_periods period WHERE period.case_id=visible_case.id),
                   (SELECT COALESCE(SUM((period.end_date-period.start_date)+1),0)::int FROM witness.protection_periods period WHERE period.case_id=visible_case.id),
                   COALESCE(visible_case.owning_org_name, '')
            FROM visible_cases visible_case
            WHERE {fullFilters}
            ORDER BY {sortSql}
            {pagingSql}
            """, connection, tx);
        AddRegistryParameters(cmd, user, query, statusGroup);
        if (paginate)
        {
            cmd.Parameters.AddWithValue(query.PageSize);
            cmd.Parameters.AddWithValue(offset);
        }
        var results = new List<WitnessCaseSummaryDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadSummary(reader, user));
        await reader.CloseAsync();
        await tx.CommitAsync(ct);
        var totalPages = total == 0 ? 0 : checked((int)((total + query.PageSize - 1) / query.PageSize));
        return new WitnessPagedResultDto<WitnessCaseSummaryDto>(
            results, query.Page, query.PageSize, total, totalPages,
            sortBy, sortDirection, statusCounts);
    }

    private static void AddRegistryParameters(
        NpgsqlCommand cmd,
        WitnessUserContext user,
        WitnessCaseSearchQuery query,
        string? statusGroup)
    {
        cmd.Parameters.AddWithValue((object?)Normalize(query.Status) ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)EscapeLikePattern(query.Search) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(user.IsGlobalAdministrator);
        cmd.Parameters.AddWithValue(CanReviewOrganizationScope(user));
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)user.OrganizationId ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Uuid
        });
        cmd.Parameters.AddWithValue(user.HasPermission(WitnessPermissions.ExternalReceive));
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)query.FormNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)query.ReceivedFrom ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)query.ReceivedTo ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)query.IsUrgent ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
        cmd.Parameters.AddWithValue((object?)Normalize(query.RiskLevel) ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)EscapeLikePattern(query.Owner) ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)EscapeLikePattern(query.MainCase) ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)Normalize(query.AppealSla) ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)query.ProtectionExpiryBefore ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.AddWithValue((object?)Normalize(query.TransferStatus) ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)EscapeLikePattern(query.Organization) ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)statusGroup ?? DBNull.Value);
    }

    private static string? EscapeLikePattern(string? value)
    {
        var normalized = Normalize(value);
        return normalized?.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    public Task<WitnessCaseDetailDto?> GetDetailAsync(Guid caseId, WitnessUserContext user, CancellationToken ct)
        => ExecuteReadWithRetryAsync(
            () => GetDetailCoreAsync(caseId, user, ct),
            nameof(GetDetailAsync),
            ct);

    public async Task<WitnessCaseDetailDto?> GetDetailAsync(
        Guid caseId,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        var result = await GetDetailAsync(caseId, user, ct);
        if (result is not null)
            await RecordSecretAccessAsync(caseId, "case.secret.viewed", "case", caseId.ToString(), user, ipAddress, ct);
        return result;
    }

    private async Task<WitnessCaseDetailDto?> GetDetailCoreAsync(Guid caseId, WitnessUserContext user, CancellationToken ct)
    {
        EnsureView(user);
        var summary = await GetSummaryAsync(caseId, user, ct);
        if (summary is null)
            return null;

        var forms = await ListFormsAsync(caseId, ct);
        var attachments = await ListAttachmentsAsync(caseId, ct);
        var events = await ListWorkflowEventsAsync(caseId, ct);
        var assignments = await ListAssignmentsAsync(caseId, ct);
        var intake = await GetFormAsync(caseId, summary.IntakeFormNumber, user, ct);
        var completedForms = forms.Where(form => form.Status is "completed" or "signed")
            .Select(form => form.FormNumber)
            .ToHashSet();
        var availableActions = stateMachine.GetAvailableActions(summary.Status, summary.UrgentStatus, user, completedForms);
        availableActions = await FilterUnsignedActionsAsync(caseId, summary.Status, availableActions, ct);
        if (summary.Status == WitnessStatuses.NoticeSent)
        {
            var currentNoticeForm = await GetLatestNoticeFormNumberAsync(caseId, ct);
            availableActions = FilterNoticeReceiptActions(availableActions, currentNoticeForm);
        }
        var caseLink = await GetCaseLinkAsync(caseId, ct);
        var currentAppeal = await GetCurrentAppealAsync(caseId, ct);
        var currentAppealResultNotice = currentAppeal is null
            ? null
            : await GetCurrentAppealResultNoticeAsync(caseId, currentAppeal.Id, ct);
        availableActions = FilterAppealResultLifecycleActions(
            summary.Status, availableActions, currentAppeal, currentAppealResultNotice);
        return new WitnessCaseDetailDto(
            summary,
            intake?.Values ?? new Dictionary<string, string>(),
            forms,
            attachments,
            events,
            availableActions,
            caseLink,
            assignments,
            currentAppeal,
            currentAppealResultNotice);
    }

    public async Task<WitnessCaseDetailDto> AssignCaseAsync(
        Guid caseId,
        CreateWitnessCaseAssignmentRequest request,
        WitnessAssignmentTarget target,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (!user.HasPermission(WitnessPermissions.AssignmentManage))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์มอบหมายผู้รับผิดชอบแฟ้มคุ้มครองพยาน");
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new WitnessWorkflowException("กรุณาระบุเหตุผลการมอบหมาย");
        var assignmentRole = request.AssignmentRole.Trim().ToLowerInvariant();
        if (assignmentRole is not ("officer" or "notice_officer" or "protection_officer" or "appeal_officer"))
            throw new WitnessWorkflowException("ประเภทผู้รับมอบหมายไม่ถูกต้อง");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        EnsureVersion(caseRow.Version, request.ExpectedVersion);
        EnsureAssignmentRoleAllowed(caseRow.Status, assignmentRole);

        var caseOrganizationId = caseRow.OwningOrgId ?? caseRow.CurrentOwnerOrgId;
        if (caseOrganizationId.HasValue && caseOrganizationId.Value != target.OrganizationId)
            throw new WitnessAuthorizationException("ผู้รับมอบหมายต้องอยู่ในหน่วยงานเดียวกับแฟ้มตามกิจกรรมที่ 12");
        if (assignmentRole == "protection_officer")
        {
            if (request.SourceFormNumber != 8)
                throw new WitnessWorkflowException("การแต่งตั้งชุดคุ้มครองต้องอ้างอิงแบบ คบ.8");
            var completedForms = await GetCompletedFormNumbersAsync(connection, tx, caseId, ct);
            if (!completedForms.Contains(8))
                throw new WitnessWorkflowException("ต้องจัดทำแบบ คบ.8 ให้สมบูรณ์ก่อนมอบหมายชุดคุ้มครอง");
        }

        var now = DateTimeOffset.UtcNow;
        if (assignmentRole != "protection_officer")
        {
            await using var closePrevious = new NpgsqlCommand("""
                UPDATE witness.case_assignments
                SET ended_at=$3
                WHERE case_id=$1 AND assignment_role=$2 AND ended_at IS NULL
                """, connection, tx);
            closePrevious.Parameters.AddWithValue(caseId);
            closePrevious.Parameters.AddWithValue(assignmentRole);
            closePrevious.Parameters.AddWithValue(now);
            await closePrevious.ExecuteNonQueryAsync(ct);
        }

        var assignmentId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO witness.case_assignments(
                id, case_id, user_id, target_username, assignment_role, org_id,
                organization_name, source_form_number, reason, assigned_by,
                assigned_by_name, assigned_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
            ON CONFLICT (case_id, user_id, assignment_role) WHERE ended_at IS NULL
            DO UPDATE SET source_form_number=EXCLUDED.source_form_number,
                          target_username=EXCLUDED.target_username,
                          organization_name=EXCLUDED.organization_name,
                          reason=EXCLUDED.reason,
                          assigned_by=EXCLUDED.assigned_by,
                          assigned_by_name=EXCLUDED.assigned_by_name,
                          assigned_at=EXCLUDED.assigned_at
            RETURNING id
            """, connection, tx))
        {
            insert.Parameters.AddWithValue(assignmentId);
            insert.Parameters.AddWithValue(caseId);
            insert.Parameters.AddWithValue(target.UserId);
            insert.Parameters.AddWithValue(target.Username);
            insert.Parameters.AddWithValue(assignmentRole);
            insert.Parameters.AddWithValue(target.OrganizationId);
            insert.Parameters.AddWithValue(target.OrganizationName);
            insert.Parameters.AddWithValue((object?)request.SourceFormNumber ?? DBNull.Value);
            insert.Parameters.AddWithValue(request.Reason.Trim());
            insert.Parameters.AddWithValue(user.UserId);
            insert.Parameters.AddWithValue(user.DisplayName);
            insert.Parameters.AddWithValue(now);
            assignmentId = (Guid)(await insert.ExecuteScalarAsync(ct)
                ?? throw new WitnessWorkflowException("ไม่สามารถบันทึกการมอบหมายได้"));
        }

        var isSingleOwner = assignmentRole != "protection_officer";
        await using (var updateCase = new NpgsqlCommand("""
            UPDATE witness.cases
            SET owning_org_id=COALESCE(owning_org_id,$2),
                current_owner_org_id=$2,
                owning_org_name=COALESCE(NULLIF(owning_org_name,''),$3),
                current_owner_org_name=$3,
                current_owner_user_id=CASE WHEN $4 THEN $5 ELSE current_owner_user_id END,
                current_owner_name=CASE WHEN $4 THEN $6 ELSE current_owner_name END,
                row_version=row_version+1,
                updated_at=$7
            WHERE id=$1
            """, connection, tx))
        {
            updateCase.Parameters.AddWithValue(caseId);
            updateCase.Parameters.AddWithValue(target.OrganizationId);
            updateCase.Parameters.AddWithValue(target.OrganizationName);
            updateCase.Parameters.AddWithValue(isSingleOwner);
            updateCase.Parameters.AddWithValue(target.UserId);
            updateCase.Parameters.AddWithValue(target.Username);
            updateCase.Parameters.AddWithValue(now);
            await updateCase.ExecuteNonQueryAsync(ct);
        }
        if (assignmentRole == "protection_officer")
        {
            await using (var revokePriorGrant = new NpgsqlCommand("""
                UPDATE witness.case_secret_grants
                SET revoked_at=$2
                WHERE source_assignment_id=$1 AND revoked_at IS NULL
                """, connection, tx))
            {
                revokePriorGrant.Parameters.AddWithValue(assignmentId);
                revokePriorGrant.Parameters.AddWithValue(now);
                await revokePriorGrant.ExecuteNonQueryAsync(ct);
            }
            await using var grant = new NpgsqlCommand("""
                INSERT INTO witness.case_secret_grants(
                    id, case_id, user_id, data_scope, reason, granted_by,
                    granted_by_name, valid_from, valid_to, source_assignment_id)
                SELECT $1,$2,$3,'safe_house',$4,$5,$6,$7,
                       ((NULLIF(form.values_data->>'end_date','')::date + INTERVAL '1 day')
                           AT TIME ZONE 'Asia/Bangkok'),$8
                FROM witness.forms form
                WHERE form.case_id=$2 AND form.form_number=8 AND form.status IN ('completed','signed')
                """, connection, tx);
            grant.Parameters.AddWithValue(Guid.NewGuid());
            grant.Parameters.AddWithValue(caseId);
            grant.Parameters.AddWithValue(target.UserId);
            grant.Parameters.AddWithValue($"สิทธิ์ Need-to-know ตามการแต่งตั้งชุดคุ้มครอง คบ.8: {request.Reason.Trim()}");
            grant.Parameters.AddWithValue(user.UserId);
            grant.Parameters.AddWithValue(user.DisplayName);
            grant.Parameters.AddWithValue(now);
            grant.Parameters.AddWithValue(assignmentId);
            if (await grant.ExecuteNonQueryAsync(ct) != 1)
                throw new WitnessWorkflowException("ไม่สามารถกำหนดระยะเวลาสิทธิ์ข้อมูล Safe House จากแบบ คบ.8 ได้");
        }
        var details = JsonSerializer.Serialize(new
        {
            assignmentId,
            target.Username,
            assignmentRole,
            target.OrganizationName,
            request.SourceFormNumber,
            request.Reason
        });
        await InsertWorkflowEventAsync(connection, tx, caseId, "case-assigned", caseRow.Status, caseRow.Status,
            user, request.Reason, null, null, details, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "case.assignment.created", "case_assignment",
            assignmentId.ToString(), user, ipAddress, details, now, ct);
        await tx.CommitAsync(ct);

        return await GetDetailAsync(caseId, user, ct)
               ?? throw new WitnessWorkflowException("มอบหมายแล้วแต่ไม่สามารถอ่านแฟ้มกลับได้");
    }

    public async Task<WitnessCaseDetailDto> EndAssignmentAsync(
        Guid caseId,
        Guid assignmentId,
        EndWitnessCaseAssignmentRequest request,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (!user.HasPermission(WitnessPermissions.AssignmentManage))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์ยุติการมอบหมายแฟ้มคุ้มครองพยาน");
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new WitnessWorkflowException("กรุณาระบุเหตุผลการยุติมอบหมาย");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        EnsureVersion(caseRow.Version, request.ExpectedVersion);
        Guid assignedUserId;
        string assignmentRole;
        await using (var lookup = new NpgsqlCommand("""
            SELECT user_id, assignment_role
            FROM witness.case_assignments
            WHERE id=$1 AND case_id=$2 AND ended_at IS NULL
            FOR UPDATE
            """, connection, tx))
        {
            lookup.Parameters.AddWithValue(assignmentId);
            lookup.Parameters.AddWithValue(caseId);
            await using var reader = await lookup.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new WitnessWorkflowException("ไม่พบการมอบหมายที่ยังมีผล");
            assignedUserId = reader.GetGuid(0);
            assignmentRole = reader.GetString(1);
        }

        var now = DateTimeOffset.UtcNow;
        await using (var end = new NpgsqlCommand("""
            UPDATE witness.case_assignments SET ended_at=$3
            WHERE id=$1 AND case_id=$2 AND ended_at IS NULL
            """, connection, tx))
        {
            end.Parameters.AddWithValue(assignmentId);
            end.Parameters.AddWithValue(caseId);
            end.Parameters.AddWithValue(now);
            await end.ExecuteNonQueryAsync(ct);
        }
        await using (var revokeGrant = new NpgsqlCommand("""
            UPDATE witness.case_secret_grants
            SET revoked_at=$2
            WHERE source_assignment_id=$1 AND revoked_at IS NULL
            """, connection, tx))
        {
            revokeGrant.Parameters.AddWithValue(assignmentId);
            revokeGrant.Parameters.AddWithValue(now);
            await revokeGrant.ExecuteNonQueryAsync(ct);
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE witness.cases
            SET current_owner_user_id=CASE WHEN current_owner_user_id=$2 THEN NULL ELSE current_owner_user_id END,
                current_owner_name=CASE WHEN current_owner_user_id=$2 THEN '' ELSE current_owner_name END,
                row_version=row_version+1,
                updated_at=$3
            WHERE id=$1
            """, connection, tx))
        {
            update.Parameters.AddWithValue(caseId);
            update.Parameters.AddWithValue(assignedUserId);
            update.Parameters.AddWithValue(now);
            await update.ExecuteNonQueryAsync(ct);
        }
        var details = JsonSerializer.Serialize(new { assignmentId, assignedUserId, assignmentRole, request.Reason });
        await InsertWorkflowEventAsync(connection, tx, caseId, "case-assignment-ended", caseRow.Status, caseRow.Status,
            user, request.Reason, null, null, details, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "case.assignment.ended", "case_assignment",
            assignmentId.ToString(), user, ipAddress, details, now, ct);
        await tx.CommitAsync(ct);

        return await GetDetailAsync(caseId, user, ct)
               ?? throw new WitnessWorkflowException("ยุติมอบหมายแล้วแต่ไม่สามารถอ่านแฟ้มกลับได้");
    }

    public async Task<IReadOnlyList<WitnessCaseLinkCandidateDto>> SearchMainCasesAsync(
        string search,
        WitnessUserContext user,
        CancellationToken ct)
    {
        if (!user.HasPermission(WitnessPermissions.OfficerReview))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์ค้นหาและเชื่อมโยงคดีหลัก");
        if (string.IsNullOrWhiteSpace(search) || search.Trim().Length < 2)
            throw new WitnessWorkflowException("กรุณาระบุคำค้นอย่างน้อย 2 ตัวอักษร");

        await using var cmd = dataSource.CreateCommand("""
            SELECT c.cmp_complaint_id, c.case_no, c.track_no,
                   COALESCE(p.pcms_no, ''), '',
                   COALESCE(c.metadata_json->>'accused_name', ''),
                   COALESCE(c.metadata_json->>'accused_agency', ''),
                   left(concat_ws(' — ', c.complaint_title, c.complaint_description), 1000),
                   CASE
                     WHEN lower(c.case_no)=lower($1) OR lower(c.track_no)=lower($1)
                          OR lower(COALESCE(p.pcms_no,''))=lower($1) THEN 100
                     WHEN c.case_no ILIKE '%' || $1 || '%' OR c.track_no ILIKE '%' || $1 || '%' THEN 90
                     WHEN concat_ws(' ', c.metadata_json->>'accused_name', c.metadata_json->>'accused_agency') ILIKE '%' || $1 || '%' THEN 70
                     ELSE 50 END AS match_score
            FROM public.tbl_cmp_complaint c
            LEFT JOIN LATERAL (
                SELECT x.pcms_no FROM public.tbl_cmp_pcms_case x
                WHERE x.cmp_complaint_id=c.cmp_complaint_id AND NOT x.is_deleted
                ORDER BY x.created_datetime DESC LIMIT 1) p ON true
            WHERE NOT c.is_deleted
              AND (c.case_no ILIKE '%' || $1 || '%'
                   OR c.track_no ILIKE '%' || $1 || '%'
                   OR COALESCE(p.pcms_no,'') ILIKE '%' || $1 || '%'
                   OR concat_ws(' ', c.metadata_json->>'accused_name', c.metadata_json->>'accused_agency') ILIKE '%' || $1 || '%'
                   OR c.complaint_title ILIKE '%' || $1 || '%'
                   OR c.complaint_description ILIKE '%' || $1 || '%')
            ORDER BY match_score DESC, c.updated_datetime DESC NULLS LAST, c.created_datetime DESC
            LIMIT 20
            """);
        cmd.Parameters.AddWithValue(search.Trim());
        var results = new List<WitnessCaseLinkCandidateDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadCaseLinkCandidate(reader));
        return results;
    }

    public async Task<WitnessCaseDetailDto> LinkMainCaseAsync(
        Guid caseId,
        LinkWitnessMainCaseRequest request,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (!user.HasPermission(WitnessPermissions.OfficerReview))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์เชื่อมโยงคดีหลัก");
        if (string.IsNullOrWhiteSpace(request.RelationshipReason))
            throw new WitnessWorkflowException("กรุณาระบุเหตุผลความเกี่ยวพันระหว่างพยานกับคดี");
        if (request.RiskLevel is not ("ต่ำ" or "ปานกลาง" or "สูง" or "วิกฤต"))
            throw new WitnessWorkflowException("ระดับความเสี่ยงไม่ถูกต้อง");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        EnsureVersion(caseRow.Version, request.ExpectedVersion);
        if (caseRow.Status != WitnessStatuses.StaffReview)
            throw new WitnessWorkflowException("เชื่อมโยงคดีหลักได้เฉพาะขั้นตรวจคำร้องโดยเจ้าหน้าที่");
        var candidate = await LoadComplaintCandidateAsync(connection, tx, request.ComplaintId, ct)
                        ?? throw new WitnessWorkflowException("ไม่พบคดีหลักที่เลือก หรือคดีถูกยกเลิกแล้ว");
        var now = DateTimeOffset.UtcNow;

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO witness.case_links(
                witness_case_id, complaint_id, complaint_case_no, track_no, pcms_no,
                investigation_no, accused_display_name, accused_agency, relationship_reason,
                risk_level, linked_by, linked_by_name, linked_at, updated_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$13)
            ON CONFLICT(witness_case_id) DO UPDATE SET
                complaint_id=EXCLUDED.complaint_id, complaint_case_no=EXCLUDED.complaint_case_no,
                track_no=EXCLUDED.track_no, pcms_no=EXCLUDED.pcms_no,
                investigation_no=EXCLUDED.investigation_no,
                accused_display_name=EXCLUDED.accused_display_name,
                accused_agency=EXCLUDED.accused_agency,
                relationship_reason=EXCLUDED.relationship_reason, risk_level=EXCLUDED.risk_level,
                linked_by=EXCLUDED.linked_by, linked_by_name=EXCLUDED.linked_by_name,
                linked_at=EXCLUDED.linked_at, updated_at=EXCLUDED.updated_at
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(candidate.ComplaintId);
            cmd.Parameters.AddWithValue(candidate.CaseNo);
            cmd.Parameters.AddWithValue(candidate.TrackNo);
            cmd.Parameters.AddWithValue((object?)Normalize(candidate.PcmsNo) ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)Normalize(candidate.InvestigationNo) ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)Normalize(candidate.AccusedDisplayName) ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)Normalize(candidate.AccusedAgency) ?? DBNull.Value);
            cmd.Parameters.AddWithValue(request.RelationshipReason.Trim());
            cmd.Parameters.AddWithValue(request.RiskLevel);
            cmd.Parameters.AddWithValue(user.UserId);
            cmd.Parameters.AddWithValue(user.DisplayName);
            cmd.Parameters.AddWithValue(now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("""
            UPDATE witness.cases
            SET risk_level=$2, row_version=row_version+1, updated_at=$3,
                summary_data=(summary_data - 'new_case_reason' - 'new_case_subject' - 'new_case_recorded_by')
                    || jsonb_build_object(
                        'main_case_status', 'linked', 'linked_case_no', $4::text,
                        'linked_complaint_id', $5::text, 'linked_investigation_no', $6::text)
            WHERE id=$1
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(request.RiskLevel);
            cmd.Parameters.AddWithValue(now);
            cmd.Parameters.AddWithValue(candidate.CaseNo);
            cmd.Parameters.AddWithValue(candidate.ComplaintId);
            cmd.Parameters.AddWithValue(candidate.InvestigationNo);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var details = JsonSerializer.Serialize(new { candidate.ComplaintId, candidate.CaseNo, candidate.InvestigationNo, request.RiskLevel });
        await InsertWorkflowEventAsync(connection, tx, caseId, "link-main-case", caseRow.Status, caseRow.Status,
            user, request.RelationshipReason.Trim(), null, null, details, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "case.main.linked", "complaint", candidate.ComplaintId.ToString(),
            user, ipAddress, details, now, ct);
        await tx.CommitAsync(ct);
        return await GetDetailAsync(caseId, user, ct)
               ?? throw new WitnessWorkflowException("บันทึกการเชื่อมโยงแล้วแต่ไม่สามารถอ่านแฟ้มกลับได้");
    }

    public async Task<WitnessCaseDetailDto> RecordNewMainCaseAsync(
        Guid caseId,
        RecordNewMainCaseRequest request,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (!user.HasPermission(WitnessPermissions.OfficerReview))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์บันทึกข้อมูลคดีใหม่");
        if (string.IsNullOrWhiteSpace(request.CaseSubject))
            throw new WitnessWorkflowException("กรุณาระบุเรื่องหรือข้อเท็จจริงของคดีใหม่");
        if (string.IsNullOrWhiteSpace(request.NewCaseReason))
            throw new WitnessWorkflowException("กรุณาระบุเหตุผลที่ยังไม่สามารถเชื่อมโยงคดีหลักได้");
        if (request.RiskLevel is not ("ต่ำ" or "ปานกลาง" or "สูง" or "วิกฤต"))
            throw new WitnessWorkflowException("ระดับความเสี่ยงไม่ถูกต้อง");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        EnsureVersion(caseRow.Version, request.ExpectedVersion);
        if (caseRow.Status != WitnessStatuses.StaffReview)
            throw new WitnessWorkflowException("บันทึกเป็นคดีใหม่ได้เฉพาะขั้นตรวจคำร้องโดยเจ้าหน้าที่");

        var now = DateTimeOffset.UtcNow;
        await using (var deleteLink = new NpgsqlCommand(
            "DELETE FROM witness.case_links WHERE witness_case_id=$1", connection, tx))
        {
            deleteLink.Parameters.AddWithValue(caseId);
            await deleteLink.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("""
            UPDATE witness.cases
            SET risk_level=$2, row_version=row_version+1, updated_at=$3,
                summary_data=(summary_data - 'linked_case_no' - 'linked_complaint_id' - 'linked_investigation_no')
                    || jsonb_build_object(
                        'main_case_status', 'new_case',
                        'new_case_subject', $4::text,
                        'new_case_reason', $5::text,
                        'new_case_recorded_by', $6::text)
            WHERE id=$1
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(request.RiskLevel);
            cmd.Parameters.AddWithValue(now);
            cmd.Parameters.AddWithValue(request.CaseSubject.Trim());
            cmd.Parameters.AddWithValue(request.NewCaseReason.Trim());
            cmd.Parameters.AddWithValue(user.DisplayName);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var details = JsonSerializer.Serialize(new
        {
            linkType = "new_case",
            caseSubject = request.CaseSubject.Trim(),
            newCaseReason = request.NewCaseReason.Trim(),
            request.RiskLevel
        });
        await InsertWorkflowEventAsync(connection, tx, caseId, "record-new-main-case", caseRow.Status, caseRow.Status,
            user, request.NewCaseReason.Trim(), null, null, details, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "case.main.recorded-new", "case", caseId.ToString(),
            user, ipAddress, details, now, ct);
        await tx.CommitAsync(ct);

        return await GetDetailAsync(caseId, user, ct)
               ?? throw new WitnessWorkflowException("บันทึกข้อมูลคดีใหม่แล้วแต่ไม่สามารถอ่านแฟ้มได้");
    }

    public async Task<WitnessCaseDetailDto> CreateAsync(
        CreateWitnessCaseRequest request,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (!user.HasPermission(WitnessPermissions.Create))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์รับคำร้องใหม่");
        if (request.FormNumber is not (1 or 2))
            throw new WitnessWorkflowException("รับคำร้องใหม่ได้เฉพาะแบบ คบ.1 หรือ คบ.2");
        formPolicy.EnsureCanEdit(request.FormNumber, user);
        formPolicy.Validate(request.FormNumber, request.Values, request.Submit);
        if (request.FormNumber == 2
            && (!request.IsUrgent
                || !request.Values.TryGetValue("unable_to_submit_in_person", out var unable)
                || unable is not ("true" or "1" or "yes" or "on")))
        {
            throw new WitnessWorkflowException("แบบ คบ.2 ใช้เฉพาะกรณีเร่งด่วนและผู้ร้องไม่สามารถมายื่นคำร้องด้วยตนเอง");
        }

        var operation = request.Submit ? "create-request" : "create-draft";
        var requestHash = ComputeIdempotencyHash(new
        {
            request.FormNumber,
            request.IsUrgent,
            request.Submit,
            request.Values
        });
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var idempotency = await ReserveIdempotencyAsync(
            connection, tx, user.UserId, "witness:cases", operation,
            request.IdempotencyKey, requestHash, resourceId: null, ct);
        if (idempotency?.IsReplay == true)
        {
            var replay = DeserializeIdempotentResponse<WitnessCaseDetailDto>(idempotency);
            await tx.CommitAsync(ct);
            return replay;
        }

        var sequence = await NextSequenceAsync(connection, tx, ct);
        var localNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var requestNo = $"WP-{localNow.Year + 543}-{sequence:000000}";
        var caseId = Guid.NewGuid();
        var formId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var values = new Dictionary<string, string>(request.Values, StringComparer.OrdinalIgnoreCase)
        {
            ["request_no"] = requestNo
        };
        var summary = BuildSummaryData(values);
        var valuesJson = JsonSerializer.Serialize(values, JsonOptions);
        var summaryJson = JsonSerializer.Serialize(summary, JsonOptions);
        var contentHash = WitnessFormPolicy.ComputeContentHash(values);
        var initialStatus = request.Submit ? WitnessStatuses.StaffReview : WitnessStatuses.IntakeDraft;
        var formStatus = request.Submit ? "completed" : "draft";
        Guid? ownerUserId = request.Submit
            ? user.HasPermission(WitnessPermissions.OfficerReview) ? user.UserId : (Guid?)null
            : user.UserId;
        var ownerName = ownerUserId.HasValue ? user.DisplayName : "";

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO witness.cases(
                id, request_no, intake_form_number, status, urgent_status,
                current_owner_role, current_owner_user_id, current_owner_name,
                owning_org_id, current_owner_org_id, owning_org_name, current_owner_org_name,
                is_urgent, summary_data, created_by, created_by_name, created_at, updated_at)
            VALUES($1, $2, $3, $4, $5, $11, $12, $13, $14, $14, $15, $15,
                   $6, $7::jsonb, $8, $9, $10, $10)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(requestNo);
            cmd.Parameters.AddWithValue(request.FormNumber);
            cmd.Parameters.AddWithValue(initialStatus);
            cmd.Parameters.AddWithValue(request.IsUrgent ? "awaiting_kb4" : "none");
            cmd.Parameters.AddWithValue(request.IsUrgent);
            cmd.Parameters.AddWithValue(summaryJson);
            cmd.Parameters.AddWithValue(user.UserId);
            cmd.Parameters.AddWithValue(user.DisplayName);
            cmd.Parameters.AddWithValue(now);
            cmd.Parameters.AddWithValue(request.Submit ? "officer" : "petitioner");
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = (object?)ownerUserId ?? DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Uuid
            });
            cmd.Parameters.AddWithValue(ownerName);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = (object?)user.OrganizationId ?? DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Uuid
            });
            cmd.Parameters.AddWithValue((object?)Normalize(user.OrganizationName) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO witness.forms(id, case_id, form_number, version, status, values_data,
                                      updated_by, updated_by_name, updated_at)
            VALUES($1, $2, $3, 1, $4, $5::jsonb, $6, $7, $8)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(formId);
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(request.FormNumber);
            cmd.Parameters.AddWithValue(formStatus);
            cmd.Parameters.AddWithValue(valuesJson);
            cmd.Parameters.AddWithValue(user.UserId);
            cmd.Parameters.AddWithValue(user.DisplayName);
            cmd.Parameters.AddWithValue(now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await InsertFormVersionAsync(connection, tx, formId, caseId, request.FormNumber, 1,
            formStatus, valuesJson, contentHash, user, now, ct);
        await InsertWorkflowEventAsync(connection, tx, caseId, operation,
            initialStatus, initialStatus, user,
            request.Submit ? "รับคำร้องและส่งเข้าคิวตรวจคำร้อง" : "บันทึกร่างคำร้อง",
            null, request.IdempotencyKey, "{}", now, ct);
        await InsertAuditAsync(connection, tx, caseId, "case.created", "case", caseId.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new { requestNo, request.FormNumber, request.IsUrgent }), now, ct);

        var canViewPii = user.IsGlobalAdministrator || user.HasPermission(WitnessPermissions.ViewPii);
        var witnessName = summary.GetValueOrDefault("witness_name", "-");
        var petitionerName = summary.GetValueOrDefault("petitioner_name", "-");
        var urgentStatus = request.IsUrgent ? "awaiting_kb4" : "none";
        var ownerRole = request.Submit ? "officer" : "petitioner";
        var summaryOwnerName = ownerName;
        var caseSummary = new WitnessCaseSummaryDto(
            caseId, requestNo, request.FormNumber, $"คบ.{request.FormNumber}",
            canViewPii ? witnessName : MaskName(witnessName),
            canViewPii ? petitionerName : MaskName(petitionerName),
            initialStatus, WitnessWorkflowStateMachine.StatusLabel(initialStatus),
            ownerRole, summaryOwnerName, "ยังไม่ประเมิน", request.IsUrgent, urgentStatus,
            1, now, now, WitnessWorkflowStateMachine.NextAction(initialStatus));
        var formSummary = new WitnessFormSummaryDto(
            formId, request.FormNumber, 1, formStatus, now, user.DisplayName, 0, []);
        var completedForms = request.Submit ? new HashSet<int> { request.FormNumber } : [];

        var result = new WitnessCaseDetailDto(
            caseSummary,
            values,
            [formSummary],
            [],
            [],
            stateMachine.GetAvailableActions(initialStatus, urgentStatus, user, completedForms),
            null,
            []);
        await CompleteIdempotencyAsync(
            connection, tx, idempotency, caseId, StatusCodes.Status201Created, result, now, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<WitnessKb1ShareLinkDto> CreateKb1ShareLinkAsync(
        Guid caseId,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (!user.HasExplicitPermission(WitnessPermissions.Create))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์ส่งแบบ คบ.1 ให้ผู้ยื่นกรอก");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        if (caseRow.Status != WitnessStatuses.IntakeDraft)
            throw new WitnessWorkflowException("ส่งให้ผู้ยื่นกรอกได้เฉพาะแฟ้ม คบ.1 ที่ยังเป็นร่างคำร้อง");
        var form = await LockFormAsync(connection, tx, caseId, 1, ct)
                   ?? throw new WitnessWorkflowException("ไม่พบแบบ คบ.1 ในแฟ้มคำร้อง");
        if (form.Status != "draft")
            throw new WitnessWorkflowException("แบบ คบ.1 ฉบับนี้ส่งหรือมีลายมือชื่อแล้ว ไม่สามารถสร้างลิงก์กรอกข้อมูลได้");

        var now = DateTimeOffset.UtcNow;
        await using (var revoke = new NpgsqlCommand("""
            UPDATE witness.kb1_share_links
            SET status='revoked', revoked_at=$2, row_version=row_version+1
            WHERE case_id=$1 AND status='active'
            """, connection, tx))
        {
            revoke.Parameters.AddWithValue(caseId);
            revoke.Parameters.AddWithValue(now);
            await revoke.ExecuteNonQueryAsync(ct);
        }

        var id = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var tokenHash = ComputeTokenHash(token);
        var expiresAt = now.Add(kb1ShareLifetime);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO witness.kb1_share_links(
                id, case_id, form_id, token_sha256, status, expires_at,
                created_by, created_by_name, created_at)
            VALUES($1,$2,$3,$4,'active',$5,$6,$7,$8)
            """, connection, tx))
        {
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue(caseId);
            insert.Parameters.AddWithValue(form.Id);
            insert.Parameters.AddWithValue(tokenHash);
            insert.Parameters.AddWithValue(expiresAt);
            insert.Parameters.AddWithValue(user.UserId);
            insert.Parameters.AddWithValue(user.DisplayName);
            insert.Parameters.AddWithValue(now);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await InsertAuditAsync(connection, tx, caseId, "kb1.share.created", "kb1_share_link", id.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new { expiresAt, formVersion = form.Version }), now, ct);
        await tx.CommitAsync(ct);
        return new WitnessKb1ShareLinkDto(id, caseId, caseRow.RequestNo, "active", expiresAt,
            now, null, null, token);
    }

    public async Task<WitnessKb1ShareLinkDto?> GetCurrentKb1ShareLinkAsync(
        Guid caseId,
        WitnessUserContext user,
        CancellationToken ct)
    {
        if (!user.HasExplicitPermission(WitnessPermissions.Create))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์ดูข้อมูลลิงก์แบบ คบ.1");
        if (await GetSummaryAsync(caseId, user, ct) is null)
            return null;

        await using var cmd = dataSource.CreateCommand("""
            SELECT s.id, s.case_id, c.request_no,
                   CASE
                       WHEN s.status='active' AND s.expires_at <= NOW() THEN 'expired'
                       ELSE s.status
                   END AS status,
                   s.expires_at, s.created_at,
                   s.last_accessed_at, s.submitted_at
            FROM witness.kb1_share_links s
            JOIN witness.cases c ON c.id=s.case_id
            WHERE s.case_id=$1
            ORDER BY s.created_at DESC
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue(caseId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return ReadKb1ShareLink(reader);
    }

    public async Task RevokeKb1ShareLinkAsync(
        Guid caseId,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (!user.HasExplicitPermission(WitnessPermissions.Create))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์ยกเลิกลิงก์แบบ คบ.1");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        var now = DateTimeOffset.UtcNow;
        Guid? linkId;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE witness.kb1_share_links
            SET status='revoked', revoked_at=$2, row_version=row_version+1
            WHERE case_id=$1 AND status='active'
            RETURNING id
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(now);
            linkId = await cmd.ExecuteScalarAsync(ct) as Guid?;
        }
        if (!linkId.HasValue)
            throw new WitnessWorkflowException("ไม่พบลิงก์ที่ยังใช้งานได้");
        await InsertAuditAsync(connection, tx, caseId, "kb1.share.revoked", "kb1_share_link", linkId.Value.ToString(),
            user, ipAddress, "{}", now, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<WitnessPublicKb1DraftDto> GetPublicKb1DraftAsync(
        string accessToken,
        string ipAddress,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var link = await LockKb1ShareLinkAsync(connection, tx, accessToken, ct);
        var now = DateTimeOffset.UtcNow;
        EnsureKb1ShareReadable(link, now);
        if (link.Status == "submitted")
        {
            await tx.CommitAsync(ct);
            return new WitnessPublicKb1DraftDto(link.RequestNo, "submitted", link.ExpiresAt,
                link.FormVersion, link.CaseVersion, new Dictionary<string, string>(), []);
        }

        await using (var touch = new NpgsqlCommand("""
            UPDATE witness.kb1_share_links
            SET last_accessed_at=$2, row_version=row_version+1
            WHERE id=$1
            """, connection, tx))
        {
            touch.Parameters.AddWithValue(link.Id);
            touch.Parameters.AddWithValue(now);
            await touch.ExecuteNonQueryAsync(ct);
        }
        var publicUser = PublicKb1User(link);
        await InsertAuditAsync(connection, tx, link.CaseId, "kb1.share.opened", "kb1_share_link", link.Id.ToString(),
            publicUser, ipAddress, "{}", now, ct);
        var attachments = await ListPublicKb1AttachmentsAsync(connection, tx, link.Id, ct);
        await tx.CommitAsync(ct);
        return new WitnessPublicKb1DraftDto(link.RequestNo, link.Status, link.ExpiresAt,
            link.FormVersion, link.CaseVersion, PublicKb1Values(link.Values), attachments);
    }

    public async Task<WitnessPublicKb1DraftDto> SavePublicKb1DraftAsync(
        string accessToken,
        SaveWitnessPublicKb1Request request,
        string ipAddress,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var link = await LockKb1ShareLinkAsync(connection, tx, accessToken, ct);
        var operation = request.Complete ? "public-kb1-submit" : "public-kb1-save";
        var requestHash = ComputeIdempotencyHash(new
        {
            request.Values,
            request.Complete,
            request.ConfirmAccuracy,
            request.ExpectedFormVersion,
            request.ExpectedCaseVersion
        });
        var idempotency = await ReserveIdempotencyAsync(connection, tx, link.Id,
            $"witness:public-kb1:{link.CaseId:D}", operation, request.IdempotencyKey,
            requestHash, link.CaseId, ct);
        if (idempotency?.IsReplay == true)
        {
            var replay = DeserializeIdempotentResponse<WitnessPublicKb1DraftDto>(idempotency);
            await tx.CommitAsync(ct);
            return replay;
        }

        var now = DateTimeOffset.UtcNow;
        EnsureKb1ShareWritable(link, now);
        if (request.Complete && !request.ConfirmAccuracy)
            throw new WitnessWorkflowException("กรุณายืนยันว่าข้อมูลถูกต้องก่อนส่งให้เจ้าหน้าที่");
        EnsureVersion(link.CaseVersion, request.ExpectedCaseVersion);
        if (link.FormVersion != request.ExpectedFormVersion)
            throw new WitnessConcurrencyException("แบบ คบ.1 ถูกแก้ไขแล้ว กรุณาโหลดข้อมูลล่าสุด");

        var values = MergePublicKb1Values(link.Values, request.Values, link.RequestNo);
        formPolicy.Validate(1, values, request.Complete);
        var nextFormVersion = link.FormVersion + 1;
        var formStatus = request.Complete ? "completed" : "draft";
        var valuesJson = JsonSerializer.Serialize(values, JsonOptions);
        var contentHash = WitnessFormPolicy.ComputeContentHash(values);
        var publicUser = PublicKb1User(link);

        await using (var updateForm = new NpgsqlCommand("""
            UPDATE witness.forms
            SET version=$2, status=$3, values_data=$4::jsonb,
                updated_by=$5, updated_by_name=$6, updated_at=$7
            WHERE id=$1
            """, connection, tx))
        {
            updateForm.Parameters.AddWithValue(link.FormId);
            updateForm.Parameters.AddWithValue(nextFormVersion);
            updateForm.Parameters.AddWithValue(formStatus);
            updateForm.Parameters.AddWithValue(valuesJson);
            updateForm.Parameters.AddWithValue(publicUser.UserId);
            updateForm.Parameters.AddWithValue(publicUser.DisplayName);
            updateForm.Parameters.AddWithValue(now);
            await updateForm.ExecuteNonQueryAsync(ct);
        }
        await InsertFormVersionAsync(connection, tx, link.FormId, link.CaseId, 1, nextFormVersion,
            formStatus, valuesJson, contentHash, publicUser, now, ct);

        var nextCaseVersion = link.CaseVersion + 1;
        var summaryJson = JsonSerializer.Serialize(BuildSummaryData(values), JsonOptions);
        await using (var updateCase = new NpgsqlCommand("""
            UPDATE witness.cases
            SET summary_data=$2::jsonb,
                status=CASE WHEN $3 THEN 'staff_review' ELSE status END,
                current_owner_role=CASE WHEN $3 THEN 'officer' ELSE current_owner_role END,
                current_owner_user_id=CASE WHEN $3 THEN $4 ELSE current_owner_user_id END,
                current_owner_name=CASE WHEN $3 THEN $5 ELSE current_owner_name END,
                row_version=$6, updated_at=$7
            WHERE id=$1
            """, connection, tx))
        {
            updateCase.Parameters.AddWithValue(link.CaseId);
            updateCase.Parameters.AddWithValue(summaryJson);
            updateCase.Parameters.AddWithValue(request.Complete);
            updateCase.Parameters.AddWithValue(link.CreatedBy);
            updateCase.Parameters.AddWithValue(link.CreatedByName);
            updateCase.Parameters.AddWithValue(nextCaseVersion);
            updateCase.Parameters.AddWithValue(now);
            await updateCase.ExecuteNonQueryAsync(ct);
        }

        if (request.Complete)
        {
            await using var submitLink = new NpgsqlCommand("""
                UPDATE witness.kb1_share_links
                SET status='submitted', submitted_at=$2, row_version=row_version+1
                WHERE id=$1
                """, connection, tx);
            submitLink.Parameters.AddWithValue(link.Id);
            submitLink.Parameters.AddWithValue(now);
            await submitLink.ExecuteNonQueryAsync(ct);
            await InsertWorkflowEventAsync(connection, tx, link.CaseId, "public-kb1-submitted",
                WitnessStatuses.IntakeDraft, WitnessStatuses.StaffReview, publicUser,
                "ผู้ยื่นยืนยันข้อมูลและส่งแบบ คบ.1 ให้เจ้าหน้าที่ตรวจสอบ", null,
                request.IdempotencyKey, "{}", now, ct);
        }
        await InsertAuditAsync(connection, tx, link.CaseId,
            request.Complete ? "kb1.public.submitted" : "kb1.public.draft.saved",
            "form", link.FormId.ToString(), publicUser, ipAddress,
            JsonSerializer.Serialize(new { formVersion = nextFormVersion, confirmed = request.ConfirmAccuracy }), now, ct);

        var attachments = await ListPublicKb1AttachmentsAsync(connection, tx, link.Id, ct);
        var result = new WitnessPublicKb1DraftDto(link.RequestNo,
            request.Complete ? "submitted" : "active", link.ExpiresAt,
            nextFormVersion, nextCaseVersion,
            request.Complete ? new Dictionary<string, string>() : PublicKb1Values(values), attachments);
        await CompleteIdempotencyAsync(connection, tx, idempotency, link.CaseId,
            StatusCodes.Status200OK, result, now, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<WitnessAttachmentDto> AddPublicKb1AttachmentAsync(
        string accessToken,
        string idempotencyKey,
        string fileName,
        string contentType,
        byte[] content,
        string ipAddress,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var link = await LockKb1ShareLinkAsync(connection, tx, accessToken, ct);
        EnsureKb1ShareWritable(link, DateTimeOffset.UtcNow);
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var requestHash = ComputeIdempotencyHash(new { fileName, contentType, hash, size = content.LongLength });
        var idempotency = await ReserveIdempotencyAsync(connection, tx, link.Id,
            $"witness:public-kb1-attachment:{link.CaseId:D}", "upload", idempotencyKey,
            requestHash, link.CaseId, ct);
        if (idempotency?.IsReplay == true)
        {
            var replay = DeserializeIdempotentResponse<WitnessAttachmentDto>(idempotency);
            await tx.CommitAsync(ct);
            return replay;
        }
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var publicUser = PublicKb1User(link);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO witness.attachments(
                id, case_id, form_number, form_version, file_name, content_type,
                size_bytes, sha256, classification, content, uploaded_by,
                uploaded_by_name, uploaded_at, kb1_share_link_id)
            VALUES($1,$2,1,$3,$4,$5,$6,$7,'ลับ',$8,$9,$10,$11,$12)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(link.CaseId);
            cmd.Parameters.AddWithValue(link.FormVersion);
            cmd.Parameters.AddWithValue(fileName);
            cmd.Parameters.AddWithValue(contentType);
            cmd.Parameters.AddWithValue(content.LongLength);
            cmd.Parameters.AddWithValue(hash);
            cmd.Parameters.AddWithValue(content);
            cmd.Parameters.AddWithValue(publicUser.UserId);
            cmd.Parameters.AddWithValue(publicUser.DisplayName);
            cmd.Parameters.AddWithValue(now);
            cmd.Parameters.AddWithValue(link.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var result = new WitnessAttachmentDto(id, fileName, contentType, content.LongLength, hash,
            "ลับ", 1, link.FormVersion, now, publicUser.DisplayName);
        await InsertAuditAsync(connection, tx, link.CaseId, "kb1.public.attachment.uploaded",
            "attachment", id.ToString(), publicUser, ipAddress,
            JsonSerializer.Serialize(new { shareLinkId = link.Id, sha256 = hash, size = content.LongLength }), now, ct);
        // idempotency_records.resource_id is the owning case (FK to witness.cases),
        // while the replay payload retains the concrete attachment id.
        await CompleteIdempotencyAsync(connection, tx, idempotency, link.CaseId,
            StatusCodes.Status200OK, result, now, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<AttachmentContent?> GetPublicKb1AttachmentContentAsync(
        string accessToken,
        Guid attachmentId,
        string ipAddress,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var link = await LockKb1ShareLinkAsync(connection, tx, accessToken, ct);
        EnsureKb1ShareReadable(link, DateTimeOffset.UtcNow);
        await using var cmd = new NpgsqlCommand("""
            SELECT file_name, content_type, content
            FROM witness.attachments
            WHERE id=$1 AND case_id=$2 AND kb1_share_link_id=$3 AND deleted_at IS NULL
            """, connection, tx);
        cmd.Parameters.AddWithValue(attachmentId);
        cmd.Parameters.AddWithValue(link.CaseId);
        cmd.Parameters.AddWithValue(link.Id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await tx.CommitAsync(ct);
            return null;
        }
        var result = new AttachmentContent(reader.GetString(0), reader.GetString(1), (byte[])reader[2]);
        await reader.CloseAsync();
        await InsertAuditAsync(connection, tx, link.CaseId, "kb1.public.attachment.downloaded",
            "attachment", attachmentId.ToString(), PublicKb1User(link), ipAddress,
            JsonSerializer.Serialize(new { shareLinkId = link.Id }), DateTimeOffset.UtcNow, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task DeletePublicKb1AttachmentAsync(
        string accessToken,
        Guid attachmentId,
        string ipAddress,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var link = await LockKb1ShareLinkAsync(connection, tx, accessToken, ct);
        EnsureKb1ShareWritable(link, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var publicUser = PublicKb1User(link);
        await using var cmd = new NpgsqlCommand("""
            UPDATE witness.attachments
            SET deleted_at=$4, deleted_by=$5, deleted_reason='ผู้ยื่นลบก่อนส่งแบบ คบ.1'
            WHERE id=$1 AND case_id=$2 AND kb1_share_link_id=$3 AND deleted_at IS NULL
            """, connection, tx);
        cmd.Parameters.AddWithValue(attachmentId);
        cmd.Parameters.AddWithValue(link.CaseId);
        cmd.Parameters.AddWithValue(link.Id);
        cmd.Parameters.AddWithValue(now);
        cmd.Parameters.AddWithValue(publicUser.UserId);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new WitnessWorkflowException("ไม่พบเอกสารแนบที่ต้องการลบ");
        await InsertAuditAsync(connection, tx, link.CaseId, "kb1.public.attachment.deleted",
            "attachment", attachmentId.ToString(), publicUser, ipAddress,
            JsonSerializer.Serialize(new { shareLinkId = link.Id }), now, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<WitnessFormDto?> GetFormAsync(
        Guid caseId,
        int formNumber,
        WitnessUserContext user,
        CancellationToken ct)
    {
        EnsureView(user);
        if (await GetSummaryAsync(caseId, user, ct) is null)
            return null;

        await using var cmd = dataSource.CreateCommand("""
            SELECT f.id, f.case_id, c.request_no, f.form_number, f.version, f.status,
                   f.values_data, f.updated_at, f.updated_by_name, c.row_version
            FROM witness.forms f
            JOIN witness.cases c ON c.id = f.case_id
            WHERE f.case_id = $1 AND f.form_number = $2
            """);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(formNumber);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var form = new FormRow(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetString(5), ReadDictionary(reader.GetString(6)),
            reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetInt64(9));
        await reader.CloseAsync();
        var signatures = await ListSignaturesAsync(form.Id, ct);
        var opinions = await ListOpinionsAsync(form.Id, ct);
        var safeHouseVisible = await CanViewSafeHouseAsync(caseId, user, ct);
        return new WitnessFormDto(form.Id, form.CaseId, form.RequestNo, form.FormNumber,
            form.Version, form.Status, MaskFormValues(form.FormNumber, form.Values, user, safeHouseVisible), signatures,
            form.UpdatedAt, form.UpdatedBy, form.CaseVersion, opinions);
    }

    public async Task<WitnessFormDto?> GetFormAsync(
        Guid caseId,
        int formNumber,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        var result = await GetFormAsync(caseId, formNumber, user, ct);
        if (result is not null)
            await RecordSecretAccessAsync(caseId, "form.secret.viewed", "form", result.Id.ToString(), user, ipAddress, ct);
        return result;
    }

    public Task RecordDocumentDownloadAsync(
        Guid caseId,
        Guid formId,
        int formNumber,
        int formVersion,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
        => RecordSecretAccessAsync(
            caseId,
            "form.document.downloaded",
            "form",
            formId.ToString(),
            user,
            ipAddress,
            ct,
            JsonSerializer.Serialize(new { formNumber, formVersion }));

    public async Task<WitnessFormDto> SaveFormAsync(
        Guid caseId,
        int formNumber,
        SaveWitnessFormRequest request,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        formPolicy.EnsureCanEdit(formNumber, user);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        EnsureVersion(caseRow.Version, request.ExpectedCaseVersion);
        WitnessFormStagePolicy.EnsureCanSave(formNumber, caseRow.Status, caseRow.UrgentStatus);
        var existing = await LockFormAsync(connection, tx, caseId, formNumber, ct);
        if (existing is not null && existing.Version != request.ExpectedFormVersion)
            throw new WitnessConcurrencyException("แบบฟอร์มถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลล่าสุด");
        if (existing is null && request.ExpectedFormVersion != 0)
            throw new WitnessConcurrencyException("ไม่พบรุ่นฟอร์มที่ต้องการแก้ไข กรุณาโหลดข้อมูลล่าสุด");

        var authorizedValues = formPolicy.AuthorizeAndMergeValues(
            formNumber,
            request.Values,
            existing?.Values,
            user,
            caseRow.Status,
            caseRow.UrgentStatus);
        formPolicy.Validate(formNumber, authorizedValues, request.Complete);

        var formId = existing?.Id ?? Guid.NewGuid();
        var nextVersion = (existing?.Version ?? 0) + 1;
        var status = request.Complete ? "completed" : "draft";
        var values = new Dictionary<string, string>(authorizedValues, StringComparer.OrdinalIgnoreCase)
        {
            ["request_no"] = caseRow.RequestNo
        };
        var valuesJson = JsonSerializer.Serialize(values, JsonOptions);
        var hash = WitnessFormPolicy.ComputeContentHash(values);
        var now = DateTimeOffset.UtcNow;

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO witness.forms(id, case_id, form_number, version, status, values_data,
                                      updated_by, updated_by_name, updated_at)
            VALUES($1, $2, $3, $4, $5, $6::jsonb, $7, $8, $9)
            ON CONFLICT(case_id, form_number) DO UPDATE
            SET version = EXCLUDED.version,
                status = EXCLUDED.status,
                values_data = EXCLUDED.values_data,
                updated_by = EXCLUDED.updated_by,
                updated_by_name = EXCLUDED.updated_by_name,
                updated_at = EXCLUDED.updated_at
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(formId);
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(formNumber);
            cmd.Parameters.AddWithValue(nextVersion);
            cmd.Parameters.AddWithValue(status);
            cmd.Parameters.AddWithValue(valuesJson);
            cmd.Parameters.AddWithValue(user.UserId);
            cmd.Parameters.AddWithValue(user.DisplayName);
            cmd.Parameters.AddWithValue(now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await InsertFormVersionAsync(connection, tx, formId, caseId, formNumber, nextVersion,
            status, valuesJson, hash, user, now, ct);
        await IncrementCaseVersionAsync(connection, tx, caseId, now, ct);
        await InsertAuditAsync(connection, tx, caseId, request.Complete ? "form.completed" : "form.draft.saved",
            "form", formId.ToString(), user, ipAddress,
            JsonSerializer.Serialize(new { formNumber, version = nextVersion, status }), now, ct);
        await tx.CommitAsync(ct);

        return new WitnessFormDto(
            formId,
            caseId,
            caseRow.RequestNo,
            formNumber,
            nextVersion,
            status,
            values,
            [],
            now,
            user.DisplayName,
            caseRow.Version + 1,
            []);
    }

    public async Task<WitnessFormDto> SignFormAsync(
        Guid caseId,
        int formNumber,
        SignWitnessFormRequest request,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        formPolicy.EnsureCanSign(formNumber, user);
        var allowedPurposes = WitnessProtectionFormCatalog.SignaturePurposes(formNumber);
        if (string.IsNullOrWhiteSpace(request.Purpose) || !allowedPurposes.Contains(request.Purpose, StringComparer.Ordinal))
            throw new WitnessWorkflowException("กรุณาเลือกหน้าที่ของผู้ลงนามให้ตรงตามแบบทางการ");
        formPolicy.EnsureCanSignPurpose(request.Purpose, user);
        if (string.IsNullOrWhiteSpace(request.Position))
            throw new WitnessWorkflowException("กรุณาระบุตำแหน่งผู้ลงนาม");
        if (string.IsNullOrWhiteSpace(request.VerificationMethod)
            || string.IsNullOrWhiteSpace(request.EvidenceReference))
            throw new WitnessWorkflowException("กรุณาระบุวิธีและหลักฐานยืนยันลายมือชื่ออิเล็กทรอนิกส์");
        if (WitnessOpinionPolicy.RequiresOpinion(formNumber, request.Purpose)
            && string.IsNullOrWhiteSpace(request.OpinionText))
            throw new WitnessWorkflowException("กรุณาบันทึกความเห็นประกอบก่อนลงลายมือชื่อในลำดับนี้");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        WitnessSignaturePolicy.EnsurePurposeMatchesWorkflowStage(
            formNumber, caseRow.Status, caseRow.UrgentStatus, request.Purpose);
        EnsureVersion(caseRow.Version, request.ExpectedCaseVersion);
        var form = await LockFormAsync(connection, tx, caseId, formNumber, ct)
                   ?? throw new WitnessWorkflowException("ไม่พบแบบฟอร์มสำหรับลงนาม");
        if (form.Version != request.ExpectedFormVersion)
            throw new WitnessConcurrencyException("แบบฟอร์มมีรุ่นใหม่กว่า กรุณาโหลดข้อมูลล่าสุดก่อนลงนาม");
        if (form.Status == "draft")
            throw new WitnessWorkflowException("ต้องบันทึกแบบฟอร์มฉบับสมบูรณ์ก่อนลงนาม");
        formPolicy.ValidatePersistentInvariants(formNumber, form.Values);

        var signerRole = PrimaryRole(user);
        var authority = await ResolveActingAuthorityAsync(connection, tx, user, signerRole, ct);
        var now = DateTimeOffset.UtcNow;
        var signatureId = Guid.NewGuid();
        Guid? opinionId = null;
        var documentHash = WitnessFormPolicy.ComputeContentHash(form.Values);
        await using (var duplicatePurpose = new NpgsqlCommand("""
            SELECT EXISTS(
                SELECT 1 FROM witness.form_signatures
                WHERE form_id=$1 AND form_version=$2 AND signer_purpose=$3)
            """, connection, tx))
        {
            duplicatePurpose.Parameters.AddWithValue(form.Id);
            duplicatePurpose.Parameters.AddWithValue(form.Version);
            duplicatePurpose.Parameters.AddWithValue(request.Purpose);
            if (await duplicatePurpose.ExecuteScalarAsync(ct) is true)
                throw new WitnessWorkflowException($"{request.Purpose} ลงนามเอกสารรุ่นนี้แล้ว");
        }
        var prerequisitePurposes = WitnessSignaturePolicy.PrerequisitePurposes(formNumber, request.Purpose);
        if (prerequisitePurposes.Count > 0)
        {
            var currentPurposes = new HashSet<string>(StringComparer.Ordinal);
            await using var prerequisiteCmd = new NpgsqlCommand("""
                SELECT signer_purpose
                FROM witness.form_signatures
                WHERE form_id=$1 AND form_version=$2
                """, connection, tx);
            prerequisiteCmd.Parameters.AddWithValue(form.Id);
            prerequisiteCmd.Parameters.AddWithValue(form.Version);
            await using var prerequisiteReader = await prerequisiteCmd.ExecuteReaderAsync(ct);
            while (await prerequisiteReader.ReadAsync(ct))
                currentPurposes.Add(prerequisiteReader.GetString(0));
            var missingPrerequisites = prerequisitePurposes.Where(purpose => !currentPurposes.Contains(purpose)).ToArray();
            if (missingPrerequisites.Length > 0)
                throw new WitnessWorkflowException($"ต้องลงนามตามลำดับก่อน: {string.Join(", ", missingPrerequisites)}");
        }
        if (WitnessOpinionPolicy.RequiresOpinion(formNumber, request.Purpose))
        {
            opinionId = Guid.NewGuid();
            await using var opinionCmd = new NpgsqlCommand("""
                INSERT INTO witness.form_opinions(
                    id, form_id, case_id, form_number, form_version, opinion_purpose,
                    opinion_text, actor_user_id, actor_name, actor_position, actor_role, created_at)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
                """, connection, tx);
            opinionCmd.Parameters.AddWithValue(opinionId.Value);
            opinionCmd.Parameters.AddWithValue(form.Id);
            opinionCmd.Parameters.AddWithValue(caseId);
            opinionCmd.Parameters.AddWithValue(formNumber);
            opinionCmd.Parameters.AddWithValue(form.Version);
            opinionCmd.Parameters.AddWithValue(request.Purpose.Trim());
            opinionCmd.Parameters.AddWithValue(request.OpinionText!.Trim());
            opinionCmd.Parameters.AddWithValue(user.UserId);
            opinionCmd.Parameters.AddWithValue(user.DisplayName);
            opinionCmd.Parameters.AddWithValue(request.Position.Trim());
            opinionCmd.Parameters.AddWithValue(signerRole);
            opinionCmd.Parameters.AddWithValue(now);
            await opinionCmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO witness.form_signatures(
                id, form_id, form_version, signer_user_id, signer_name, signer_position,
                signer_role, signer_purpose, verification_method, evidence_reference, document_hash,
                delegation_reference, signed_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(signatureId);
            cmd.Parameters.AddWithValue(form.Id);
            cmd.Parameters.AddWithValue(form.Version);
            cmd.Parameters.AddWithValue(user.UserId);
            cmd.Parameters.AddWithValue(user.DisplayName);
            cmd.Parameters.AddWithValue(request.Position.Trim());
            cmd.Parameters.AddWithValue(signerRole);
            cmd.Parameters.AddWithValue(request.Purpose.Trim());
            cmd.Parameters.AddWithValue(request.VerificationMethod.Trim());
            cmd.Parameters.AddWithValue(request.EvidenceReference.Trim());
            cmd.Parameters.AddWithValue(documentHash);
            cmd.Parameters.AddWithValue((object?)authority?.DelegationReference ?? DBNull.Value);
            cmd.Parameters.AddWithValue(now);
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new WitnessWorkflowException($"{request.Purpose} ลงนามเอกสารรุ่นนี้แล้ว");
            }
        }

        var signedPurposes = new HashSet<string>(StringComparer.Ordinal);
        await using (var signedPurposeCmd = new NpgsqlCommand("""
            SELECT signer_purpose FROM witness.form_signatures
            WHERE form_id=$1 AND form_version=$2
            """, connection, tx))
        {
            signedPurposeCmd.Parameters.AddWithValue(form.Id);
            signedPurposeCmd.Parameters.AddWithValue(form.Version);
            await using var signedPurposeReader = await signedPurposeCmd.ExecuteReaderAsync(ct);
            while (await signedPurposeReader.ReadAsync(ct))
                signedPurposes.Add(signedPurposeReader.GetString(0));
        }
        var signatureStatus = allowedPurposes.All(signedPurposes.Contains) ? "signed" : "completed";
        await using (var cmd = new NpgsqlCommand("UPDATE witness.forms SET status=$2, updated_at=$3 WHERE id=$1", connection, tx))
        {
            cmd.Parameters.AddWithValue(form.Id);
            cmd.Parameters.AddWithValue(signatureStatus);
            cmd.Parameters.AddWithValue(now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await IncrementCaseVersionAsync(connection, tx, caseId, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "form.signed", "signature", signatureId.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new
            {
                formNumber,
                formVersion = form.Version,
                signerRole,
                request.VerificationMethod,
                authority?.DelegationReference,
                opinionId
            }), now, ct);
        await tx.CommitAsync(ct);

        return await GetFormAsync(caseId, formNumber, user, ct)
               ?? throw new WitnessWorkflowException("ลงนามแล้วแต่ไม่สามารถอ่านข้อมูลกลับได้");
    }

    public async Task<WitnessCommandResultDto> ExecuteCommandAsync(
        Guid caseId,
        string action,
        ExecuteWitnessCommandRequest request,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (action.StartsWith("urgent-", StringComparison.OrdinalIgnoreCase))
            return await ExecuteUrgentCommandAsync(caseId, action, request, user, ipAddress, ct);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var normalizedAction = NormalizeIdempotencyOperation(action);
        var requestHash = ComputeIdempotencyHash(new
        {
            Action = normalizedAction,
            request.ExpectedVersion,
            Reason = request.Reason?.Trim() ?? "",
            request.Data
        });
        var idempotency = await ReserveIdempotencyAsync(
            connection, tx, user.UserId, CaseIdempotencyScope(caseId), normalizedAction,
            request.IdempotencyKey, requestHash, caseId, ct);
        if (idempotency?.IsReplay == true)
        {
            var replayCase = await LockCaseAsync(connection, tx, caseId, ct);
            await EnsureCaseMutationAccessAsync(connection, tx, replayCase, user, ct);
            var replay = DeserializeIdempotentResponse<WitnessCommandResultDto>(idempotency);
            await tx.CommitAsync(ct);
            return replay;
        }
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        EnsureVersion(caseRow.Version, request.ExpectedVersion);
        var now = DateTimeOffset.UtcNow;
        await ValidateStoredFormInvariantsAsync(connection, tx, caseId, ct);
        var completedForms = await GetCompletedFormNumbersAsync(connection, tx, caseId, ct);
        if (action == "submit-supervisor" && !await HasMainCaseLinkAsync(connection, tx, caseId, ct))
            throw new WitnessWorkflowException("กรุณาเชื่อมโยงคดีหลัก หรือบันทึกว่าเป็นคดีใหม่ พร้อมประเมินภัยก่อนส่งผู้บังคับบัญชาชั้นต้น");
        if (action is "request-withdrawal" or "withdrawal-submit-supervisor" or "withdrawal-supervisor-forward" or "withdrawal-director-forward")
            await EnsureWithdrawalStatementAsync(connection, tx, caseId, ct);
        if (action == "close-no-appeal" && caseRow.Status == WitnessStatuses.AppealWindow)
            await EnsureAppealDeadlineElapsedAsync(connection, tx, caseId, ct);
        if (action == "request-transfer")
            await EnsureTransferEligibilityAsync(connection, tx, caseId, request.Data, ct);
        var appealResultContext = await PrepareAppealResultLifecycleCommandAsync(
            connection, tx, caseRow, action, request, user, now, ct);
        var definition = stateMachine.RequireTransition(caseRow.Status, action, user, completedForms);
        await EnsureRequiredSignaturesAsync(connection, tx, caseId, action, caseRow.Status, ct);
        if (definition.RequiresReason && string.IsNullOrWhiteSpace(request.Reason))
            throw new WitnessWorkflowException("กรุณาระบุเหตุผลประกอบการดำเนินการ");

        var nextVersion = caseRow.Version + 1;
        var toStatus = action == "complete-appeal-result-notification"
            ? appealResultContext?.CompletionStatus
              ?? throw new WitnessWorkflowException("ไม่พบสถานะปลายทางของผลอุทธรณ์")
            : definition.ToStatus;
        var ownerRole = OwnerForStatus(toStatus);
        var receivedAt = ParseNoticeReceivedAt(action, request.Data, now)?.ToUniversalTime();
        NoticeReceiptTarget? noticeReceiptTarget = null;
        if (receivedAt.HasValue)
        {
            noticeReceiptTarget = await LockAndValidateLatestNoticeDeliveryAsync(
                connection, tx, caseId, action, receivedAt.Value, ct);
        }
        DateOnly? appealDeadline = action == "record-notice-receipt-rejected" && receivedAt.HasValue
            ? DateOnly.FromDateTime(receivedAt.Value.ToOffset(TimeSpan.FromHours(7)).Date).AddDays(30)
            : null;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE witness.cases
            SET status=$2, current_owner_role=$3, current_owner_user_id=NULL,
                current_owner_name='', row_version=$4, updated_at=$5,
                notice_received_at=COALESCE($6, notice_received_at),
                appeal_deadline=COALESCE($7, appeal_deadline),
                summary_data=CASE WHEN $8 THEN jsonb_set(summary_data, '{withdrawal_requested}', 'true'::jsonb, true) ELSE summary_data END
            WHERE id=$1
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(toStatus);
            cmd.Parameters.AddWithValue(ownerRole);
            cmd.Parameters.AddWithValue(nextVersion);
            cmd.Parameters.AddWithValue(now);
            cmd.Parameters.AddWithValue((object?)receivedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)appealDeadline ?? DBNull.Value);
            cmd.Parameters.AddWithValue(action == "request-withdrawal");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (action == "receive-appeal")
            await InsertAppealAsync(connection, tx, caseRow, request, user, now, ct);
        if (action == "submit-appeal")
        {
            await using var appealCmd = new NpgsqlCommand("""
                UPDATE witness.appeals
                SET status='submitted', row_version=row_version+1, updated_at=$2
                WHERE id=(SELECT id FROM witness.appeals WHERE case_id=$1 ORDER BY created_at DESC LIMIT 1)
                """, connection, tx);
            appealCmd.Parameters.AddWithValue(caseId);
            appealCmd.Parameters.AddWithValue(now);
            await appealCmd.ExecuteNonQueryAsync(ct);
        }
        if (action == "start-protection")
            await InsertProtectionPeriodFromFormAsync(connection, tx, caseId, 11, 1, now, ct);
        if (action == "send-notice")
            await InsertNoticeDeliveryAsync(connection, tx, caseRow, request, user, now, ct);
        if (action == "notify-appeal-result")
            await InsertAppealResultNoticeAsync(
                connection, tx, caseRow, request, appealResultContext!, user, now, ct);
        if (action == "record-appeal-result-receipt")
            await RecordAppealResultReceiptAsync(
                connection, tx, request, appealResultContext!, user, now, ct);
        if (action == "complete-appeal-result-notification")
            await CompleteAppealResultNoticeAsync(
                connection, tx, appealResultContext!, user, now, ct);
        if (receivedAt.HasValue && noticeReceiptTarget is not null)
        {
            await RecordNoticeReceiptAsync(
                connection, tx, caseId, noticeReceiptTarget.DeliveryId,
                receivedAt.Value, request.Data, ct);
        }
        var dataJson = JsonSerializer.Serialize(request.Data ?? [], JsonOptions);
        await InsertWorkflowEventAsync(connection, tx, caseId, action, caseRow.Status,
            toStatus, user, request.Reason ?? "", appealResultContext?.ExternalReference,
            request.IdempotencyKey, dataJson, now, ct);
        var auditAction = action switch
        {
            "notify-appeal-result" => "appeal.result.notice.sent",
            "record-appeal-result-receipt" => "appeal.result.notice.received",
            "complete-appeal-result-notification" => "appeal.result.notice.completed",
            _ => "workflow.transition"
        };
        var auditEntityType = appealResultContext is null ? "case" : "appeal_result_notice";
        var auditEntityId = appealResultContext?.Notice?.Id.ToString()
                            ?? appealResultContext?.Appeal.Id.ToString()
                            ?? caseId.ToString();
        await InsertAuditAsync(connection, tx, caseId, auditAction, auditEntityType, auditEntityId,
            user, ipAddress, JsonSerializer.Serialize(new
            {
                action,
                from = caseRow.Status,
                to = toStatus,
                request.Reason,
                request.Data,
                appealId = appealResultContext?.Appeal.Id,
                appealResultNoticeId = appealResultContext?.Notice?.Id
            }), now, ct);
        var available = stateMachine.GetAvailableActions(toStatus, caseRow.UrgentStatus, user, completedForms);
        if (action == "complete-appeal-result-notification")
            available = available.Where(item => item.Code != "notify-appeal-result").ToArray();
        if (action == "send-notice")
        {
            var currentNoticeForm = caseRow.Status switch
            {
                WitnessStatuses.ApprovedPendingNotice => 9,
                WitnessStatuses.RejectedPendingNotice => 10,
                WitnessStatuses.TerminationOrdered => 17,
                _ => (int?)null
            };
            available = FilterNoticeReceiptActions(available, currentNoticeForm);
        }
        var result = new WitnessCommandResultDto(caseId, caseRow.RequestNo, caseRow.Status,
            toStatus, nextVersion, available);
        await CompleteIdempotencyAsync(
            connection, tx, idempotency, caseId, StatusCodes.Status200OK, result, now, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<WitnessCommandResultDto> ReceiveExternalResultAsync(
        Guid caseId,
        ReceiveExternalResultRequest request,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (!user.HasPermission(WitnessPermissions.ExternalReceive))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์รับผลจาก External Module");
        if (string.IsNullOrWhiteSpace(request.ReferenceNo) || string.IsNullOrWhiteSpace(request.Reason))
            throw new WitnessWorkflowException("กรุณาระบุเลขอ้างอิงและเหตุผลของผลคำสั่ง");

        var normalizedResultType = request.ResultType.Trim().ToLowerInvariant();
        var requestHash = ComputeIdempotencyHash(new
        {
            ResultType = normalizedResultType,
            ReferenceNo = request.ReferenceNo.Trim(),
            DecisionAt = request.DecisionAt.ToUniversalTime(),
            Reason = request.Reason.Trim(),
            request.ExpectedVersion,
            request.Data
        });

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var idempotency = await ReserveIdempotencyAsync(
            connection, tx, user.UserId, CaseIdempotencyScope(caseId), "external-result",
            request.IdempotencyKey, requestHash, caseId, ct);
        if (idempotency?.IsReplay == true)
        {
            _ = await LockCaseAsync(connection, tx, caseId, ct);
            var replay = DeserializeIdempotentResponse<WitnessCommandResultDto>(idempotency);
            await tx.CommitAsync(ct);
            return replay;
        }

        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        EnsureVersion(caseRow.Version, request.ExpectedVersion);
        await ValidateStoredFormInvariantsAsync(connection, tx, caseId, ct);
        await EnsureRequiredSignaturesAsync(connection, tx, caseId, "external-result", caseRow.Status, ct);
        if (caseRow.Status == WitnessStatuses.ExternalPending
            && string.Equals(request.ResultType, "approved", StringComparison.OrdinalIgnoreCase))
            await EnsureRequiredSignaturesAsync(connection, tx, caseId, "external-approved", caseRow.Status, ct);
        AppealRow? currentAppeal = null;
        if (caseRow.Status == WitnessStatuses.AppealExternalPending)
            currentAppeal = await LockCurrentAppealAsync(connection, tx, caseId, ct);
        if (request.Data?.TryGetValue("external_attachment_id", out var externalAttachmentValue) != true
            || !Guid.TryParse(externalAttachmentValue, out var externalAttachmentId)
            || !await AttachmentExistsAsync(connection, tx, caseId, externalAttachmentId, ct)
            || (currentAppeal is not null
                && !await AppealAttachmentExistsAsync(
                    connection, tx, caseId, currentAppeal.Id, externalAttachmentId,
                    AppealExternalResult, ct)))
            throw new WitnessWorkflowException("กรุณาแนบไฟล์ผลคำสั่งหรือหนังสือจาก External Module");
        var toStatus = ResolveExternalTarget(caseRow.Status, normalizedResultType);
        if (caseRow.Status == WitnessStatuses.AppealExternalPending
            && normalizedResultType is "appeal-reversed" or "appeal-overturned")
        {
            var appealedNoticeForm = await GetLatestNoticeFormNumberAsync(connection, tx, caseId, ct);
            toStatus = appealedNoticeForm switch
            {
                10 => WitnessStatuses.ApprovedPendingNotice,
                17 => WitnessStatuses.ProtectionActive,
                _ => throw new WitnessWorkflowException("ไม่พบคำสั่งเดิมที่ถูกอุทธรณ์")
            };
        }
        var now = DateTimeOffset.UtcNow;
        if (request.DecisionAt.ToUniversalTime() > now)
            throw new WitnessWorkflowException("วันที่คำสั่งจาก External Module ต้องไม่เป็นเวลาในอนาคต");
        var nextVersion = caseRow.Version + 1;
        var payloadJson = JsonSerializer.Serialize(request.Data ?? [], JsonOptions);

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO witness.external_results(
                id, case_id, result_type, reference_no, decision_at, reason,
                payload, received_by, received_by_name, received_at)
            VALUES($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$9,$10)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(normalizedResultType);
            cmd.Parameters.AddWithValue(request.ReferenceNo.Trim());
            cmd.Parameters.AddWithValue(request.DecisionAt.ToUniversalTime());
            cmd.Parameters.AddWithValue(request.Reason.Trim());
            cmd.Parameters.AddWithValue(payloadJson);
            cmd.Parameters.AddWithValue(user.UserId);
            cmd.Parameters.AddWithValue(user.DisplayName);
            cmd.Parameters.AddWithValue(now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand("""
            UPDATE witness.cases
            SET status=$2, current_owner_role=$3, current_owner_user_id=NULL,
                current_owner_name='', row_version=$4, updated_at=$5
            WHERE id=$1
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(toStatus);
            cmd.Parameters.AddWithValue(OwnerForStatus(toStatus));
            cmd.Parameters.AddWithValue(nextVersion);
            cmd.Parameters.AddWithValue(now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (caseRow.Status == WitnessStatuses.ExtensionExternalPending
            && string.Equals(request.ResultType, "extension-approved", StringComparison.OrdinalIgnoreCase))
        {
            await InsertProtectionPeriodFromFormAsync(connection, tx, caseId, 14, 0, now, ct);
        }
        if (currentAppeal is not null)
        {
            var isReturnForRevision = normalizedResultType == "return-for-revision";
            await using var appealCmd = new NpgsqlCommand("""
                UPDATE witness.appeals
                SET status=$2,
                    external_reference=$3,
                    decision=$4,
                    decided_at=$5,
                    row_version=row_version+1,
                    updated_at=$6
                WHERE id=$1
                """, connection, tx);
            appealCmd.Parameters.AddWithValue(currentAppeal.Id);
            appealCmd.Parameters.AddWithValue(isReturnForRevision ? "received" : "decided");
            appealCmd.Parameters.AddWithValue(request.ReferenceNo.Trim());
            appealCmd.Parameters.AddWithValue((object?)(isReturnForRevision ? null : normalizedResultType) ?? DBNull.Value);
            appealCmd.Parameters.Add(new NpgsqlParameter
            {
                Value = isReturnForRevision ? DBNull.Value : request.DecisionAt.ToUniversalTime(),
                NpgsqlDbType = NpgsqlDbType.TimestampTz
            });
            appealCmd.Parameters.AddWithValue(now);
            await appealCmd.ExecuteNonQueryAsync(ct);
        }
        await InsertWorkflowEventAsync(connection, tx, caseId, "external-result", caseRow.Status,
            toStatus, user, request.Reason, request.ReferenceNo, request.IdempotencyKey, payloadJson, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "external.result.received", "case", caseId.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new
            {
                request.ResultType,
                request.ReferenceNo,
                request.DecisionAt,
                from = caseRow.Status,
                to = toStatus
            }), now, ct);
        var availableAfterExternalResult = stateMachine.GetAvailableActions(toStatus, caseRow.UrgentStatus, user);
        if (currentAppeal is not null && normalizedResultType != "return-for-revision")
            availableAfterExternalResult = availableAfterExternalResult
                .Where(action => action.Code == "notify-appeal-result")
                .ToArray();
        var result = new WitnessCommandResultDto(caseId, caseRow.RequestNo, caseRow.Status, toStatus,
            nextVersion, availableAfterExternalResult);
        await CompleteIdempotencyAsync(
            connection, tx, idempotency, caseId, StatusCodes.Status200OK, result, now, ct);
        await tx.CommitAsync(ct);

        return result;
    }

    public async Task<WitnessAttachmentDto> AddAttachmentAsync(
        Guid caseId,
        int? formNumber,
        int? formVersion,
        string fileName,
        string contentType,
        byte[] content,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
        => await AddAttachmentAsync(
            caseId, formNumber, formVersion, appealId: null, evidenceType: null,
            idempotencyKey: null, fileName, contentType, content, user, ipAddress, ct);

    public async Task<WitnessAttachmentDto> AddAttachmentAsync(
        Guid caseId,
        int? formNumber,
        int? formVersion,
        Guid? appealId,
        string? evidenceType,
        string? idempotencyKey,
        string fileName,
        string contentType,
        byte[] content,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var normalizedEvidenceType = NormalizeEvidenceType(evidenceType);
        if (appealId.HasValue)
            EnsureAppealAttachmentWritePermission(user, normalizedEvidenceType);
        else if (!CanManageAttachments(user))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์แนบเอกสารในแฟ้มนี้");
        if (!appealId.HasValue && normalizedEvidenceType is not null)
            throw new WitnessWorkflowException("ประเภทหลักฐานอุทธรณ์ต้องระบุคำอุทธรณ์ที่เกี่ยวข้อง");

        var requestHash = ComputeIdempotencyHash(new
        {
            CaseId = caseId,
            formNumber,
            formVersion,
            AppealId = appealId,
            EvidenceType = normalizedEvidenceType,
            FileName = fileName,
            ContentType = contentType,
            Size = content.LongLength,
            Sha256 = hash
        });
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var idempotency = await ReserveIdempotencyAsync(
            connection, tx, user.UserId, CaseIdempotencyScope(caseId), "attachment-upload",
            idempotencyKey, requestHash, caseId, ct);
        if (idempotency?.IsReplay == true)
        {
            _ = await LockCaseAsync(connection, tx, caseId, ct);
            var replay = DeserializeIdempotentResponse<WitnessAttachmentDto>(idempotency);
            await tx.CommitAsync(ct);
            return replay;
        }

        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        if (appealId.HasValue)
        {
            var appeal = await LockAppealAsync(connection, tx, caseId, appealId.Value, ct);
            var currentAppeal = await LockCurrentAppealAsync(connection, tx, caseId, ct);
            if (currentAppeal.Id != appeal.Id)
                throw new WitnessWorkflowException("เพิ่มหลักฐานได้เฉพาะคำอุทธรณ์รอบปัจจุบัน");
            ValidateAppealAttachmentState(caseRow.Status, appeal.Status, normalizedEvidenceType!);
        }

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO witness.attachments(
                id, case_id, form_number, form_version, appeal_id, evidence_type,
                file_name, content_type, size_bytes, sha256, content,
                uploaded_by, uploaded_by_name, uploaded_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue((object?)formNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)formVersion ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)appealId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            cmd.Parameters.AddWithValue((object?)normalizedEvidenceType ?? DBNull.Value);
            cmd.Parameters.AddWithValue(fileName);
            cmd.Parameters.AddWithValue(contentType);
            cmd.Parameters.AddWithValue(content.LongLength);
            cmd.Parameters.AddWithValue(hash);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, content);
            cmd.Parameters.AddWithValue(user.UserId);
            cmd.Parameters.AddWithValue(user.DisplayName);
            cmd.Parameters.AddWithValue(now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await IncrementCaseVersionAsync(connection, tx, caseId, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "attachment.uploaded", "attachment", id.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new
            {
                attachmentId = id,
                appealId,
                evidenceType = normalizedEvidenceType,
                contentType,
                size = content.LongLength,
                sha256 = hash,
                formNumber,
                formVersion
            }), now, ct);
        var result = new WitnessAttachmentDto(id, fileName, contentType, content.LongLength, hash,
            "ลับ", formNumber, formVersion, now, user.DisplayName, appealId, normalizedEvidenceType);
        await CompleteIdempotencyAsync(
            connection, tx, idempotency, caseId, StatusCodes.Status200OK, result, now, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<AttachmentContent?> GetAttachmentContentAsync(
        Guid caseId,
        Guid attachmentId,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (!user.HasPermission(WitnessPermissions.DocumentDownload))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์เปิดหรือดาวน์โหลดเอกสารลับ");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT file_name, content_type, content, sha256, appeal_id, evidence_type
            FROM witness.attachments
            WHERE id=$1 AND case_id=$2 AND deleted_at IS NULL
            """, connection, tx);
        cmd.Parameters.AddWithValue(attachmentId);
        cmd.Parameters.AddWithValue(caseId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var result = new AttachmentContent(reader.GetString(0), reader.GetString(1), (byte[])reader[2]);
        var sha256 = reader.GetString(3);
        Guid? appealId = reader.IsDBNull(4) ? null : reader.GetGuid(4);
        var evidenceType = reader.IsDBNull(5) ? null : reader.GetString(5);
        await reader.CloseAsync();
        if (appealId.HasValue)
        {
            EnsureAppealReadPermission(user);
            _ = await LockAppealAsync(connection, tx, caseId, appealId.Value, ct);
        }
        await InsertAuditAsync(connection, tx, caseId, "attachment.downloaded", "attachment", attachmentId.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new
            {
                attachmentId,
                appealId,
                evidenceType,
                sha256
            }), DateTimeOffset.UtcNow, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task DeleteAttachmentAsync(
        Guid caseId,
        Guid attachmentId,
        string reason,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (!CanManageAttachments(user))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์ลบเอกสารแนบ");
        if (string.IsNullOrWhiteSpace(reason))
            throw new WitnessWorkflowException("กรุณาระบุเหตุผลการลบเอกสาร");
        var now = DateTimeOffset.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        AppealRow? appeal = null;
        string? evidenceType = null;
        string? sha256 = null;
        await using (var lookup = new NpgsqlCommand("""
            SELECT appeal_id, evidence_type, sha256
            FROM witness.attachments
            WHERE id=$1 AND case_id=$2 AND deleted_at IS NULL
            FOR UPDATE
            """, connection, tx))
        {
            lookup.Parameters.AddWithValue(attachmentId);
            lookup.Parameters.AddWithValue(caseId);
            await using var reader = await lookup.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new WitnessWorkflowException("ไม่พบเอกสารแนบที่ต้องการลบ");
            Guid? appealId = reader.IsDBNull(0) ? null : reader.GetGuid(0);
            evidenceType = reader.IsDBNull(1) ? null : reader.GetString(1);
            sha256 = reader.GetString(2);
            await reader.CloseAsync();
            if (appealId.HasValue)
            {
                EnsureAppealAttachmentWritePermission(user, evidenceType);
                appeal = await LockAppealAsync(connection, tx, caseId, appealId.Value, ct);
                var currentAppeal = await LockCurrentAppealAsync(connection, tx, caseId, ct);
                if (currentAppeal.Id != appeal.Id)
                    throw new WitnessWorkflowException("ลบหลักฐานได้เฉพาะคำอุทธรณ์รอบปัจจุบัน");
                ValidateAppealAttachmentState(caseRow.Status, appeal.Status, evidenceType!);
            }
        }
        if (evidenceType == AppealResultNoticeProof)
        {
            await using var referenced = new NpgsqlCommand("""
                SELECT EXISTS(
                    SELECT 1 FROM witness.appeal_result_notices
                    WHERE case_id=$1
                      AND (proof_attachment_id=$2 OR receipt_proof_attachment_id=$2))
                """, connection, tx);
            referenced.Parameters.AddWithValue(caseId);
            referenced.Parameters.AddWithValue(attachmentId);
            if (await referenced.ExecuteScalarAsync(ct) is true)
                throw new WitnessWorkflowException("ไม่สามารถลบหลักฐานที่ผูกกับการแจ้งผลอุทธรณ์แล้ว");
        }
        await using (var cmd = new NpgsqlCommand("""
            UPDATE witness.attachments
            SET deleted_at=$3, deleted_by=$4, deleted_reason=$5
            WHERE id=$1 AND case_id=$2 AND deleted_at IS NULL
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(attachmentId);
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(now);
            cmd.Parameters.AddWithValue(user.UserId);
            cmd.Parameters.AddWithValue(reason.Trim());
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                throw new WitnessWorkflowException("ไม่พบเอกสารแนบที่ต้องการลบ");
        }
        await IncrementCaseVersionAsync(connection, tx, caseId, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "attachment.deleted", "attachment", attachmentId.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new
            {
                attachmentId,
                appealId = appeal?.Id,
                evidenceType,
                sha256,
                reason = reason.Trim()
            }), now, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<WitnessAuditDto>> GetAuditAsync(
        Guid caseId,
        WitnessUserContext user,
        CancellationToken ct)
    {
        if (!user.HasPermission(WitnessPermissions.AuditView)
            && !user.HasPermission("witness.*"))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์ดู Audit Trail");
        if (await GetSummaryAsync(caseId, user, ct) is null)
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์ดู Audit Trail ของแฟ้มนี้");
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, action, entity_type, entity_id, actor_name, actor_role, occurred_at, details
            FROM witness.audit_events
            WHERE case_id=$1
            ORDER BY occurred_at DESC
            """);
        cmd.Parameters.AddWithValue(caseId);
        var results = new List<WitnessAuditDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new WitnessAuditDto(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6),
                JsonDocument.Parse(reader.GetString(7)).RootElement.Clone()));
        }
        return results;
    }

    private async Task<WitnessCaseSummaryDto?> GetSummaryAsync(Guid caseId, WitnessUserContext user, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, request_no, intake_form_number, status, urgent_status,
                   current_owner_role, current_owner_name, risk_level, is_urgent,
                   summary_data, row_version, created_at, updated_at, appeal_deadline,
                   (SELECT MAX(period.end_date) FROM witness.protection_periods period WHERE period.case_id=witness.cases.id),
                   (SELECT COALESCE(SUM((period.end_date-period.start_date)+1),0)::int FROM witness.protection_periods period WHERE period.case_id=witness.cases.id),
                   COALESCE(owning_org_name, '')
            FROM witness.cases
            WHERE id=$1
              AND (
                    $2
                    OR created_by=$3
                    OR current_owner_user_id=$3
                    OR EXISTS (
                        SELECT 1 FROM witness.case_assignments assignment
                        WHERE assignment.case_id=witness.cases.id
                          AND assignment.user_id=$3
                          AND assignment.ended_at IS NULL)
                    OR ($4 AND (
                        owning_org_id = $5::uuid
                        OR current_owner_org_id = $5::uuid))
                    OR ($6 AND current_owner_role = 'external_module')
                  )
            """);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(user.IsGlobalAdministrator);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(CanReviewOrganizationScope(user));
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)user.OrganizationId ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Uuid
        });
        cmd.Parameters.AddWithValue(user.HasPermission(WitnessPermissions.ExternalReceive));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSummary(reader, user) : null;
    }

    private static WitnessCaseSummaryDto ReadSummary(NpgsqlDataReader reader, WitnessUserContext user)
    {
        var summary = ReadDictionary(reader.GetString(9));
        var canViewPii = user.IsGlobalAdministrator || user.HasPermission(WitnessPermissions.ViewPii);
        var witness = summary.GetValueOrDefault("witness_name", "-");
        var petitioner = summary.GetValueOrDefault("petitioner_name", "-");
        return new WitnessCaseSummaryDto(
            reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), $"คบ.{reader.GetInt32(2)}",
            canViewPii ? witness : MaskName(witness),
            canViewPii ? petitioner : MaskName(petitioner),
            reader.GetString(3), WitnessWorkflowStateMachine.StatusLabel(reader.GetString(3)),
            reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetBoolean(8),
            reader.GetString(4), reader.GetInt64(10), reader.GetFieldValue<DateTimeOffset>(11),
            reader.GetFieldValue<DateTimeOffset>(12), WitnessWorkflowStateMachine.NextAction(reader.GetString(3)),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateOnly>(13),
            reader.IsDBNull(14) ? null : reader.GetFieldValue<DateOnly>(14),
            reader.GetInt32(15), reader.GetString(16));
    }

    private async Task<T> ExecuteReadWithRetryAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken ct)
    {
        try
        {
            return await operation();
        }
        catch (NpgsqlException ex) when (
            !ct.IsCancellationRequested
            && (ex.IsTransient || ex.InnerException is TimeoutException))
        {
            logger?.LogWarning(ex,
                "Transient PostgreSQL failure during {Operation}; clearing stale connections and retrying once",
                operationName);
            NpgsqlConnection.ClearAllPools();
            await Task.Delay(150, ct);
            return await operation();
        }
    }

    private async Task<IReadOnlyList<WitnessFormSummaryDto>> ListFormsAsync(Guid caseId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT f.id, f.form_number, f.version, f.status, f.updated_at, f.updated_by_name,
                   COUNT(s.id)::int,
                   COALESCE(
                       ARRAY_AGG(DISTINCT s.signer_purpose) FILTER (WHERE s.id IS NOT NULL),
                       ARRAY[]::text[])
            FROM witness.forms f
            LEFT JOIN witness.form_signatures s ON s.form_id=f.id AND s.form_version=f.version
            WHERE f.case_id=$1
            GROUP BY f.id
            ORDER BY f.form_number
            """);
        cmd.Parameters.AddWithValue(caseId);
        var results = new List<WitnessFormSummaryDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new WitnessFormSummaryDto(reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5), reader.GetInt32(6),
                reader.GetFieldValue<string[]>(7)));
        return results;
    }

    private async Task<IReadOnlyList<WitnessAttachmentDto>> ListAttachmentsAsync(Guid caseId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, file_name, content_type, size_bytes, sha256, classification,
                   form_number, form_version, uploaded_at, uploaded_by_name,
                   appeal_id, evidence_type
            FROM witness.attachments
            WHERE case_id=$1 AND deleted_at IS NULL
            ORDER BY uploaded_at DESC
            """);
        cmd.Parameters.AddWithValue(caseId);
        var results = new List<WitnessAttachmentDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new WitnessAttachmentDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetFieldValue<DateTimeOffset>(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        return results;
    }

    public async Task<IReadOnlyList<WitnessAttachmentDto>> ListAppealAttachmentsAsync(
        Guid caseId,
        Guid appealId,
        WitnessUserContext user,
        CancellationToken ct)
    {
        EnsureAppealReadPermission(user);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        _ = await LockAppealAsync(connection, tx, caseId, appealId, ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT id, file_name, content_type, size_bytes, sha256, classification,
                   form_number, form_version, uploaded_at, uploaded_by_name,
                   appeal_id, evidence_type
            FROM witness.attachments
            WHERE case_id=$1 AND appeal_id=$2 AND deleted_at IS NULL
            ORDER BY uploaded_at DESC
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(appealId);
        var results = new List<WitnessAttachmentDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadAttachment(reader));
        await reader.CloseAsync();
        await tx.CommitAsync(ct);
        return results;
    }

    private async Task<WitnessAppealDto?> GetCurrentAppealAsync(Guid caseId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, case_id, filed_at, filed_channel, statement, late_reason,
                   is_late, status, row_version, external_reference, decision,
                   decided_at, created_at, updated_at
            FROM witness.appeals
            WHERE case_id=$1
            ORDER BY created_at DESC
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue(caseId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return ReadAppeal(reader);
    }

    private async Task<WitnessAppealResultNoticeDto?> GetCurrentAppealResultNoticeAsync(
        Guid caseId,
        Guid appealId,
        CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, case_id, appeal_id, external_result_id, external_reference,
                   recipient, delivery_channel, sent_at, proof_attachment_id,
                   received_at, actual_recipient, receipt_note,
                   receipt_proof_attachment_id, delivery_status, completion_status,
                   created_by_name, created_at, updated_by_name, updated_at
            FROM witness.appeal_result_notices
            WHERE case_id=$1 AND appeal_id=$2
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(appealId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAppealResultNotice(reader) : null;
    }

    private static IReadOnlyList<WitnessAvailableActionDto> FilterAppealResultLifecycleActions(
        string caseStatus,
        IReadOnlyList<WitnessAvailableActionDto> actions,
        WitnessAppealDto? appeal,
        WitnessAppealResultNoticeDto? notice)
    {
        var finalAppeal = appeal is { Status: "decided" }
                          && appeal.Decision is "appeal-upheld" or "appeal-reversed" or "appeal-overturned";
        var eligibleNotifyState = appeal?.Decision switch
        {
            "appeal-upheld" => caseStatus == WitnessStatuses.AppealDecided,
            "appeal-reversed" or "appeal-overturned" => caseStatus is WitnessStatuses.ApprovedPendingNotice
                or WitnessStatuses.ProtectionActive,
            _ => false
        };

        var filtered = actions.Where(action => action.Code switch
        {
            "notify-appeal-result" => finalAppeal && eligibleNotifyState && notice is null,
            "record-appeal-result-receipt" => finalAppeal && notice?.DeliveryStatus == "sent",
            "complete-appeal-result-notification" => finalAppeal && notice?.DeliveryStatus == "received",
            _ => true
        }).ToArray();

        if (!finalAppeal)
            return filtered;
        if (notice is null && eligibleNotifyState)
            return filtered.Where(action => action.Code == "notify-appeal-result").ToArray();
        if (notice?.DeliveryStatus == "sent")
            return filtered.Where(action => action.Code == "record-appeal-result-receipt").ToArray();
        if (notice?.DeliveryStatus == "received")
            return filtered.Where(action => action.Code == "complete-appeal-result-notification").ToArray();
        return filtered.Where(action => action.Code is not (
            "notify-appeal-result" or "record-appeal-result-receipt" or "complete-appeal-result-notification"))
            .ToArray();
    }

    private async Task<IReadOnlyList<WitnessWorkflowEventDto>> ListWorkflowEventsAsync(Guid caseId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, action, from_status, to_status, actor_name, actor_role,
                   reason, external_reference, occurred_at
            FROM witness.workflow_events
            WHERE case_id=$1
            ORDER BY occurred_at
            """);
        cmd.Parameters.AddWithValue(caseId);
        var results = new List<WitnessWorkflowEventDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new WitnessWorkflowEventDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8)));
        return results;
    }

    private async Task<IReadOnlyList<WitnessCaseAssignmentDto>> ListAssignmentsAsync(Guid caseId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, user_id, target_username, assignment_role, org_id,
                   organization_name, source_form_number, reason,
                   assigned_by_name, assigned_at, ended_at
            FROM witness.case_assignments
            WHERE case_id=$1
            ORDER BY assigned_at DESC
            """);
        cmd.Parameters.AddWithValue(caseId);
        var results = new List<WitnessCaseAssignmentDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new WitnessCaseAssignmentDto(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.GetString(7),
                reader.GetString(8), reader.GetFieldValue<DateTimeOffset>(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10)));
        return results;
    }

    private async Task<IReadOnlyList<WitnessSignatureDto>> ListSignaturesAsync(Guid formId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, form_version, signer_name, signer_position, signer_role,
                   signer_purpose, verification_method, evidence_reference, document_hash, signed_at
            FROM witness.form_signatures
            WHERE form_id=$1
            ORDER BY signed_at
            """);
        cmd.Parameters.AddWithValue(formId);
        var results = new List<WitnessSignatureDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new WitnessSignatureDto(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetFieldValue<DateTimeOffset>(9)));
        return results;
    }

    private async Task<IReadOnlyList<WitnessFormOpinionDto>> ListOpinionsAsync(Guid formId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, form_number, form_version, opinion_purpose, opinion_text,
                   actor_name, actor_position, actor_role, created_at
            FROM witness.form_opinions
            WHERE form_id=$1
            ORDER BY created_at
            """);
        cmd.Parameters.AddWithValue(formId);
        var results = new List<WitnessFormOpinionDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new WitnessFormOpinionDto(
                reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8)));
        return results;
    }

    private static async Task<long> NextSequenceAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT nextval('witness.request_number_seq')", connection, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<PublicKb1ShareRow> LockKb1ShareLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string accessToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Trim().Length != 64)
            throw new WitnessWorkflowException("ลิงก์แบบ คบ.1 ไม่ถูกต้องหรือหมดอายุแล้ว");
        await using var cmd = new NpgsqlCommand("""
            SELECT s.id, s.case_id, s.form_id, s.status, s.expires_at,
                   s.created_by, s.created_by_name, c.request_no, c.status,
                   c.row_version, f.version, f.status, f.values_data
            FROM witness.kb1_share_links s
            JOIN witness.cases c ON c.id=s.case_id
            JOIN witness.forms f ON f.id=s.form_id AND f.case_id=s.case_id
            WHERE s.token_sha256=$1
            FOR UPDATE OF s, c, f
            """, connection, tx);
        cmd.Parameters.AddWithValue(ComputeTokenHash(accessToken.Trim()));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new WitnessWorkflowException("ลิงก์แบบ คบ.1 ไม่ถูกต้องหรือหมดอายุแล้ว");
        return new PublicKb1ShareRow(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4), reader.GetGuid(5), reader.GetString(6),
            reader.GetString(7), reader.GetString(8), reader.GetInt64(9), reader.GetInt32(10),
            reader.GetString(11), ReadDictionary(reader.GetString(12)));
    }

    private static void EnsureKb1ShareReadable(PublicKb1ShareRow link, DateTimeOffset now)
    {
        if (link.Status == "submitted")
            return;
        if (link.Status != "active")
            throw new WitnessWorkflowException("ลิงก์แบบ คบ.1 ถูกยกเลิกหรือใช้งานไปแล้ว");
        if (link.ExpiresAt <= now)
            throw new WitnessWorkflowException("ลิงก์แบบ คบ.1 หมดอายุแล้ว กรุณาติดต่อเจ้าหน้าที่เพื่อสร้างลิงก์ใหม่");
    }

    private static void EnsureKb1ShareWritable(PublicKb1ShareRow link, DateTimeOffset now)
    {
        EnsureKb1ShareReadable(link, now);
        if (link.Status != "active")
            throw new WitnessWorkflowException("แบบ คบ.1 ถูกส่งให้เจ้าหน้าที่แล้ว ไม่สามารถแก้ไขผ่านลิงก์นี้ได้");
        if (link.CaseStatus != WitnessStatuses.IntakeDraft || link.FormStatus != "draft")
            throw new WitnessWorkflowException("แฟ้มคำร้องพ้นขั้นตอนรับข้อมูลแล้ว กรุณาติดต่อเจ้าหน้าที่");
    }

    private static string ComputeTokenHash(string token)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();

    private static WitnessUserContext PublicKb1User(PublicKb1ShareRow link)
        => new(
            link.Id,
            $"public-kb1-{link.Id:N}",
            "ผู้ยื่นผ่านลิงก์ คบ.1",
            "ผู้ยื่นคำร้อง",
            new HashSet<string>(["public_petitioner"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static Dictionary<string, string> PublicKb1Values(IReadOnlyDictionary<string, string> source)
    {
        var allowed = Kb1PublicFieldKeys();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            if (allowed.Contains(item.Key) || string.Equals(item.Key, "request_no", StringComparison.OrdinalIgnoreCase))
                result[item.Key] = item.Value;
        }
        return result;
    }

    private static Dictionary<string, string> MergePublicKb1Values(
        IReadOnlyDictionary<string, string> existing,
        IReadOnlyDictionary<string, string> submitted,
        string requestNo)
    {
        var allowed = Kb1PublicFieldKeys();
        var values = PublicKb1Values(existing);
        foreach (var item in submitted)
        {
            if (string.Equals(item.Key, "request_no", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(item.Value, requestNo, StringComparison.Ordinal))
                    throw new WitnessWorkflowException("ไม่สามารถแก้ไขเลขคำร้องผ่านลิงก์ผู้ยื่นได้");
                continue;
            }
            if (!allowed.Contains(item.Key))
                throw new WitnessWorkflowException($"ข้อมูล {item.Key} ไม่อยู่ในแบบ คบ.1 สำหรับผู้ยื่น");
            values[item.Key] = item.Value ?? "";
        }
        values["request_no"] = requestNo;
        return values;
    }

    private static HashSet<string> Kb1PublicFieldKeys()
    {
        var definition = WitnessProtectionFormCatalog.Get(1);
        var keys = new HashSet<string>(definition.Fields.Select(field => field.Key),
            StringComparer.OrdinalIgnoreCase);
        foreach (var field in definition.Fields)
            keys.Add($"{field.Key}_other");
        keys.Remove("request_no");
        return keys;
    }

    private static async Task<IReadOnlyList<WitnessAttachmentDto>> ListPublicKb1AttachmentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid shareLinkId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, file_name, content_type, size_bytes, sha256, classification,
                   form_number, form_version, uploaded_at, uploaded_by_name
            FROM witness.attachments
            WHERE kb1_share_link_id=$1 AND deleted_at IS NULL
            ORDER BY uploaded_at
            """, connection, tx);
        cmd.Parameters.AddWithValue(shareLinkId);
        var result = new List<WitnessAttachmentDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new WitnessAttachmentDto(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetString(9)));
        return result;
    }

    private static WitnessKb1ShareLinkDto ReadKb1ShareLink(NpgsqlDataReader reader)
        => new(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));

    private static async Task<CaseRow> LockCaseAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid caseId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT request_no, status, urgent_status, row_version, created_by,
                   current_owner_user_id, owning_org_id, current_owner_org_id,
                   current_owner_role
            FROM witness.cases WHERE id=$1 FOR UPDATE
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new WitnessWorkflowException("ไม่พบแฟ้มคำร้อง");
        return new CaseRow(
            caseId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            reader.GetString(8));
    }

    private static async Task<FormRow?> LockFormAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid caseId, int formNumber, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT f.id, f.case_id, c.request_no, f.form_number, f.version, f.status,
                   f.values_data, f.updated_at, f.updated_by_name, c.row_version
            FROM witness.forms f JOIN witness.cases c ON c.id=f.case_id
            WHERE f.case_id=$1 AND f.form_number=$2 FOR UPDATE OF f
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(formNumber);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new FormRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetString(5), ReadDictionary(reader.GetString(6)),
            reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetInt64(9));
    }

    private static async Task<HashSet<int>> GetCompletedFormNumbersAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid caseId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT form_number FROM witness.forms
            WHERE case_id=$1 AND status IN ('completed','signed')
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        var result = new HashSet<int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetInt32(0));
        return result;
    }

    private async Task<IReadOnlyList<WitnessAvailableActionDto>> FilterUnsignedActionsAsync(
        Guid caseId,
        string status,
        IReadOnlyList<WitnessAvailableActionDto> actions,
        CancellationToken ct)
    {
        var requirements = actions
            .SelectMany(action => WitnessSignaturePolicy.Requirements(action.Code, status))
            .Distinct()
            .ToArray();
        if (requirements.Length == 0)
            return actions;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var signed = await LoadCurrentSignaturesAsync(connection, null, caseId, ct);
        return actions
            .Where(action => WitnessSignaturePolicy.Requirements(action.Code, status)
                .All(requirement => signed.Contains((requirement.FormNumber, requirement.Purpose))))
            .ToArray();
    }

    private static async Task EnsureRequiredSignaturesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        string action,
        string status,
        CancellationToken ct)
    {
        var requirements = WitnessSignaturePolicy.Requirements(action, status);
        if (requirements.Count == 0)
            return;

        var signed = await LoadCurrentSignaturesAsync(connection, tx, caseId, ct);
        var missing = requirements
            .Where(requirement => !signed.Contains((requirement.FormNumber, requirement.Purpose)))
            .Select(requirement => $"คบ.{requirement.FormNumber} — {requirement.Purpose}")
            .ToArray();
        if (missing.Length > 0)
            throw new WitnessWorkflowException($"ลายมือชื่อที่จำเป็นยังไม่ครบ: {string.Join(", ", missing)}");
    }

    private static async Task<HashSet<(int FormNumber, string Purpose)>> LoadCurrentSignaturesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        Guid caseId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT f.form_number, signature.signer_purpose
            FROM witness.forms f
            JOIN witness.form_signatures signature
              ON signature.form_id=f.id AND signature.form_version=f.version
            WHERE f.case_id=$1
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        var result = new HashSet<(int FormNumber, string Purpose)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    private static async Task<bool> HasMainCaseLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM witness.case_links WHERE witness_case_id=$1)
                OR EXISTS(
                    SELECT 1 FROM witness.cases
                    WHERE id=$1 AND summary_data->>'main_case_status'='new_case')
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task EnsureAppealDeadlineElapsedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT appeal_deadline FROM witness.cases WHERE id=$1", connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        var raw = await cmd.ExecuteScalarAsync(ct);
        var deadline = raw switch
        {
            DateOnly dateOnly => dateOnly,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => throw new WitnessWorkflowException("ไม่พบวันครบกำหนดอุทธรณ์")
        };
        var bangkokToday = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).Date);
        if (bangkokToday <= deadline)
            throw new WitnessWorkflowException($"ยังปิดเรื่องไม่ได้ สิ้นสุดกำหนดอุทธรณ์วันที่ {deadline:dd/MM/yyyy}");
    }

    private static async Task<bool> AttachmentExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        Guid attachmentId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS(
                SELECT 1 FROM witness.attachments
                WHERE id=$1 AND case_id=$2 AND deleted_at IS NULL)
            """, connection, tx);
        cmd.Parameters.AddWithValue(attachmentId);
        cmd.Parameters.AddWithValue(caseId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<bool> AppealAttachmentExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        Guid appealId,
        Guid attachmentId,
        string evidenceType,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS(
                SELECT 1 FROM witness.attachments
                WHERE id=$1 AND case_id=$2 AND appeal_id=$3
                  AND evidence_type=$4 AND deleted_at IS NULL)
            """, connection, tx);
        cmd.Parameters.AddWithValue(attachmentId);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(appealId);
        cmd.Parameters.AddWithValue(evidenceType);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<AppealRow> LockAppealAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        Guid appealId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, case_id, status, row_version, external_reference, decision, decided_at
            FROM witness.appeals
            WHERE id=$1
            FOR UPDATE
            """, connection, tx);
        cmd.Parameters.AddWithValue(appealId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new WitnessWorkflowException("ไม่พบคำอุทธรณ์ที่ระบุ");
        var result = new AppealRow(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
        if (result.CaseId != caseId)
            throw new WitnessWorkflowException("คำอุทธรณ์ไม่ได้อยู่ในแฟ้มคำร้องนี้");
        return result;
    }

    private static async Task<AppealRow> LockCurrentAppealAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, case_id, status, row_version, external_reference, decision, decided_at
            FROM witness.appeals
            WHERE case_id=$1
            ORDER BY created_at DESC
            LIMIT 1
            FOR UPDATE
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new WitnessWorkflowException("ไม่พบคำอุทธรณ์ของแฟ้มนี้");
        return new AppealRow(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
    }

    private static async Task<AppealRow?> TryLockCurrentAppealAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, case_id, status, row_version, external_reference, decision, decided_at
            FROM witness.appeals
            WHERE case_id=$1
            ORDER BY created_at DESC
            LIMIT 1
            FOR UPDATE
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new AppealRow(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
    }

    private static async Task<AppealResultNoticeRow?> TryLockAppealResultNoticeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        Guid appealId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, case_id, appeal_id, external_result_id, external_reference,
                   sent_at, received_at, delivery_status, completion_status
            FROM witness.appeal_result_notices
            WHERE case_id=$1 AND appeal_id=$2
            LIMIT 1
            FOR UPDATE
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(appealId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new AppealResultNoticeRow(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
            reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetString(7), reader.GetString(8));
    }

    private async Task<int?> GetLatestNoticeFormNumberAsync(Guid caseId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT form_number
            FROM witness.notice_deliveries
            WHERE case_id=$1
            ORDER BY sent_at DESC, created_at DESC
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue(caseId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is int number ? number : null;
    }

    private static async Task<int?> GetLatestNoticeFormNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT form_number FROM witness.notice_deliveries
            WHERE case_id=$1 ORDER BY sent_at DESC, created_at DESC LIMIT 1
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is int number ? number : null;
    }

    private async Task<WitnessCaseLinkDto?> GetCaseLinkAsync(Guid caseId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT l.complaint_id, l.complaint_case_no, l.track_no, COALESCE(l.pcms_no,''),
                   COALESCE(l.investigation_no,''), COALESCE(l.accused_display_name,''),
                   COALESCE(l.accused_agency,''), l.relationship_reason, l.risk_level,
                   l.linked_by_name, l.linked_at, c.summary_data, c.risk_level, c.updated_at
            FROM witness.cases c
            LEFT JOIN witness.case_links l ON l.witness_case_id=c.id
            WHERE c.id=$1
            """);
        cmd.Parameters.AddWithValue(caseId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        if (!reader.IsDBNull(0))
            return new WitnessCaseLinkDto(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10),
                "existing_case", "");

        var summary = ReadDictionary(reader.GetString(11));
        if (!summary.GetValueOrDefault("main_case_status", "").Equals("new_case", StringComparison.OrdinalIgnoreCase))
            return null;
        return new WitnessCaseLinkDto(null, "คดีใหม่/ยังไม่มีเลขคดี", "รอรับเรื่องเข้าสู่คดีหลัก", "", "", "", "",
            summary.GetValueOrDefault("new_case_reason", ""), reader.GetString(12),
            summary.GetValueOrDefault("new_case_recorded_by", ""), reader.GetFieldValue<DateTimeOffset>(13),
            "new_case", summary.GetValueOrDefault("new_case_subject", ""));
    }

    private static async Task<WitnessCaseLinkCandidateDto?> LoadComplaintCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        long complaintId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT c.cmp_complaint_id, c.case_no, c.track_no,
                   COALESCE(p.pcms_no, ''), '',
                   COALESCE(c.metadata_json->>'accused_name', ''),
                   COALESCE(c.metadata_json->>'accused_agency', ''),
                   left(concat_ws(' — ', c.complaint_title, c.complaint_description), 1000), 100
            FROM public.tbl_cmp_complaint c
            LEFT JOIN LATERAL (
                SELECT x.pcms_no FROM public.tbl_cmp_pcms_case x
                WHERE x.cmp_complaint_id=c.cmp_complaint_id AND NOT x.is_deleted
                ORDER BY x.created_datetime DESC LIMIT 1) p ON true
            WHERE c.cmp_complaint_id=$1 AND NOT c.is_deleted
            """, connection, tx);
        cmd.Parameters.AddWithValue(complaintId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCaseLinkCandidate(reader) : null;
    }

    private static WitnessCaseLinkCandidateDto ReadCaseLinkCandidate(NpgsqlDataReader reader)
        => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8));

    private static IReadOnlyList<WitnessAvailableActionDto> FilterNoticeReceiptActions(
        IReadOnlyList<WitnessAvailableActionDto> actions,
        int? noticeFormNumber)
        => noticeFormNumber switch
        {
            9 => actions.Where(item => item.Code != "record-notice-receipt-rejected").ToArray(),
            10 or 17 => actions.Where(item => item.Code != "record-notice-receipt-approved").ToArray(),
            _ => actions.Where(item => !item.Code.StartsWith("record-notice-receipt-", StringComparison.Ordinal)).ToArray()
        };

    private static async Task InsertFormVersionAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid formId, Guid caseId,
        int formNumber, int version, string status, string valuesJson, string contentHash,
        WitnessUserContext user, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO witness.form_versions(
                id, form_id, case_id, form_number, version, status, values_data,
                content_sha256, created_by, created_by_name, created_at)
            VALUES($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$9,$10,$11)
            """, connection, tx);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(formId);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(formNumber);
        cmd.Parameters.AddWithValue(version);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue(valuesJson);
        cmd.Parameters.AddWithValue(contentHash);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(user.DisplayName);
        cmd.Parameters.AddWithValue(now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertWorkflowEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid caseId, string action,
        string from, string to, WitnessUserContext user, string reason, string? externalReference,
        string? idempotencyKey, string detailsJson, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO witness.workflow_events(
                id, case_id, action, from_status, to_status, actor_user_id, actor_name,
                actor_role, reason, external_reference, details, occurred_at, idempotency_key)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11::jsonb,$12,$13)
            """, connection, tx);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.AddWithValue(from);
        cmd.Parameters.AddWithValue(to);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(user.DisplayName);
        cmd.Parameters.AddWithValue(PrimaryRole(user));
        cmd.Parameters.AddWithValue(reason?.Trim() ?? "");
        cmd.Parameters.AddWithValue((object?)Normalize(externalReference) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(detailsJson);
        cmd.Parameters.AddWithValue(now);
        cmd.Parameters.AddWithValue((object?)Normalize(idempotencyKey) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IdempotencyReservation?> ReserveIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid actorUserId,
        string resourceScope,
        string operation,
        string? idempotencyKey,
        string requestHash,
        Guid? resourceId,
        CancellationToken ct)
    {
        var normalizedKey = Normalize(idempotencyKey);
        if (normalizedKey is null)
            return null;
        if (normalizedKey.Length > 100)
            throw new WitnessWorkflowException("รหัสป้องกันการบันทึกซ้ำต้องยาวไม่เกิน 100 ตัวอักษร");

        var normalizedOperation = NormalizeIdempotencyOperation(operation);
        var reservationId = Guid.NewGuid();
        await using (var reserve = new NpgsqlCommand("""
            INSERT INTO witness.idempotency_records(
                id, actor_user_id, resource_scope, idempotency_key, operation,
                request_hash, status, resource_id, created_at)
            VALUES($1,$2,$3,$4,$5,$6,'processing',$7,NOW())
            ON CONFLICT (actor_user_id, resource_scope, idempotency_key) DO NOTHING
            RETURNING id
            """, connection, tx))
        {
            reserve.Parameters.AddWithValue(reservationId);
            reserve.Parameters.AddWithValue(actorUserId);
            reserve.Parameters.AddWithValue(resourceScope);
            reserve.Parameters.AddWithValue(normalizedKey);
            reserve.Parameters.AddWithValue(normalizedOperation);
            reserve.Parameters.AddWithValue(requestHash);
            reserve.Parameters.Add(new NpgsqlParameter
            {
                Value = (object?)resourceId ?? DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Uuid
            });
            if (await reserve.ExecuteScalarAsync(ct) is Guid insertedId)
                return new IdempotencyReservation(insertedId, false, null, null, resourceId);
        }

        await using var existing = new NpgsqlCommand("""
            SELECT id, operation, request_hash, status, resource_id,
                   response_status, response_body::text
            FROM witness.idempotency_records
            WHERE actor_user_id=$1 AND resource_scope=$2 AND idempotency_key=$3
            """, connection, tx);
        existing.Parameters.AddWithValue(actorUserId);
        existing.Parameters.AddWithValue(resourceScope);
        existing.Parameters.AddWithValue(normalizedKey);
        await using var reader = await existing.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new WitnessIdempotencyConflictException(
                "ไม่สามารถตรวจสอบผลคำขอเดิมได้ กรุณาลองใหม่อีกครั้ง");

        var existingId = reader.GetGuid(0);
        var existingOperation = reader.GetString(1);
        var existingHash = reader.GetString(2);
        var status = reader.GetString(3);
        Guid? existingResourceId = reader.IsDBNull(4) ? null : reader.GetGuid(4);
        int? responseStatus = reader.IsDBNull(5) ? null : reader.GetInt32(5);
        var responseBody = reader.IsDBNull(6) ? null : reader.GetString(6);

        if (status == "legacy")
            throw new WitnessIdempotencyConflictException(
                "รหัสป้องกันการบันทึกซ้ำนี้เคยถูกใช้กับข้อมูลเดิมแล้ว กรุณาสร้างรหัสใหม่");
        if (!string.Equals(existingOperation, normalizedOperation, StringComparison.Ordinal)
            || !string.Equals(existingHash, requestHash, StringComparison.Ordinal))
        {
            throw new WitnessIdempotencyConflictException(
                "รหัสป้องกันการบันทึกซ้ำนี้ถูกใช้กับคำสั่งหรือข้อมูลอื่นแล้ว กรุณาสร้างรหัสใหม่");
        }
        if (status != "completed" || responseStatus is null || string.IsNullOrWhiteSpace(responseBody))
        {
            throw new WitnessIdempotencyConflictException(
                "คำขอเดิมยังประมวลผลไม่เสร็จ กรุณารอสักครู่แล้วลองใหม่");
        }

        return new IdempotencyReservation(existingId, true, responseBody, responseStatus, existingResourceId);
    }

    private static async Task CompleteIdempotencyAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        IdempotencyReservation? reservation,
        Guid resourceId,
        int responseStatus,
        T response,
        DateTimeOffset completedAt,
        CancellationToken ct)
    {
        if (reservation is null || reservation.IsReplay)
            return;

        await using var cmd = new NpgsqlCommand("""
            UPDATE witness.idempotency_records
            SET status='completed', resource_id=$2, response_status=$3,
                response_body=$4::jsonb, completed_at=$5
            WHERE id=$1 AND status='processing'
            """, connection, tx);
        cmd.Parameters.AddWithValue(reservation.Id);
        cmd.Parameters.AddWithValue(resourceId);
        cmd.Parameters.AddWithValue(responseStatus);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(response, JsonOptions));
        cmd.Parameters.AddWithValue(completedAt);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new WitnessIdempotencyConflictException(
                "ไม่สามารถบันทึกผลสำหรับป้องกันรายการซ้ำได้ กรุณาลองใหม่อีกครั้ง");
    }

    private static T DeserializeIdempotentResponse<T>(IdempotencyReservation reservation)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(reservation.ResponseBody!, JsonOptions)
                   ?? throw new JsonException("Stored response is null");
        }
        catch (JsonException)
        {
            throw new WitnessIdempotencyConflictException(
                "ผลคำขอเดิมไม่สมบูรณ์ กรุณาติดต่อผู้ดูแลระบบโดยไม่ส่งคำขอซ้ำ");
        }
    }

    private static string ComputeIdempotencyHash<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, JsonOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonicalJson(writer, element);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string NormalizeIdempotencyOperation(string operation)
        => operation.Trim().ToLowerInvariant();

    private static string CaseIdempotencyScope(Guid caseId)
        => $"witness:case:{caseId:D}";

    private async Task RecordSecretAccessAsync(
        Guid caseId,
        string action,
        string entityType,
        string entityId,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct,
        string? detailsJson = null)
    {
        var safeHouseVisible = await CanViewSafeHouseAsync(caseId, user, ct);
        var details = detailsJson ?? JsonSerializer.Serialize(new
        {
            piiVisible = user.HasPermission(WitnessPermissions.ViewPii),
            safeHouseVisible,
            organizationId = user.OrganizationId
        });
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await InsertAuditAsync(connection, tx, caseId, action, entityType, entityId,
            user, ipAddress, details, DateTimeOffset.UtcNow, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid? caseId, string action,
        string entityType, string entityId, WitnessUserContext user, string ipAddress,
        string detailsJson, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO witness.audit_events(
                id, case_id, action, entity_type, entity_id, actor_user_id,
                actor_name, actor_role, ip_address, details, occurred_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::jsonb,$11)
            """, connection, tx);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue((object?)caseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.AddWithValue(entityType);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(user.DisplayName);
        cmd.Parameters.AddWithValue(PrimaryRole(user));
        cmd.Parameters.AddWithValue(ipAddress ?? "");
        cmd.Parameters.AddWithValue(detailsJson);
        cmd.Parameters.AddWithValue(now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task IncrementCaseVersionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid caseId, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE witness.cases SET row_version=row_version+1, updated_at=$2 WHERE id=$1
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ActingAuthority?> ResolveActingAuthorityAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, WitnessUserContext user, string signerRole, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT principal_name, delegation_reference, valid_from, valid_to
            FROM witness.acting_authorities
            WHERE acting_user_id=$1 AND target_role=$2 AND revoked_at IS NULL
              AND valid_from <= NOW() AND (valid_to IS NULL OR valid_to >= NOW())
            ORDER BY valid_from DESC LIMIT 1
            """, connection, tx);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(signerRole);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new ActingAuthority(reader.GetString(0), reader.GetString(1),
            reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
    }

    private async Task<WitnessCommandResultDto> ExecuteUrgentCommandAsync(
        Guid caseId,
        string action,
        ExecuteWitnessCommandRequest request,
        WitnessUserContext user,
        string ipAddress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new WitnessWorkflowException("กรุณาระบุเหตุผลประกอบการดำเนินการกรณีเร่งด่วน");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var normalizedAction = NormalizeIdempotencyOperation(action);
        var requestHash = ComputeIdempotencyHash(new
        {
            Action = normalizedAction,
            request.ExpectedVersion,
            Reason = request.Reason?.Trim() ?? "",
            request.Data
        });
        var idempotency = await ReserveIdempotencyAsync(
            connection, tx, user.UserId, CaseIdempotencyScope(caseId), normalizedAction,
            request.IdempotencyKey, requestHash, caseId, ct);
        if (idempotency?.IsReplay == true)
        {
            var replayCase = await LockCaseAsync(connection, tx, caseId, ct);
            await EnsureCaseMutationAccessAsync(connection, tx, replayCase, user, ct);
            var replay = DeserializeIdempotentResponse<WitnessCommandResultDto>(idempotency);
            await tx.CommitAsync(ct);
            return replay;
        }
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        EnsureVersion(caseRow.Version, request.ExpectedVersion);
        await ValidateStoredFormInvariantsAsync(connection, tx, caseId, ct);
        if (caseRow.Status != WitnessStatuses.StaffReview)
            throw new WitnessWorkflowException("Workflow กรณีเร่งด่วนต้องดำเนินควบคู่ระหว่างขั้นตรวจคำร้องเท่านั้น");
        var completedForms = await GetCompletedFormNumbersAsync(connection, tx, caseId, ct);

        var (fromUrgent, toUrgent, permission, requiredForm) = action switch
        {
            "urgent-submit-supervisor" => ("awaiting_kb4", "supervisor_review", WitnessPermissions.OfficerReview, 4),
            "urgent-supervisor-forward" => ("supervisor_review", "director_review", WitnessPermissions.SupervisorReview, 4),
            "urgent-supervisor-return" => ("supervisor_review", "awaiting_kb4", WitnessPermissions.SupervisorReview, 4),
            "urgent-director-approve" => ("director_review", "temporary_active", WitnessPermissions.DirectorReview, 5),
            "urgent-director-return" => ("director_review", "awaiting_kb4", WitnessPermissions.DirectorReview, 4),
            _ => throw new WitnessWorkflowException("ไม่รู้จักคำสั่ง Workflow กรณีเร่งด่วน")
        };
        if (!string.Equals(caseRow.UrgentStatus, fromUrgent, StringComparison.OrdinalIgnoreCase))
            throw new WitnessWorkflowException("สถานะกรณีเร่งด่วนเปลี่ยนไปแล้ว กรุณาโหลดแฟ้มล่าสุด");
        if (!user.Permissions.Contains(permission))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์ดำเนินการกรณีเร่งด่วนในขั้นตอนนี้");
        if (!completedForms.Contains(requiredForm))
            throw new WitnessWorkflowException($"ต้องบันทึกแบบ คบ.{requiredForm} ให้สมบูรณ์ก่อนส่งต่อกรณีเร่งด่วน");
        if (action == "urgent-submit-supervisor" && !completedForms.Contains(3))
            throw new WitnessWorkflowException("ต้องบันทึกแบบ คบ.3 และ คบ.4 ให้สมบูรณ์ก่อนส่งกรณีเร่งด่วน");
        await EnsureRequiredSignaturesAsync(connection, tx, caseId, action, caseRow.Status, ct);

        var now = DateTimeOffset.UtcNow;
        var nextVersion = caseRow.Version + 1;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE witness.cases
            SET urgent_status=$2, row_version=$3, updated_at=$4
            WHERE id=$1
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue(toUrgent);
            cmd.Parameters.AddWithValue(nextVersion);
            cmd.Parameters.AddWithValue(now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (action == "urgent-director-approve")
            await InsertProtectionPeriodFromFormAsync(connection, tx, caseId, 5, 0, now, ct, temporary: true);

        var dataJson = JsonSerializer.Serialize(request.Data ?? [], JsonOptions);
        await InsertWorkflowEventAsync(connection, tx, caseId, action,
            $"{caseRow.Status}/urgent:{fromUrgent}", $"{caseRow.Status}/urgent:{toUrgent}",
            user, request.Reason ?? "", null, request.IdempotencyKey, dataJson, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "workflow.urgent.transition", "case", caseId.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new { action, fromUrgent, toUrgent, mainStatus = caseRow.Status }), now, ct);
        var available = stateMachine.GetAvailableActions(caseRow.Status, toUrgent, user, completedForms);
        var result = new WitnessCommandResultDto(caseId, caseRow.RequestNo, caseRow.Status,
            caseRow.Status, nextVersion, available);
        await CompleteIdempotencyAsync(
            connection, tx, idempotency, caseId, StatusCodes.Status200OK, result, now, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task ValidateStoredFormInvariantsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        CancellationToken ct)
    {
        var storedForms = new List<(int FormNumber, IReadOnlyDictionary<string, string> Values)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT form_number, values_data::text
            FROM witness.forms
            WHERE case_id=$1 AND status IN ('completed', 'signed')
            ORDER BY form_number
            FOR UPDATE
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(caseId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                storedForms.Add((
                    reader.GetInt32(0),
                    JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(1), JsonOptions)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
            }
        }

        foreach (var storedForm in storedForms)
            formPolicy.ValidatePersistentInvariants(storedForm.FormNumber, storedForm.Values);
    }

    private static DateTimeOffset? ParseNoticeReceivedAt(
        string action,
        IReadOnlyDictionary<string, string>? data,
        DateTimeOffset now)
    {
        if (action is not ("record-notice-receipt-approved" or "record-notice-receipt-rejected"))
            return null;
        if (data is null
            || !data.TryGetValue("received_at", out var value)
            || !DateTimeOffset.TryParse(value, out var receivedAt))
        {
            throw new WitnessWorkflowException("กรุณาระบุวันที่และเวลาที่ผู้รับได้รับหนังสือแจ้งผล");
        }
        if (receivedAt.ToUniversalTime() > now.ToUniversalTime())
            throw new WitnessWorkflowException("วันรับหนังสือต้องไม่เป็นเวลาในอนาคต");
        return receivedAt;
    }

    private static async Task<NoticeReceiptTarget> LockAndValidateLatestNoticeDeliveryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        string action,
        DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, form_number, sent_at
            FROM witness.notice_deliveries
            WHERE case_id=$1
            ORDER BY sent_at DESC, created_at DESC
            LIMIT 1
            FOR UPDATE
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new WitnessWorkflowException("ไม่พบประวัติการส่งหนังสือสำหรับบันทึกวันรับ");

        var target = new NoticeReceiptTarget(
            reader.GetGuid(0),
            reader.GetInt32(1),
            reader.GetFieldValue<DateTimeOffset>(2));

        var actionMatchesForm = action switch
        {
            "record-notice-receipt-approved" => target.FormNumber == 9,
            "record-notice-receipt-rejected" => target.FormNumber is 10 or 17,
            _ => false
        };
        if (!actionMatchesForm)
        {
            throw new WitnessWorkflowException(
                $"ประเภทการบันทึกวันรับไม่ตรงกับหนังสือแจ้งผล คบ.{target.FormNumber}");
        }
        if (receivedAt.ToUniversalTime() < target.SentAt.ToUniversalTime())
        {
            throw new WitnessWorkflowException(
                "วันรับหนังสือต้องไม่ก่อนวันที่และเวลาส่งหนังสือ");
        }

        return target;
    }

    private static async Task InsertAppealAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        CaseRow caseRow,
        ExecuteWitnessCommandRequest request,
        WitnessUserContext user,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var data = request.Data ?? throw new WitnessWorkflowException("กรุณาระบุข้อมูลคำอุทธรณ์");
        if (!data.TryGetValue("filed_at", out var filedValue) || !DateTimeOffset.TryParse(filedValue, out var filedAt))
            throw new WitnessWorkflowException("กรุณาระบุวันที่ยื่นอุทธรณ์");
        if (!data.TryGetValue("filed_channel", out var channel) || string.IsNullOrWhiteSpace(channel))
            throw new WitnessWorkflowException("กรุณาระบุช่องทางยื่นอุทธรณ์");

        DateOnly? deadline = null;
        DateTimeOffset? noticeReceivedAt = null;
        await using (var deadlineCmd = new NpgsqlCommand("SELECT appeal_deadline, notice_received_at FROM witness.cases WHERE id=$1", connection, tx))
        {
            deadlineCmd.Parameters.AddWithValue(caseRow.Id);
            await using var deadlineReader = await deadlineCmd.ExecuteReaderAsync(ct);
            if (await deadlineReader.ReadAsync(ct))
            {
                if (!deadlineReader.IsDBNull(0))
                {
                    var raw = deadlineReader.GetValue(0);
                    if (raw is DateTime dateTime) deadline = DateOnly.FromDateTime(dateTime);
                    else if (raw is DateOnly dateOnly) deadline = dateOnly;
                }
                if (!deadlineReader.IsDBNull(1)) noticeReceivedAt = deadlineReader.GetFieldValue<DateTimeOffset>(1);
            }
        }
        if (filedAt > now.AddMinutes(5))
            throw new WitnessWorkflowException("วันยื่นอุทธรณ์ต้องไม่เป็นเวลาในอนาคต");
        if (noticeReceivedAt.HasValue && filedAt < noticeReceivedAt.Value)
            throw new WitnessWorkflowException("วันยื่นอุทธรณ์ต้องไม่ก่อนวันที่ได้รับหนังสือแจ้งผล");
        var isLate = deadline.HasValue && DateOnly.FromDateTime(filedAt.Date) > deadline.Value;
        data.TryGetValue("late_reason", out var lateReason);
        if (isLate && string.IsNullOrWhiteSpace(lateReason))
            throw new WitnessWorkflowException("คำอุทธรณ์เกิน 30 วัน กรุณาระบุเหตุผลที่ยื่นล่าช้า");

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO witness.appeals(
                id, case_id, filed_at, filed_channel, statement, late_reason,
                is_late, status, created_by, created_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,'received',$8,$9)
            """, connection, tx);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(caseRow.Id);
        cmd.Parameters.AddWithValue(filedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue(channel.Trim());
        cmd.Parameters.AddWithValue(request.Reason.Trim());
        cmd.Parameters.AddWithValue((object?)Normalize(lateReason) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(isLate);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertNoticeDeliveryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        CaseRow caseRow,
        ExecuteWitnessCommandRequest request,
        WitnessUserContext user,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var data = request.Data ?? throw new WitnessWorkflowException("กรุณาระบุข้อมูลการส่งหนังสือแจ้งผล");
        if (!data.TryGetValue("sent_at", out var sentValue) || !DateTimeOffset.TryParse(sentValue, out var sentAt))
            throw new WitnessWorkflowException("กรุณาระบุวันที่และเวลาส่งหนังสือ");
        if (sentAt.ToUniversalTime() > now)
            throw new WitnessWorkflowException("วันส่งหนังสือต้องไม่เป็นเวลาในอนาคต");
        if (!data.TryGetValue("delivery_channel", out var channel) || string.IsNullOrWhiteSpace(channel))
            throw new WitnessWorkflowException("กรุณาระบุช่องทางส่งหนังสือ");
        if (!data.TryGetValue("recipient", out var recipient) || string.IsNullOrWhiteSpace(recipient))
            throw new WitnessWorkflowException("กรุณาระบุผู้รับหนังสือ");
        data.TryGetValue("tracking_reference", out var trackingReference);
        var formNumber = caseRow.Status switch
        {
            WitnessStatuses.ApprovedPendingNotice => 9,
            WitnessStatuses.RejectedPendingNotice => 10,
            WitnessStatuses.TerminationOrdered => 17,
            _ => throw new WitnessWorkflowException("สถานะปัจจุบันไม่สามารถส่งหนังสือแจ้งผลได้")
        };

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO witness.notice_deliveries(
                id, case_id, form_number, sent_at, delivery_channel, recipient,
                tracking_reference, created_by, created_by_name, created_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            """, connection, tx);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(caseRow.Id);
        cmd.Parameters.AddWithValue(formNumber);
        cmd.Parameters.AddWithValue(sentAt.ToUniversalTime());
        cmd.Parameters.AddWithValue(channel.Trim());
        cmd.Parameters.AddWithValue(recipient.Trim());
        cmd.Parameters.AddWithValue((object?)Normalize(trackingReference) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(user.DisplayName);
        cmd.Parameters.AddWithValue(now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<AppealResultCommandContext?> PrepareAppealResultLifecycleCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        CaseRow caseRow,
        string action,
        ExecuteWitnessCommandRequest request,
        WitnessUserContext user,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var isLifecycleAction = action is "notify-appeal-result"
            or "record-appeal-result-receipt"
            or "complete-appeal-result-notification";
        var mayHavePendingFinalAppeal = caseRow.Status is WitnessStatuses.AppealDecided
            or WitnessStatuses.AppealResultNoticeSent
            or WitnessStatuses.AppealResultReceived
            or WitnessStatuses.ApprovedPendingNotice
            or WitnessStatuses.ProtectionActive;
        if (!isLifecycleAction && !mayHavePendingFinalAppeal)
            return null;

        var appeal = await TryLockCurrentAppealAsync(connection, tx, caseRow.Id, ct);
        var isFinal = appeal is { Status: "decided", Decision: "appeal-upheld" or "appeal-reversed" or "appeal-overturned" };
        if (!isFinal)
        {
            if (isLifecycleAction)
                throw new WitnessWorkflowException("ผลอุทธรณ์ยังไม่เป็นผลชี้ขาด");
            return null;
        }

        var notice = await TryLockAppealResultNoticeAsync(connection, tx, caseRow.Id, appeal!.Id, ct);
        if (!isLifecycleAction)
        {
            if (notice is null && IsAppealResultNoticeEligibleState(caseRow.Status, appeal.Decision!))
                throw new WitnessWorkflowException("ต้องส่งหนังสือแจ้งผลอุทธรณ์และบันทึกวันรับก่อนดำเนินแฟ้มตามผลชี้ขาด");
            return new AppealResultCommandContext(appeal, notice);
        }

        if (!user.HasExplicitPermission(WitnessPermissions.AppealManage))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์แจ้งผลอุทธรณ์");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new WitnessWorkflowException("กรุณาระบุรหัสป้องกันการบันทึกซ้ำสำหรับการแจ้งผลอุทธรณ์");
        var data = request.Data ?? throw new WitnessWorkflowException("กรุณาระบุข้อมูลหนังสือแจ้งผลอุทธรณ์");
        var appealId = ParseRequiredGuid(data, "appeal_id", "กรุณาระบุคำอุทธรณ์");
        if (appealId != appeal.Id)
            throw new WitnessWorkflowException("คำอุทธรณ์ไม่ได้อยู่ในแฟ้มคำร้องนี้หรือไม่ใช่คำอุทธรณ์รอบปัจจุบัน");

        if (action == "notify-appeal-result")
        {
            if (notice is not null)
                throw new WitnessWorkflowException("ส่งหนังสือแจ้งผลอุทธรณ์แล้ว");
            if (!IsAppealResultNoticeEligibleState(caseRow.Status, appeal.Decision!))
                throw new WitnessWorkflowException("สถานะปัจจุบันไม่สามารถส่งหนังสือแจ้งผลอุทธรณ์ได้");
            if (string.IsNullOrWhiteSpace(appeal.ExternalReference))
                throw new WitnessWorkflowException("ไม่พบเลขอ้างอิงผลอุทธรณ์จาก External Module");

            var sentAt = ParseRequiredInstant(data, "sent_at", "กรุณาระบุวันที่และเวลาส่งหนังสือแจ้งผลอุทธรณ์");
            if (sentAt > now)
                throw new WitnessWorkflowException("วันส่งหนังสือแจ้งผลอุทธรณ์ต้องไม่เป็นเวลาในอนาคต");
            var recipient = RequiredText(data, "recipient", "กรุณาระบุผู้รับหนังสือแจ้งผลอุทธรณ์");
            var channel = RequiredText(data, "delivery_channel", "กรุณาระบุช่องทางส่งหนังสือแจ้งผลอุทธรณ์");
            ValidateAppealResultDeliveryChannel(channel);
            var proofAttachmentId = ParseRequiredGuid(data, "proof_attachment_id", "กรุณาแนบหลักฐานการส่งผลอุทธรณ์");
            if (!await AppealAttachmentExistsAsync(
                    connection, tx, caseRow.Id, appeal.Id, proofAttachmentId,
                    AppealResultNoticeProof, ct))
                throw new WitnessWorkflowException("ไม่พบหลักฐานการส่งผลอุทธรณ์ในคำอุทธรณ์นี้");

            if (data.TryGetValue("external_reference", out var suppliedReference)
                && !string.IsNullOrWhiteSpace(suppliedReference)
                && !string.Equals(suppliedReference.Trim(), appeal.ExternalReference, StringComparison.Ordinal))
                throw new WitnessWorkflowException("เลขอ้างอิงผลอุทธรณ์ไม่ตรงกับผลจาก External Module");

            var externalResultId = await GetFinalAppealExternalResultIdAsync(
                connection, tx, caseRow.Id, appeal.ExternalReference, appeal.Decision!, ct);
            var completionStatus = appeal.Decision switch
            {
                "appeal-upheld" => WitnessStatuses.Closed,
                "appeal-reversed" or "appeal-overturned" when caseRow.Status is WitnessStatuses.ApprovedPendingNotice
                    or WitnessStatuses.ProtectionActive => caseRow.Status,
                _ => throw new WitnessWorkflowException("ผลอุทธรณ์ยังไม่เป็นผลชี้ขาด")
            };
            return new AppealResultCommandContext(
                appeal, null, externalResultId, appeal.ExternalReference, sentAt,
                proofAttachmentId, Recipient: recipient, DeliveryChannel: channel,
                CompletionStatus: completionStatus);
        }

        if (notice is null)
            throw new WitnessWorkflowException("ยังไม่ได้ส่งหนังสือแจ้งผลอุทธรณ์");

        if (action == "record-appeal-result-receipt")
        {
            if (notice.DeliveryStatus != "sent")
                throw new WitnessWorkflowException("บันทึกวันรับผลอุทธรณ์แล้ว");
            var receivedAt = ParseRequiredInstant(data, "received_at", "กรุณาระบุวันที่และเวลารับผลอุทธรณ์");
            if (receivedAt > now)
                throw new WitnessWorkflowException("วันรับผลอุทธรณ์ต้องไม่เป็นเวลาในอนาคต");
            if (receivedAt < notice.SentAt)
                throw new WitnessWorkflowException("วันรับผลอุทธรณ์ต้องไม่ก่อนวันที่และเวลาส่งหนังสือ");

            Guid? receiptProofId = null;
            if (data.TryGetValue("receipt_proof_attachment_id", out var receiptProofValue)
                && !string.IsNullOrWhiteSpace(receiptProofValue))
            {
                receiptProofId = ParseRequiredGuid(
                    data, "receipt_proof_attachment_id", "หลักฐานการรับผลอุทธรณ์ไม่ถูกต้อง");
                if (!await AppealAttachmentExistsAsync(
                        connection, tx, caseRow.Id, appeal.Id, receiptProofId.Value,
                        AppealResultNoticeProof, ct))
                    throw new WitnessWorkflowException("ไม่พบหลักฐานการรับผลอุทธรณ์ในคำอุทธรณ์นี้");
            }
            data.TryGetValue("actual_recipient", out var actualRecipient);
            data.TryGetValue("receipt_note", out var receiptNote);
            return new AppealResultCommandContext(
                appeal, notice, notice.ExternalResultId, notice.ExternalReference,
                receivedAt, ReceiptProofAttachmentId: receiptProofId,
                ActualRecipient: Normalize(actualRecipient), ReceiptNote: Normalize(receiptNote),
                CompletionStatus: notice.CompletionStatus);
        }

        if (notice.DeliveryStatus != "received" || !notice.ReceivedAt.HasValue)
            throw new WitnessWorkflowException("ยังไม่สามารถปิดเรื่องได้ เนื่องจากยังไม่ได้บันทึกหลักฐานการรับผลอุทธรณ์");
        return new AppealResultCommandContext(
            appeal, notice, notice.ExternalResultId, notice.ExternalReference,
            CompletionStatus: notice.CompletionStatus);
    }

    private static async Task InsertAppealResultNoticeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        CaseRow caseRow,
        ExecuteWitnessCommandRequest request,
        AppealResultCommandContext context,
        WitnessUserContext user,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO witness.appeal_result_notices(
                id, case_id, appeal_id, external_result_id, external_reference,
                recipient, delivery_channel, sent_at, proof_attachment_id,
                delivery_status, completion_status, correlation_reference,
                created_by, created_by_name, created_at,
                updated_by, updated_by_name, updated_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,'sent',$10,$11,$12,$13,$14,$12,$13,$14)
            """, connection, tx);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(caseRow.Id);
        cmd.Parameters.AddWithValue(context.Appeal.Id);
        cmd.Parameters.AddWithValue(context.ExternalResultId!.Value);
        cmd.Parameters.AddWithValue(context.ExternalReference!);
        cmd.Parameters.AddWithValue(context.Recipient!);
        cmd.Parameters.AddWithValue(context.DeliveryChannel!);
        cmd.Parameters.AddWithValue(context.EventAt!.Value.ToUniversalTime());
        cmd.Parameters.AddWithValue(context.ProofAttachmentId!.Value);
        cmd.Parameters.AddWithValue(context.CompletionStatus!);
        cmd.Parameters.AddWithValue(request.IdempotencyKey!.Trim());
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(user.DisplayName);
        cmd.Parameters.AddWithValue(now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RecordAppealResultReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        ExecuteWitnessCommandRequest request,
        AppealResultCommandContext context,
        WitnessUserContext user,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE witness.appeal_result_notices
            SET received_at=$2, actual_recipient=$3, receipt_note=$4,
                receipt_proof_attachment_id=$5, delivery_status='received',
                correlation_reference=$6, updated_by=$7, updated_by_name=$8, updated_at=$9
            WHERE id=$1 AND delivery_status='sent'
            """, connection, tx);
        cmd.Parameters.AddWithValue(context.Notice!.Id);
        cmd.Parameters.AddWithValue(context.EventAt!.Value.ToUniversalTime());
        cmd.Parameters.AddWithValue((object?)context.ActualRecipient ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)context.ReceiptNote ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)context.ReceiptProofAttachmentId ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Uuid
        });
        cmd.Parameters.AddWithValue(request.IdempotencyKey!.Trim());
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(user.DisplayName);
        cmd.Parameters.AddWithValue(now);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new WitnessConcurrencyException("ข้อมูลการรับผลอุทธรณ์ถูกแก้ไขแล้ว กรุณาโหลดข้อมูลล่าสุด");
    }

    private static async Task CompleteAppealResultNoticeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        AppealResultCommandContext context,
        WitnessUserContext user,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE witness.appeal_result_notices
            SET delivery_status='completed', updated_by=$2, updated_by_name=$3, updated_at=$4
            WHERE id=$1 AND delivery_status='received' AND received_at IS NOT NULL
            """, connection, tx);
        cmd.Parameters.AddWithValue(context.Notice!.Id);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(user.DisplayName);
        cmd.Parameters.AddWithValue(now);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new WitnessWorkflowException("ยังไม่สามารถปิดเรื่องได้ เนื่องจากยังไม่ได้บันทึกหลักฐานการรับผลอุทธรณ์");
    }

    private static bool IsAppealResultNoticeEligibleState(string caseStatus, string decision)
        => decision switch
        {
            "appeal-upheld" => caseStatus == WitnessStatuses.AppealDecided,
            "appeal-reversed" or "appeal-overturned" => caseStatus is WitnessStatuses.ApprovedPendingNotice
                or WitnessStatuses.ProtectionActive,
            _ => false
        };

    private static void ValidateAppealResultDeliveryChannel(string channel)
    {
        if (channel is not ("ส่งมอบด้วยตนเอง" or "ไปรษณีย์ลงทะเบียน" or "ไปรษณีย์ตอบรับ"
            or "เจ้าหน้าที่นำส่ง" or "ช่องทางอิเล็กทรอนิกส์" or "อีเมลและหนังสือตาม"))
            throw new WitnessWorkflowException("ช่องทางส่งหนังสือแจ้งผลอุทธรณ์ไม่ถูกต้อง");
    }

    private static string RequiredText(
        IReadOnlyDictionary<string, string> data,
        string key,
        string message)
        => data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new WitnessWorkflowException(message);

    private static Guid ParseRequiredGuid(
        IReadOnlyDictionary<string, string> data,
        string key,
        string message)
        => data.TryGetValue(key, out var value) && Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new WitnessWorkflowException(message);

    private static DateTimeOffset ParseRequiredInstant(
        IReadOnlyDictionary<string, string> data,
        string key,
        string message)
    {
        if (!data.TryGetValue(key, out var value)
            || !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsed))
            throw new WitnessWorkflowException(message);
        return parsed.ToUniversalTime();
    }

    private static async Task<Guid> GetFinalAppealExternalResultIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        string externalReference,
        string decision,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id
            FROM witness.external_results
            WHERE case_id=$1 AND reference_no=$2 AND result_type=$3
            ORDER BY received_at DESC
            LIMIT 1
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(externalReference);
        cmd.Parameters.AddWithValue(decision);
        return await cmd.ExecuteScalarAsync(ct) is Guid id
            ? id
            : throw new WitnessWorkflowException("ไม่พบผลอุทธรณ์ชี้ขาดจาก External Module");
    }

    private static async Task RecordNoticeReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        Guid deliveryId,
        DateTimeOffset receivedAt,
        IReadOnlyDictionary<string, string>? data,
        CancellationToken ct)
    {
        if (data?.TryGetValue("receipt_proof_attachment_id", out var proofValue) != true
            || !Guid.TryParse(proofValue, out var proofAttachmentId))
            throw new WitnessWorkflowException("กรุณาแนบหลักฐานการรับหนังสือ");

        await using (var proofCmd = new NpgsqlCommand("""
            SELECT EXISTS(
                SELECT 1 FROM witness.attachments
                WHERE id=$1 AND case_id=$2 AND deleted_at IS NULL)
            """, connection, tx))
        {
            proofCmd.Parameters.AddWithValue(proofAttachmentId);
            proofCmd.Parameters.AddWithValue(caseId);
            if (await proofCmd.ExecuteScalarAsync(ct) is not true)
                throw new WitnessWorkflowException("ไม่พบไฟล์หลักฐานการรับหนังสือในแฟ้มนี้");
        }

        await using var cmd = new NpgsqlCommand("""
            UPDATE witness.notice_deliveries
            SET received_at=$2, receipt_proof_attachment_id=$3
            WHERE id=$1 AND case_id=$4
            """, connection, tx);
        cmd.Parameters.AddWithValue(deliveryId);
        cmd.Parameters.AddWithValue(receivedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue(proofAttachmentId);
        cmd.Parameters.AddWithValue(caseId);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw new WitnessWorkflowException("ไม่พบประวัติการส่งหนังสือสำหรับบันทึกวันรับ");
    }

    private static async Task InsertProtectionPeriodFromFormAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        int formNumber,
        int requestedRound,
        DateTimeOffset now,
        CancellationToken ct,
        bool temporary = false)
    {
        await using var formCmd = new NpgsqlCommand("""
            SELECT values_data FROM witness.forms
            WHERE case_id=$1 AND form_number=$2 AND status IN ('completed','signed')
            """, connection, tx);
        formCmd.Parameters.AddWithValue(caseId);
        formCmd.Parameters.AddWithValue(formNumber);
        var raw = await formCmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(raw))
            throw new WitnessWorkflowException($"ไม่พบแบบ คบ.{formNumber} ฉบับสมบูรณ์สำหรับกำหนดระยะเวลาคุ้มครอง");
        var values = ReadDictionary(raw);
        var startKey = formNumber == 14 ? "extension_start" : "start_date";
        var endKey = formNumber == 14 ? "extension_end" : "end_date";
        if (!WitnessIsoDate.TryParse(values.GetValueOrDefault(startKey), out var start)
            || !WitnessIsoDate.TryParse(values.GetValueOrDefault(endKey), out var end)
            || end < start)
            throw new WitnessWorkflowException($"ช่วงวันที่ในแบบ คบ.{formNumber} ไม่ถูกต้อง");
        var days = end.DayNumber - start.DayNumber + 1;
        if (days > 90)
            throw new WitnessWorkflowException("ระยะเวลาคุ้มครองต่อรอบต้องไม่เกิน 90 วัน");

        var existingDays = 0;
        var nextRound = requestedRound;
        await using (var sumCmd = new NpgsqlCommand("SELECT COALESCE(SUM(days),0)::int, COALESCE(MAX(round_number),0)::int FROM witness.protection_periods WHERE case_id=$1", connection, tx))
        {
            sumCmd.Parameters.AddWithValue(caseId);
            await using var reader = await sumCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) { existingDays = reader.GetInt32(0); if (nextRound <= 0) nextRound = reader.GetInt32(1) + 1; }
        }
        if (!temporary && existingDays + days > 180)
            throw new WitnessWorkflowException("ระยะเวลาคุ้มครองสะสมต้องไม่เกิน 180 วัน");
        if (temporary) nextRound = 0;

        await using var insert = new NpgsqlCommand("""
            INSERT INTO witness.protection_periods(
                id, case_id, round_number, start_date, end_date, days, source_form_number, created_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8)
            ON CONFLICT(case_id, round_number) DO UPDATE
            SET start_date=EXCLUDED.start_date, end_date=EXCLUDED.end_date,
                days=EXCLUDED.days, source_form_number=EXCLUDED.source_form_number
            """, connection, tx);
        insert.Parameters.AddWithValue(Guid.NewGuid());
        insert.Parameters.AddWithValue(caseId);
        insert.Parameters.AddWithValue(nextRound);
        insert.Parameters.AddWithValue(start);
        insert.Parameters.AddWithValue(end);
        insert.Parameters.AddWithValue(days);
        insert.Parameters.AddWithValue(formNumber);
        insert.Parameters.AddWithValue(now);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static string ResolveExternalTarget(string currentStatus, string resultType)
    {
        var result = resultType.Trim().ToLowerInvariant();
        return (currentStatus, result) switch
        {
            (WitnessStatuses.ExternalPending, "approved") => WitnessStatuses.ApprovedPendingNotice,
            (WitnessStatuses.ExternalPending, "rejected") => WitnessStatuses.RejectedPendingNotice,
            (WitnessStatuses.WithdrawalExternalPending, "rejected") => WitnessStatuses.RejectedPendingNotice,
            (WitnessStatuses.WithdrawalExternalPending, "return-for-revision") => WitnessStatuses.WithdrawalStaffRevision,
            (WitnessStatuses.ExtensionExternalPending, "extension-approved") => WitnessStatuses.ProtectionActive,
            (WitnessStatuses.ExtensionExternalPending, "extension-rejected") => WitnessStatuses.TerminationOrdered,
            (WitnessStatuses.TerminationExternalPending, "termination-ordered") => WitnessStatuses.TerminationOrdered,
            (WitnessStatuses.AppealExternalPending, "appeal-upheld") => WitnessStatuses.AppealDecided,
            (WitnessStatuses.AppealExternalPending, "appeal-reversed" or "appeal-overturned") => WitnessStatuses.AppealDecided,
            (WitnessStatuses.TransferExternalPending, "transfer-approved") => WitnessStatuses.TransferWaiting,
            (WitnessStatuses.TransferExternalPending, "transfer-rejected" or "return-for-revision") => WitnessStatuses.ProtectionActive,
            (WitnessStatuses.ExternalPending, "return-for-revision") => WitnessStatuses.StaffReview,
            (WitnessStatuses.ExtensionExternalPending, "return-for-revision") => WitnessStatuses.ProtectionActive,
            (WitnessStatuses.TerminationExternalPending, "return-for-revision") => WitnessStatuses.ProtectionActive,
            (WitnessStatuses.AppealExternalPending, "return-for-revision") => WitnessStatuses.AppealReceived,
            _ => throw new WitnessWorkflowException("ผลจาก External Module ไม่ตรงกับสถานะปัจจุบัน")
        };
    }

    private static async Task EnsureWithdrawalStatementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT values_data->>'statement_type'
            FROM witness.forms
            WHERE case_id=$1 AND form_number=3 AND status IN ('completed','signed')
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        var statementType = (await cmd.ExecuteScalarAsync(ct))?.ToString();
        if (!string.Equals(statementType, "บันทึกกรณีพยานขอถอนคำร้อง", StringComparison.Ordinal))
            throw new WitnessWorkflowException("ต้องเลือกประเภท คบ.3 เป็น ‘บันทึกกรณีพยานขอถอนคำร้อง’ ก่อนส่งเรื่องถอนคำร้อง");
    }

    private static async Task EnsureTransferEligibilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
        IReadOnlyDictionary<string, string>? data,
        CancellationToken ct)
    {
        var values = data ?? new Dictionary<string, string>();
        if (!values.TryGetValue("criminal_case_eligible", out var eligible)
            || !string.Equals(eligible, "true", StringComparison.OrdinalIgnoreCase))
            throw new WitnessWorkflowException("ต้องยืนยันว่าเป็นคดีอาญาที่เข้าเกณฑ์ส่งต่อกรมคุ้มครองสิทธิและเสรีภาพ");
        if (!values.TryGetValue("transfer_trigger", out var trigger)
            || trigger is not ("witness-request" or "approaching-180-days"))
            throw new WitnessWorkflowException("กรุณาระบุว่าพยานร้องขอส่งต่อ หรือระยะเวลาคุ้มครองใกล้ครบ 180 วัน");
        if (trigger == "approaching-180-days")
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT COALESCE(SUM(days),0)::int FROM witness.protection_periods WHERE case_id=$1", connection, tx);
            cmd.Parameters.AddWithValue(caseId);
            var days = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            if (days < 150)
                throw new WitnessWorkflowException($"ระยะเวลาคุ้มครองสะสมปัจจุบัน {days} วัน ยังไม่ถึงเกณฑ์ใกล้ครบ 180 วัน (ตั้งแต่ 150 วัน)");
        }
    }

    private static Dictionary<string, string> BuildSummaryData(IReadOnlyDictionary<string, string> values)
    {
        var witness = JoinName(values, "witness_prefix", "witness_first_name", "witness_last_name");
        if (string.IsNullOrWhiteSpace(witness))
            witness = JoinName(values, "reporter_prefix", "reporter_first_name", "reporter_last_name");
        var petitioner = JoinName(values, "petitioner_prefix", "petitioner_first_name", "petitioner_last_name");
        if (string.IsNullOrWhiteSpace(petitioner))
            petitioner = witness;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["witness_name"] = string.IsNullOrWhiteSpace(witness) ? "ไม่ระบุ" : witness,
            ["petitioner_name"] = string.IsNullOrWhiteSpace(petitioner) ? "ไม่ระบุ" : petitioner,
            ["phone"] = FirstValue(values, "witness_phone", "reporter_phone", "phone", "petitioner_phone"),
            ["case_subject"] = FirstValue(values, "complaint_subject", "case_subject", "corruption_case")
        };
    }

    private static string JoinName(IReadOnlyDictionary<string, string> values, params string[] keys)
        => string.Join(" ", keys.Select(key => values.GetValueOrDefault(key, "")).Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    private static string FirstValue(IReadOnlyDictionary<string, string> values, params string[] keys)
        => keys.Select(key => values.GetValueOrDefault(key, "")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static Dictionary<string, string> ReadDictionary(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => property.Value.GetRawText()
            };
        }
        return result;
    }

    private static string MaskName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length <= 2)
            return "***";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(part => part.Length <= 1 ? "*" : $"{part[0]}{new string('*', Math.Min(5, part.Length - 1))}"));
    }

    private static string OwnerForStatus(string status) => status switch
    {
        WitnessStatuses.IntakeDraft => "petitioner",
        WitnessStatuses.StaffReview => "officer",
        WitnessStatuses.WithdrawalStaffRevision => "officer",
        WitnessStatuses.SupervisorReview => "supervisor",
        WitnessStatuses.WithdrawalSupervisorReview => "supervisor",
        WitnessStatuses.DirectorReview => "director",
        WitnessStatuses.WithdrawalDirectorReview => "director",
        WitnessStatuses.ExtensionDirectorReview => "director",
        WitnessStatuses.ExtensionSupervisorReview => "supervisor",
        WitnessStatuses.ExternalPending or WitnessStatuses.ExtensionExternalPending
            or WitnessStatuses.TerminationExternalPending or WitnessStatuses.AppealExternalPending
            or WitnessStatuses.WithdrawalExternalPending or WitnessStatuses.TransferExternalPending => "external_module",
        WitnessStatuses.ApprovedPendingNotice or WitnessStatuses.RejectedPendingNotice
            or WitnessStatuses.NoticeSent or WitnessStatuses.TerminationOrdered => "officer",
        WitnessStatuses.ProtectionSetup or WitnessStatuses.ProtectionActive
            or WitnessStatuses.TransferWaiting or WitnessStatuses.TransferAccepted
            or WitnessStatuses.TransferRejected => "protection_officer",
        WitnessStatuses.AppealWindow or WitnessStatuses.AppealReceived or WitnessStatuses.AppealDecided
            or WitnessStatuses.AppealResultNoticeSent or WitnessStatuses.AppealResultReceived => "appeal_officer",
        _ => "officer"
    };

    private static void EnsureAssignmentRoleAllowed(string status, string assignmentRole)
    {
        var allowed = assignmentRole switch
        {
            "officer" => status is WitnessStatuses.StaffReview or WitnessStatuses.WithdrawalStaffRevision,
            "notice_officer" => status is WitnessStatuses.ApprovedPendingNotice
                or WitnessStatuses.RejectedPendingNotice or WitnessStatuses.NoticeSent
                or WitnessStatuses.TerminationOrdered,
            "protection_officer" => status is WitnessStatuses.ApprovedPendingNotice
                or WitnessStatuses.ProtectionSetup or WitnessStatuses.ProtectionActive
                or WitnessStatuses.ExtensionSupervisorReview or WitnessStatuses.ExtensionDirectorReview
                or WitnessStatuses.ExtensionExternalPending or WitnessStatuses.TerminationExternalPending
                or WitnessStatuses.TransferExternalPending or WitnessStatuses.TransferWaiting
                or WitnessStatuses.TransferAccepted or WitnessStatuses.TransferRejected,
            "appeal_officer" => status is WitnessStatuses.AppealWindow or WitnessStatuses.AppealReceived
                or WitnessStatuses.AppealExternalPending or WitnessStatuses.AppealDecided
                or WitnessStatuses.AppealResultNoticeSent or WitnessStatuses.AppealResultReceived,
            _ => false
        };
        if (!allowed)
            throw new WitnessWorkflowException("ประเภทผู้รับมอบหมายไม่สัมพันธ์กับขั้นตอนปัจจุบันของแฟ้ม");
    }

    private static void EnsureView(WitnessUserContext user)
    {
        if (!user.IsGlobalAdministrator
            && !user.HasPermission(WitnessPermissions.ViewMasked)
            && !user.HasPermission(WitnessPermissions.ViewPii)
            && !user.HasPermission(WitnessPermissions.ExternalReceive))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์เข้าถึงระบบคุ้มครองพยาน");
    }

    private static bool CanReviewOrganizationScope(WitnessUserContext user)
        => user.OrganizationId.HasValue
           && (user.HasPermission(WitnessPermissions.SupervisorReview)
               || user.HasPermission(WitnessPermissions.DirectorReview)
               || user.HasPermission(WitnessPermissions.AssignmentManage));

    private static async Task EnsureCaseMutationAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        CaseRow caseRow,
        WitnessUserContext user,
        CancellationToken ct)
    {
        if (caseRow.CreatedBy == user.UserId
            || caseRow.CurrentOwnerUserId == user.UserId
            || (caseRow.CurrentOwnerRole == "external_module"
                && user.HasPermission(WitnessPermissions.ExternalReceive)))
            return;

        await using var cmd = new NpgsqlCommand("""
            SELECT
                EXISTS(
                    SELECT 1 FROM witness.case_assignments assignment
                    WHERE assignment.case_id=$1
                      AND assignment.user_id=$2
                      AND assignment.ended_at IS NULL)
                OR ($4 AND (
                    $5::uuid = $3::uuid
                    OR $6::uuid = $3::uuid))
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseRow.Id);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)user.OrganizationId ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Uuid
        });
        cmd.Parameters.AddWithValue(CanReviewOrganizationScope(user));
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)caseRow.OwningOrgId ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Uuid
        });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)caseRow.CurrentOwnerOrgId ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Uuid
        });
        if (await cmd.ExecuteScalarAsync(ct) is not true)
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์เข้าถึงหรือแก้ไขแฟ้มคำร้องนี้");
    }

    private static bool CanManageAttachments(WitnessUserContext user)
        => user.HasPermission(WitnessPermissions.Create)
           || user.HasPermission(WitnessPermissions.OfficerReview)
           || user.HasPermission(WitnessPermissions.SupervisorReview)
           || user.HasPermission(WitnessPermissions.DirectorReview)
           || user.HasPermission(WitnessPermissions.NoticeManage)
           || user.HasPermission(WitnessPermissions.ProtectionManage)
           || user.HasPermission(WitnessPermissions.AppealManage)
           || user.HasPermission(WitnessPermissions.ExternalReceive);

    private static void EnsureAppealReadPermission(WitnessUserContext user)
    {
        if (!user.HasExplicitPermission(WitnessPermissions.AppealManage)
            && !user.HasExplicitPermission(WitnessPermissions.ExternalReceive))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์เปิดเอกสารของคำอุทธรณ์");
    }

    private static void EnsureAppealAttachmentWritePermission(
        WitnessUserContext user,
        string? evidenceType)
    {
        if (evidenceType == AppealExternalResult)
        {
            if (!user.HasExplicitPermission(WitnessPermissions.ExternalReceive))
                throw new WitnessAuthorizationException("ไม่มีสิทธิ์แนบเอกสารผลจาก External Module");
            return;
        }

        if (!user.HasExplicitPermission(WitnessPermissions.AppealManage))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์เพิ่มหรือแก้ไขหลักฐานคำอุทธรณ์");
    }

    private static string? NormalizeEvidenceType(string? evidenceType)
    {
        var normalized = Normalize(evidenceType)?.ToLowerInvariant();
        if (normalized is null)
            return null;
        if (normalized is not (AppealNewEvidence or AppealLateFilingReason or AppealExternalResult
            or AppealResultNoticeProof))
            throw new WitnessWorkflowException("ประเภทหลักฐานอุทธรณ์ไม่ถูกต้อง");
        return normalized;
    }

    private static void ValidateAppealAttachmentState(
        string caseStatus,
        string appealStatus,
        string evidenceType)
    {
        if (evidenceType == AppealExternalResult)
        {
            if (caseStatus != WitnessStatuses.AppealExternalPending || appealStatus != "submitted")
                throw new WitnessWorkflowException("แนบผลจาก External Module ได้เฉพาะคำอุทธรณ์ที่ส่งพิจารณาแล้ว");
            return;
        }

        if (evidenceType == AppealResultNoticeProof)
        {
            if (appealStatus != "decided"
                || caseStatus is not (WitnessStatuses.AppealDecided
                    or WitnessStatuses.ApprovedPendingNotice
                    or WitnessStatuses.ProtectionActive
                    or WitnessStatuses.AppealResultNoticeSent))
                throw new WitnessWorkflowException("แนบหลักฐานแจ้งผลได้เฉพาะคำอุทธรณ์ที่มีผลชี้ขาดและอยู่ระหว่างแจ้งผล");
            return;
        }

        if (caseStatus != WitnessStatuses.AppealReceived || appealStatus != "received")
            throw new WitnessWorkflowException("เพิ่มหรือลบหลักฐานได้เฉพาะคำอุทธรณ์ที่อยู่ระหว่างรับข้อมูลหรือแก้ไข");
    }

    private async Task<bool> CanViewSafeHouseAsync(
        Guid caseId,
        WitnessUserContext user,
        CancellationToken ct)
    {
        if (user.IsGlobalAdministrator)
            return true;
        if (!user.HasPermission(WitnessPermissions.ViewSafeHouse))
            return false;
        await using var cmd = dataSource.CreateCommand("""
            SELECT EXISTS(
                SELECT 1
                FROM witness.case_secret_grants grant_row
                WHERE grant_row.case_id=$1
                  AND grant_row.user_id=$2
                  AND grant_row.data_scope='safe_house'
                  AND grant_row.revoked_at IS NULL
                  AND grant_row.valid_from <= NOW()
                  AND (grant_row.valid_to IS NULL OR grant_row.valid_to > NOW()))
            """);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(user.UserId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static IReadOnlyDictionary<string, string> MaskFormValues(
        int formNumber,
        IReadOnlyDictionary<string, string> values,
        WitnessUserContext user,
        bool safeHouseVisible)
    {
        if ((user.IsGlobalAdministrator || user.HasPermission(WitnessPermissions.ViewPii))
            && safeHouseVisible)
            return values;

        var sensitiveKeys = WitnessProtectionFormCatalog.All
            .FirstOrDefault(form => form.Number == formNumber)?
            .Sections.SelectMany(section => section.Fields)
            .Where(field => field.Sensitive)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var safeHouseKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "safe_house", "safe_house_address", "safe_house_location", "protection_area",
            "handover_place", "operational_route", "vehicle_plate", "vehicle_details"
        };
        var result = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        foreach (var key in result.Keys.ToArray())
        {
            if ((!user.IsGlobalAdministrator
                 && !user.HasPermission(WitnessPermissions.ViewPii)
                 && sensitiveKeys.Contains(key))
                || (!safeHouseVisible && safeHouseKeys.Contains(key)))
                result[key] = "***";
        }
        return result;
    }

    private static WitnessAttachmentDto ReadAttachment(NpgsqlDataReader reader)
        => new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));

    private static WitnessAppealDto ReadAppeal(NpgsqlDataReader reader)
        => new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetBoolean(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.GetFieldValue<DateTimeOffset>(12),
            reader.GetFieldValue<DateTimeOffset>(13));

    private static WitnessAppealResultNoticeDto ReadAppealResultNotice(NpgsqlDataReader reader)
        => new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetGuid(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetFieldValue<DateTimeOffset>(16),
            reader.GetString(17),
            reader.GetFieldValue<DateTimeOffset>(18));

    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected)
            throw new WitnessConcurrencyException("แฟ้มคำร้องถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลล่าสุด");
    }

    private static string PrimaryRole(WitnessUserContext user)
        => user.Roles.OrderBy(role => role, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "authenticated_user";

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record CaseRow(
        Guid Id,
        string RequestNo,
        string Status,
        string UrgentStatus,
        long Version,
        Guid CreatedBy,
        Guid? CurrentOwnerUserId,
        Guid? OwningOrgId,
        Guid? CurrentOwnerOrgId,
        string CurrentOwnerRole);
    private sealed record FormRow(
        Guid Id, Guid CaseId, string RequestNo, int FormNumber, int Version, string Status,
        Dictionary<string, string> Values, DateTimeOffset UpdatedAt, string UpdatedBy, long CaseVersion);
    private sealed record PublicKb1ShareRow(
        Guid Id,
        Guid CaseId,
        Guid FormId,
        string Status,
        DateTimeOffset ExpiresAt,
        Guid CreatedBy,
        string CreatedByName,
        string RequestNo,
        string CaseStatus,
        long CaseVersion,
        int FormVersion,
        string FormStatus,
        Dictionary<string, string> Values);
    private sealed record AppealRow(
        Guid Id,
        Guid CaseId,
        string Status,
        long Version,
        string? ExternalReference,
        string? Decision,
        DateTimeOffset? DecidedAt);
    private sealed record AppealResultNoticeRow(
        Guid Id,
        Guid CaseId,
        Guid AppealId,
        Guid ExternalResultId,
        string ExternalReference,
        DateTimeOffset SentAt,
        DateTimeOffset? ReceivedAt,
        string DeliveryStatus,
        string CompletionStatus);
    private sealed record AppealResultCommandContext(
        AppealRow Appeal,
        AppealResultNoticeRow? Notice,
        Guid? ExternalResultId = null,
        string? ExternalReference = null,
        DateTimeOffset? EventAt = null,
        Guid? ProofAttachmentId = null,
        Guid? ReceiptProofAttachmentId = null,
        string? Recipient = null,
        string? DeliveryChannel = null,
        string? ActualRecipient = null,
        string? ReceiptNote = null,
        string? CompletionStatus = null);
    private sealed record IdempotencyReservation(
        Guid Id,
        bool IsReplay,
        string? ResponseBody,
        int? ResponseStatus,
        Guid? ResourceId);
    private sealed record NoticeReceiptTarget(
        Guid DeliveryId,
        int FormNumber,
        DateTimeOffset SentAt);
}

public sealed record AttachmentContent(string FileName, string ContentType, byte[] Content);
