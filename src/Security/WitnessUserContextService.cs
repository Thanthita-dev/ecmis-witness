using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace EcmisWitness.Api.Security;

public static class WitnessPermissions
{
    public const string ViewMasked = "witness.view.masked";
    public const string ViewPii = "witness.view.pii";
    public const string ViewSafeHouse = "witness.view.safe_house";
    public const string Create = "witness.create";
    public const string Edit = "witness.edit";
    public const string OfficerReview = "witness.review.officer";
    public const string SupervisorReview = "witness.review.supervisor";
    public const string DirectorReview = "witness.review.director";
    public const string ExternalReceive = "witness.external.receive";
    public const string NoticeManage = "witness.notice.manage";
    public const string ProtectionManage = "witness.protection.manage";
    public const string AppealManage = "witness.appeal.manage";
    public const string DocumentDownload = "witness.document.download";
    public const string AuditView = "witness.audit.view";
    public const string AssignmentManage = "witness.assignment.manage";
}

public sealed record WitnessUserContext(
    Guid UserId,
    string Username,
    string DisplayName,
    string Position,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string> Permissions,
    Guid? OrganizationId = null,
    string OrganizationName = "",
    string OrganizationType = "")
{
    private static readonly IReadOnlySet<string> GlobalAdministratorRoles =
        new HashSet<string>(["admin", "system_admin", "super_admin", "superadmin"],
            StringComparer.OrdinalIgnoreCase);

    public bool HasPermission(string permission)
    {
        if (Permissions.Contains(permission) || Permissions.Contains("witness.*"))
            return true;

        return permission.StartsWith("witness.", StringComparison.OrdinalIgnoreCase)
               && Roles.Any(GlobalAdministratorRoles.Contains);
    }

    public bool IsGlobalAdministrator => HasPermission("witness.*");

}

public sealed record WitnessAssignmentTarget(
    Guid UserId,
    string Username,
    string Position,
    Guid OrganizationId,
    string OrganizationName,
    string OrganizationType);

