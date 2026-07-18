using System.Security.Cryptography;
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
    ILogger<WitnessRepository>? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
            () => ListCoreAsync(user, query, ct),
            nameof(ListAsync),
            ct);

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

    private async Task<IReadOnlyList<WitnessCaseSummaryDto>> ListCoreAsync(
        WitnessUserContext user,
        WitnessCaseSearchQuery query,
        CancellationToken ct)
    {
        EnsureView(user);
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, request_no, intake_form_number, status, urgent_status,
                   current_owner_role, current_owner_name, risk_level, is_urgent,
                   summary_data, row_version, created_at, updated_at, appeal_deadline,
                   (SELECT MAX(period.end_date) FROM witness.protection_periods period WHERE period.case_id=witness.cases.id),
                   (SELECT COALESCE(SUM((period.end_date-period.start_date)+1),0)::int FROM witness.protection_periods period WHERE period.case_id=witness.cases.id),
                   COALESCE(owning_org_name, '')
            FROM witness.cases
            WHERE ($1::text IS NULL OR status = $1)
              AND (status <> 'intake_draft' OR created_by = $3 OR $4)
              AND (
                    $4
                    OR created_by = $3
                    OR current_owner_user_id = $3
                    OR EXISTS (
                        SELECT 1 FROM witness.case_assignments assignment
                        WHERE assignment.case_id = witness.cases.id
                          AND assignment.user_id = $3
                          AND assignment.ended_at IS NULL)
                    OR ($5 AND (
                        owning_org_id = $6::uuid
                        OR current_owner_org_id = $6::uuid))
                    OR ($7 AND current_owner_role = 'external_module')
                  )
              AND ($2::text IS NULL
                   OR request_no ILIKE '%' || $2 || '%'
                   OR summary_data->>'witness_code' ILIKE '%' || $2 || '%'
                   OR summary_data->>'witness_name' ILIKE '%' || $2 || '%'
                   OR summary_data->>'petitioner_name' ILIKE '%' || $2 || '%')
              AND ($8::int IS NULL OR intake_form_number=$8 OR EXISTS(
                    SELECT 1 FROM witness.forms searched_form
                    WHERE searched_form.case_id=witness.cases.id AND searched_form.form_number=$8))
              AND ($9::date IS NULL OR created_at >= $9::date)
              AND ($10::date IS NULL OR created_at < ($10::date + 1))
              AND ($11::boolean IS NULL OR is_urgent=$11)
              AND ($12::text IS NULL OR risk_level=$12)
              AND ($13::text IS NULL OR current_owner_name ILIKE '%' || $13 || '%')
              AND ($14::text IS NULL
                   OR summary_data->>'provisional_case_subject' ILIKE '%' || $14 || '%'
                   OR EXISTS(
                        SELECT 1 FROM witness.case_links searched_link
                        LEFT JOIN public.tbl_cmp_complaint complaint
                          ON complaint.cmp_complaint_id=searched_link.complaint_id
                        WHERE searched_link.witness_case_id=witness.cases.id
                          AND concat_ws(' ', searched_link.complaint_case_no,
                              searched_link.track_no, searched_link.pcms_no,
                              searched_link.investigation_no, complaint.case_no,
                              complaint.track_no, complaint.metadata_json->>'red_case_no')
                              ILIKE '%' || $14 || '%'))
              AND ($15::text IS NULL OR
                    ($15='within-30-days' AND status='appeal_window' AND appeal_deadline >= (NOW() AT TIME ZONE 'Asia/Bangkok')::date)
                    OR ($15='overdue' AND status='appeal_window' AND appeal_deadline < (NOW() AT TIME ZONE 'Asia/Bangkok')::date)
                    OR ($15='submitted' AND status='appeal_external_pending')
                    OR ($15='decided' AND status='appeal_decided'))
              AND ($16::date IS NULL OR EXISTS(
                    SELECT 1 FROM witness.protection_periods expiry
                    WHERE expiry.case_id=witness.cases.id AND expiry.end_date <= $16))
              AND ($17::text IS NULL OR status=$17)
              AND ($18::text IS NULL OR owning_org_name ILIKE '%' || $18 || '%'
                                      OR current_owner_org_name ILIKE '%' || $18 || '%')
            ORDER BY updated_at DESC
            """);
        cmd.Parameters.AddWithValue((object?)Normalize(query.Status) ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)Normalize(query.Search) ?? DBNull.Value);
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
        cmd.Parameters.AddWithValue((object?)Normalize(query.Owner) ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)Normalize(query.MainCase) ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)Normalize(query.AppealSla) ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)query.ProtectionExpiryBefore ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.AddWithValue((object?)Normalize(query.TransferStatus) ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)Normalize(query.Organization) ?? DBNull.Value);

        var results = new List<WitnessCaseSummaryDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadSummary(reader, user));
        return results;
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
        return new WitnessCaseDetailDto(
            summary,
            intake?.Values ?? new Dictionary<string, string>(),
            forms,
            attachments,
            events,
            availableActions,
            caseLink,
            assignments);
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

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            await using var idempotency = new NpgsqlCommand("""
                SELECT c.id
                FROM witness.workflow_events e
                JOIN witness.cases c ON c.id = e.case_id
                WHERE e.idempotency_key = $1 AND e.actor_user_id = $2
                LIMIT 1
                """, connection, tx);
            idempotency.Parameters.AddWithValue(request.IdempotencyKey.Trim());
            idempotency.Parameters.AddWithValue(user.UserId);
            var existing = await idempotency.ExecuteScalarAsync(ct);
            if (existing is Guid existingId)
            {
                await tx.CommitAsync(ct);
                return await GetDetailAsync(existingId, user, ct)
                       ?? throw new WitnessWorkflowException("ไม่พบคำร้องเดิมจาก idempotency key");
            }
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
        await InsertWorkflowEventAsync(connection, tx, caseId, request.Submit ? "create-request" : "create-draft",
            initialStatus, initialStatus, user,
            request.Submit ? "รับคำร้องและส่งเข้าคิวตรวจคำร้อง" : "บันทึกร่างคำร้อง",
            null, request.IdempotencyKey, "{}", now, ct);
        await InsertAuditAsync(connection, tx, caseId, "case.created", "case", caseId.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new { requestNo, request.FormNumber, request.IsUrgent }), now, ct);
        await tx.CommitAsync(ct);

        var canViewPii = user.HasPermission(WitnessPermissions.ViewPii);
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

        return new WitnessCaseDetailDto(
            caseSummary,
            values,
            [formSummary],
            [],
            [],
            stateMachine.GetAvailableActions(initialStatus, urgentStatus, user, completedForms),
            null,
            []);
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
        formPolicy.Validate(formNumber, request.Values, request.Complete);

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

        var formId = existing?.Id ?? Guid.NewGuid();
        var nextVersion = (existing?.Version ?? 0) + 1;
        var status = request.Complete ? "completed" : "draft";
        var values = new Dictionary<string, string>(request.Values, StringComparer.OrdinalIgnoreCase)
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
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        EnsureVersion(caseRow.Version, request.ExpectedVersion);
        var completedForms = await GetCompletedFormNumbersAsync(connection, tx, caseId, ct);
        if (action == "submit-supervisor" && !await HasMainCaseLinkAsync(connection, tx, caseId, ct))
            throw new WitnessWorkflowException("กรุณาเชื่อมโยงคดีหลัก หรือบันทึกว่าเป็นคดีใหม่ พร้อมประเมินภัยก่อนส่งผู้บังคับบัญชาชั้นต้น");
        if (action is "request-withdrawal" or "withdrawal-submit-supervisor" or "withdrawal-supervisor-forward" or "withdrawal-director-forward")
            await EnsureWithdrawalStatementAsync(connection, tx, caseId, ct);
        if (action == "close-no-appeal" && caseRow.Status == WitnessStatuses.AppealWindow)
            await EnsureAppealDeadlineElapsedAsync(connection, tx, caseId, ct);
        if (action == "request-transfer")
            await EnsureTransferEligibilityAsync(connection, tx, caseId, request.Data, ct);
        var definition = stateMachine.RequireTransition(caseRow.Status, action, user, completedForms);
        await EnsureRequiredSignaturesAsync(connection, tx, caseId, action, caseRow.Status, ct);
        if (definition.RequiresReason && string.IsNullOrWhiteSpace(request.Reason))
            throw new WitnessWorkflowException("กรุณาระบุเหตุผลประกอบการดำเนินการ");

        var nextVersion = caseRow.Version + 1;
        var now = DateTimeOffset.UtcNow;
        var ownerRole = OwnerForStatus(definition.ToStatus);
        var receivedAt = ParseNoticeReceivedAt(action, request.Data);
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
            cmd.Parameters.AddWithValue(definition.ToStatus);
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
                UPDATE witness.appeals SET status='submitted'
                WHERE id=(SELECT id FROM witness.appeals WHERE case_id=$1 ORDER BY created_at DESC LIMIT 1)
                """, connection, tx);
            appealCmd.Parameters.AddWithValue(caseId);
            await appealCmd.ExecuteNonQueryAsync(ct);
        }
        if (action == "start-protection")
            await InsertProtectionPeriodFromFormAsync(connection, tx, caseId, 11, 1, now, ct);
        if (action == "send-notice")
            await InsertNoticeDeliveryAsync(connection, tx, caseRow, request, user, now, ct);
        if (receivedAt.HasValue)
            await RecordNoticeReceiptAsync(connection, tx, caseId, receivedAt.Value, request.Data, ct);
        var dataJson = JsonSerializer.Serialize(request.Data ?? [], JsonOptions);
        await InsertWorkflowEventAsync(connection, tx, caseId, action, caseRow.Status,
            definition.ToStatus, user, request.Reason, null, request.IdempotencyKey, dataJson, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "workflow.transition", "case", caseId.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new
            {
                action,
                from = caseRow.Status,
                to = definition.ToStatus,
                request.Reason,
                request.Data
            }), now, ct);
        await tx.CommitAsync(ct);

        var available = stateMachine.GetAvailableActions(definition.ToStatus, caseRow.UrgentStatus, user, completedForms);
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
        return new WitnessCommandResultDto(caseId, caseRow.RequestNo, caseRow.Status,
            definition.ToStatus, nextVersion, available);
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

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        EnsureVersion(caseRow.Version, request.ExpectedVersion);
        await EnsureRequiredSignaturesAsync(connection, tx, caseId, "external-result", caseRow.Status, ct);
        if (caseRow.Status == WitnessStatuses.ExternalPending
            && string.Equals(request.ResultType, "approved", StringComparison.OrdinalIgnoreCase))
            await EnsureRequiredSignaturesAsync(connection, tx, caseId, "external-approved", caseRow.Status, ct);
        if (request.Data?.TryGetValue("external_attachment_id", out var externalAttachmentValue) != true
            || !Guid.TryParse(externalAttachmentValue, out var externalAttachmentId)
            || !await AttachmentExistsAsync(connection, tx, caseId, externalAttachmentId, ct))
            throw new WitnessWorkflowException("กรุณาแนบไฟล์ผลคำสั่งหรือหนังสือจาก External Module");
        var toStatus = ResolveExternalTarget(caseRow.Status, request.ResultType);
        if (caseRow.Status == WitnessStatuses.AppealExternalPending
            && request.ResultType.Trim().ToLowerInvariant() is "appeal-reversed" or "appeal-overturned")
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
            cmd.Parameters.AddWithValue(request.ResultType.Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue(request.ReferenceNo.Trim());
            cmd.Parameters.AddWithValue(request.DecisionAt);
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
        if (caseRow.Status == WitnessStatuses.AppealExternalPending)
        {
            await using var appealCmd = new NpgsqlCommand("""
                UPDATE witness.appeals
                SET status='decided', external_reference=$2, decision=$3
                WHERE id=(SELECT id FROM witness.appeals WHERE case_id=$1 ORDER BY created_at DESC LIMIT 1)
                """, connection, tx);
            appealCmd.Parameters.AddWithValue(caseId);
            appealCmd.Parameters.AddWithValue(request.ReferenceNo.Trim());
            appealCmd.Parameters.AddWithValue(request.ResultType.Trim().ToLowerInvariant());
            await appealCmd.ExecuteNonQueryAsync(ct);
        }
        await InsertWorkflowEventAsync(connection, tx, caseId, "external-result", caseRow.Status,
            toStatus, user, request.Reason, request.ReferenceNo, null, payloadJson, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "external.result.received", "case", caseId.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new
            {
                request.ResultType,
                request.ReferenceNo,
                request.DecisionAt,
                from = caseRow.Status,
                to = toStatus
            }), now, ct);
        await tx.CommitAsync(ct);

        return new WitnessCommandResultDto(caseId, caseRow.RequestNo, caseRow.Status, toStatus,
            nextVersion, stateMachine.GetAvailableActions(toStatus, caseRow.UrgentStatus, user));
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
    {
        if (!user.HasPermission(WitnessPermissions.Edit)
            && !user.HasPermission(WitnessPermissions.Create)
            && !user.HasPermission(WitnessPermissions.ProtectionManage)
            && !user.HasPermission(WitnessPermissions.AppealManage)
            && !user.HasPermission(WitnessPermissions.ExternalReceive))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์แนบเอกสารในแฟ้มนี้");

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO witness.attachments(
                id, case_id, form_number, form_version, file_name, content_type,
                size_bytes, sha256, content, uploaded_by, uploaded_by_name, uploaded_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(caseId);
            cmd.Parameters.AddWithValue((object?)formNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)formVersion ?? DBNull.Value);
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
            user, ipAddress, JsonSerializer.Serialize(new { fileName, contentType, size = content.LongLength, hash, formNumber, formVersion }), now, ct);
        await tx.CommitAsync(ct);
        return new WitnessAttachmentDto(id, fileName, contentType, content.LongLength, hash,
            "ลับ", formNumber, formVersion, now, user.DisplayName);
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
            SELECT file_name, content_type, content
            FROM witness.attachments
            WHERE id=$1 AND case_id=$2 AND deleted_at IS NULL
            """, connection, tx);
        cmd.Parameters.AddWithValue(attachmentId);
        cmd.Parameters.AddWithValue(caseId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var result = new AttachmentContent(reader.GetString(0), reader.GetString(1), (byte[])reader[2]);
        await reader.CloseAsync();
        await InsertAuditAsync(connection, tx, caseId, "attachment.downloaded", "attachment", attachmentId.ToString(),
            user, ipAddress, "{}", DateTimeOffset.UtcNow, ct);
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
        if (!user.HasPermission(WitnessPermissions.Edit)
            && !user.HasPermission(WitnessPermissions.ProtectionManage))
            throw new WitnessAuthorizationException("ไม่มีสิทธิ์ลบเอกสารแนบ");
        if (string.IsNullOrWhiteSpace(reason))
            throw new WitnessWorkflowException("กรุณาระบุเหตุผลการลบเอกสาร");
        var now = DateTimeOffset.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
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
            user, ipAddress, JsonSerializer.Serialize(new { reason }), now, ct);
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
        var canViewPii = user.HasPermission(WitnessPermissions.ViewPii);
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
                   form_number, form_version, uploaded_at, uploaded_by_name
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
                reader.GetFieldValue<DateTimeOffset>(8), reader.GetString(9)));
        return results;
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
        var caseRow = await LockCaseAsync(connection, tx, caseId, ct);
        await EnsureCaseMutationAccessAsync(connection, tx, caseRow, user, ct);
        EnsureVersion(caseRow.Version, request.ExpectedVersion);
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
            user, request.Reason, null, request.IdempotencyKey, dataJson, now, ct);
        await InsertAuditAsync(connection, tx, caseId, "workflow.urgent.transition", "case", caseId.ToString(),
            user, ipAddress, JsonSerializer.Serialize(new { action, fromUrgent, toUrgent, mainStatus = caseRow.Status }), now, ct);
        await tx.CommitAsync(ct);

        var available = stateMachine.GetAvailableActions(caseRow.Status, toUrgent, user, completedForms);
        return new WitnessCommandResultDto(caseId, caseRow.RequestNo, caseRow.Status,
            caseRow.Status, nextVersion, available);
    }

    private static DateTimeOffset? ParseNoticeReceivedAt(
        string action,
        IReadOnlyDictionary<string, string>? data)
    {
        if (action is not ("record-notice-receipt-approved" or "record-notice-receipt-rejected"))
            return null;
        if (data is null
            || !data.TryGetValue("received_at", out var value)
            || !DateTimeOffset.TryParse(value, out var receivedAt))
        {
            throw new WitnessWorkflowException("กรุณาระบุวันที่และเวลาที่ผู้รับได้รับหนังสือแจ้งผล");
        }
        if (receivedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new WitnessWorkflowException("วันรับหนังสือต้องไม่เป็นเวลาในอนาคต");
        return receivedAt;
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
        cmd.Parameters.AddWithValue(filedAt);
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
        cmd.Parameters.AddWithValue(sentAt);
        cmd.Parameters.AddWithValue(channel.Trim());
        cmd.Parameters.AddWithValue(recipient.Trim());
        cmd.Parameters.AddWithValue((object?)Normalize(trackingReference) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(user.DisplayName);
        cmd.Parameters.AddWithValue(now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RecordNoticeReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid caseId,
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
            WHERE id=(
                SELECT id FROM witness.notice_deliveries
                WHERE case_id=$1 ORDER BY sent_at DESC LIMIT 1
            )
            """, connection, tx);
        cmd.Parameters.AddWithValue(caseId);
        cmd.Parameters.AddWithValue(receivedAt);
        cmd.Parameters.AddWithValue(proofAttachmentId);
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
        if (!DateOnly.TryParse(values.GetValueOrDefault(startKey), out var start)
            || !DateOnly.TryParse(values.GetValueOrDefault(endKey), out var end)
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
        WitnessStatuses.AppealWindow or WitnessStatuses.AppealReceived or WitnessStatuses.AppealDecided => "appeal_officer",
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
                or WitnessStatuses.AppealExternalPending or WitnessStatuses.AppealDecided,
            _ => false
        };
        if (!allowed)
            throw new WitnessWorkflowException("ประเภทผู้รับมอบหมายไม่สัมพันธ์กับขั้นตอนปัจจุบันของแฟ้ม");
    }

    private static void EnsureView(WitnessUserContext user)
    {
        if (!user.HasPermission(WitnessPermissions.ViewMasked)
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
        if (user.IsGlobalAdministrator
            || caseRow.CreatedBy == user.UserId
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
        if (user.HasPermission(WitnessPermissions.ViewPii)
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
            if ((!user.HasPermission(WitnessPermissions.ViewPii) && sensitiveKeys.Contains(key))
                || (!safeHouseVisible && safeHouseKeys.Contains(key)))
                result[key] = "***";
        }
        return result;
    }

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
}

public sealed record AttachmentContent(string FileName, string ContentType, byte[] Content);