public sealed class WitnessDependencyException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class WitnessUserContextService(
    NpgsqlDataSource dataSource,
    HttpClient adminApi,
    IMemoryCache cache,
    IConfiguration configuration)
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<WitnessUserContext?>>> InFlight = new();
    private readonly TimeSpan authenticationCacheDuration = TimeSpan.FromSeconds(
        Math.Clamp(configuration.GetValue("Witness:AuthCacheSeconds", 30), 5, 1800));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<WitnessAssignmentTarget> ResolveAssignmentTargetAsync(
        string username,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new WitnessDependencyException("กรุณาระบุชื่อบัญชีผู้รับมอบหมายจากกิจกรรมที่ 12");
        var identity = await ResolveActivity12IdentityAsync(username.Trim(), false, ct);
        if (!identity.OrganizationId.HasValue)
            throw new WitnessDependencyException("ผู้รับมอบหมายยังไม่มีหน่วยงานที่มีผลในกิจกรรมที่ 12");
        return new WitnessAssignmentTarget(
            identity.UserId,
            username.Trim(),
            identity.Position,
            identity.OrganizationId.Value,
            identity.OrganizationName,
            identity.OrganizationType);
    }

    public async Task<WitnessUserContext?> GetAsync(HttpContext httpContext, CancellationToken ct)
    {
        var authorization = httpContext.Request.Headers.Authorization.FirstOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(authorization))
            return null;

        var cacheKey = AuthenticationCacheKey(authorization);
        if (cache.TryGetValue<WitnessUserContext>(cacheKey, out var cachedUser))
            return cachedUser;

        var candidate = new Lazy<Task<WitnessUserContext?>>(
            () => FetchCurrentUserAsync(authorization, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var current = InFlight.GetOrAdd(cacheKey, candidate);
        try
        {
            var user = await current.Value.WaitAsync(ct);
            if (user is not null)
                cache.Set(cacheKey, user, authenticationCacheDuration);
            return user;
        }
        finally
        {
            InFlight.TryRemove(new KeyValuePair<string, Lazy<Task<WitnessUserContext?>>>(cacheKey, current));
        }
    }

    private async Task<WitnessUserContext?> FetchCurrentUserAsync(
        string authorization,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/users/me");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        HttpResponseMessage response;
        try
        {
            response = await adminApi.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new WitnessDependencyException("ไม่สามารถเชื่อมต่อระบบผู้ใช้งานส่วนกลางเพื่อตรวจสอบสิทธิ์ได้", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new WitnessDependencyException($"ระบบผู้ใช้งานส่วนกลางตอบกลับผิดพลาด ({(int)response.StatusCode})");

            AdminApiEnvelope<AdminCurrentUser>? envelope;
            try
            {
                envelope = await response.Content.ReadFromJsonAsync<AdminApiEnvelope<AdminCurrentUser>>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                throw new WitnessDependencyException("ระบบผู้ใช้งานส่วนกลางตอบข้อมูลสิทธิ์ไม่ถูกต้อง", ex);
            }

            var current = envelope?.Data;
            if (envelope?.Success != true || current is null || string.IsNullOrWhiteSpace(current.Username))
                throw new WitnessDependencyException(envelope?.Error ?? envelope?.Message ?? "ไม่พบข้อมูลสิทธิ์ผู้ใช้งาน");

            var displayName = $"{current.FirstName} {current.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = current.Username;

            var isGlobalAdministrator = (current.Permissions ?? [])
                                            .Contains("witness.*", StringComparer.OrdinalIgnoreCase)
                                        || (current.Roles ?? []).Any(GlobalRole);
            var activity12 = current.UserId.HasValue
                ? new Activity12Identity(
                    current.UserId.Value,
                    current.PositionName ?? current.UserType ?? "",
                    current.OrganizationId,
                    current.OrganizationName ?? "",
                    current.OrganizationType ?? "")
                : await ResolveActivity12IdentityAsync(current.Username, isGlobalAdministrator, ct);

            return new WitnessUserContext(
                activity12.UserId,
                current.Username,
                displayName,
                activity12.Position,
                new HashSet<string>(current.Roles ?? [], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(current.Permissions ?? [], StringComparer.OrdinalIgnoreCase),
                activity12.OrganizationId,
                activity12.OrganizationName,
                activity12.OrganizationType);
        }
    }

    private async Task<Activity12Identity> ResolveActivity12IdentityAsync(
        string username,
        bool allowNoOrganization,
        CancellationToken ct)
    {
        try
        {
            await using var schemaCommand = dataSource.CreateCommand("""
                SELECT to_regclass('public.tbl_sys_users') IS NOT NULL,
                       to_regclass('public.tbl_sys_user') IS NOT NULL
                """);
            await using var schemaReader = await schemaCommand.ExecuteReaderAsync(ct);
            if (!await schemaReader.ReadAsync(ct))
                throw new WitnessDependencyException("ไม่พบ schema ระบบกำหนดสิทธิ์ กิจกรรมที่ 12");
            var hasCurrentSchema = schemaReader.GetBoolean(0);
            var hasLegacySchema = schemaReader.GetBoolean(1);
            await schemaReader.CloseAsync();

            if (hasCurrentSchema)
                return await ResolveCurrentActivity12IdentityAsync(username, allowNoOrganization, ct);
            if (hasLegacySchema)
                return await ResolveLegacyActivity12IdentityAsync(username, allowNoOrganization, ct);
            throw new WitnessDependencyException("ไม่พบตารางผู้ใช้งานของระบบกำหนดสิทธิ์ กิจกรรมที่ 12");
        }
        catch (WitnessDependencyException)
        {
            throw;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new WitnessDependencyException(
                "ไม่สามารถอ่านขอบเขตหน่วยงานจากระบบกำหนดสิทธิ์ กิจกรรมที่ 12 ได้", ex);
        }
    }

    private async Task<Activity12Identity> ResolveCurrentActivity12IdentityAsync(
        string username,
        bool allowNoOrganization,
        CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
                SELECT u.id,
                       COALESCE(p.name_th, ''),
                       oa.org_id,
                       COALESCE(o.name_th, ''),
                       COALESCE(o.org_type::text, '')
                FROM public.tbl_sys_users u
                LEFT JOIN LATERAL (
                    SELECT x.org_id, x.position_id
                    FROM public.tbl_sys_user_org_assignments x
                    WHERE x.user_id = u.id
                      AND x.is_active = TRUE
                      AND (x.effective_date IS NULL OR x.effective_date <= CURRENT_DATE)
                      AND (x.expiry_date IS NULL OR x.expiry_date >= CURRENT_DATE)
                    ORDER BY x.effective_date DESC NULLS LAST
                    LIMIT 1
                ) oa ON TRUE
                LEFT JOIN public.tbl_sys_organizations o
                    ON o.id = oa.org_id AND o.deleted_at IS NULL AND o.is_active = TRUE
                LEFT JOIN public.tbl_sys_positions p
                    ON p.id = oa.position_id AND p.deleted_at IS NULL AND p.is_active = TRUE
                WHERE lower(u.username) = lower($1)
                  AND u.deleted_at IS NULL
                  AND u.account_status::text = 'active'
                  AND (u.locked_until IS NULL OR u.locked_until <= NOW())
                LIMIT 1
                """);
        cmd.Parameters.AddWithValue(username.Trim());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new WitnessDependencyException("ไม่พบผู้ใช้งานในระบบกำหนดสิทธิ์ กิจกรรมที่ 12");
        if (reader.IsDBNull(2) && !allowNoOrganization)
            throw new WitnessDependencyException("ผู้ใช้งานยังไม่ได้รับมอบหมายหน่วยงานในกิจกรรมที่ 12");

        return new Activity12Identity(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4));
    }

    private async Task<Activity12Identity> ResolveLegacyActivity12IdentityAsync(
        string username,
        bool allowNoOrganization,
        CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            WITH matched_user AS (
                SELECT u.tsu_id, u.tsu_username, COUNT(*) OVER () AS user_count
                FROM public.tbl_sys_user u
                WHERE lower(btrim(u.tsu_username)) = lower(btrim($1))
                  AND u.is_deleted IS FALSE
                  AND btrim(COALESCE(u.tsu_status, '')) = '1'
                  AND (u.tsu_locked_until IS NULL OR u.tsu_locked_until <= CURRENT_TIMESTAMP)
            ), assignments AS (
                SELECT u.tsu_id, u.user_count, upd.tsd_id, upd.tsp_id,
                       COUNT(upd.tsupd_id) OVER (PARTITION BY u.tsu_id) AS assignment_count
                FROM matched_user u
                LEFT JOIN public.tbl_sys_user_pos_dept upd
                  ON upd.tsu_id = u.tsu_id AND upd.is_deleted IS FALSE
            )
            SELECT a.tsu_id,
                   COALESCE(p.tsp_name, ''),
                   a.tsd_id,
                   COALESCE(d.tsd_name, ''),
                   'department'
            FROM assignments a
            LEFT JOIN public.tbl_sys_department d
              ON d.tsd_id = a.tsd_id
             AND d.is_deleted IS FALSE
             AND btrim(COALESCE(d.tsd_status, '')) = '1'
            LEFT JOIN public.tbl_sys_position p
              ON p.tsp_id = a.tsp_id
             AND p.is_deleted IS FALSE
             AND btrim(COALESCE(p.tsp_status, '')) = '1'
            WHERE a.user_count = 1
              AND (a.assignment_count = 1 OR ($2 AND a.assignment_count = 0))
              AND ($2 OR (d.tsd_id IS NOT NULL AND p.tsp_id IS NOT NULL))
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue(username.Trim());
        cmd.Parameters.AddWithValue(allowNoOrganization);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new WitnessDependencyException(
                "ข้อมูลผู้ใช้หรือหน่วยงานในกิจกรรมที่ 12 ไม่ครบหรือมีการมอบหมายซ้ำ ระบบจึงปฏิเสธการเข้าถึง");

        var activity12UserId = reader.GetInt32(0);
        var departmentId = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
        return new Activity12Identity(
            ExternalId("activity12-user", activity12UserId),
            reader.GetString(1),
            departmentId.HasValue ? ExternalId("activity12-department", departmentId.Value) : null,
            reader.GetString(3),
            reader.GetString(4));
    }

    private static bool GlobalRole(string role)
        => role is not null && role.Trim().ToLowerInvariant() is
            "admin" or "system_admin" or "super_admin" or "superadmin";

    private static Guid ExternalId(string authority, int id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{authority}:{id}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string AuthenticationCacheKey(string authorization)
        => $"witness-auth:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorization))).ToLowerInvariant()}";

    private sealed record AdminApiEnvelope<T>(bool Success, T? Data, string? Message, string? Error);

    private sealed record AdminCurrentUser(
        Guid? UserId,
        string Username,
        string? UserType,
        string? FirstName,
        string? LastName,
        string? PositionName,
        Guid? OrganizationId,
        string? OrganizationName,
        string? OrganizationType,
        string[]? Roles,
        string[]? Permissions);

    private sealed record Activity12Identity(
        Guid UserId,
        string Position,
        Guid? OrganizationId,
        string OrganizationName,
        string OrganizationType);

    public async Task<ActingAuthority?> GetActingAuthorityAsync(
        Guid userId,
        string targetRole,
        CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT principal_name, delegation_reference, valid_from, valid_to
            FROM witness.acting_authorities
            WHERE acting_user_id = $1
              AND target_role = $2
              AND revoked_at IS NULL
              AND valid_from <= NOW()
              AND (valid_to IS NULL OR valid_to >= NOW())
            ORDER BY valid_from DESC
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(targetRole);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new ActingAuthority(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
    }
}

public sealed record ActingAuthority(
    string PrincipalName,
    string DelegationReference,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo);
