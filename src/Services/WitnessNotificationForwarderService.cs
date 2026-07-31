using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace EcmisWitness.Api.Services;

public sealed class WitnessNotificationForwarderService(
    NpgsqlDataSource dataSource,
    HttpClient notificationApi,
    IConfiguration configuration,
    ILogger<WitnessNotificationForwarderService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsConfigured())
        {
            logger.LogWarning("Notification Center forwarding is disabled because BaseUrl or PublisherApiKey is missing");
            return;
        }

        var intervalSeconds = Math.Clamp(
            configuration.GetValue("Notification:ForwardIntervalSeconds", 30), 10, 600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        await ForwardPendingAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await ForwardPendingAsync(stoppingToken);
    }

    private bool IsConfigured() =>
        notificationApi.BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(configuration["Notification:PublisherApiKey"]);

    private async Task ForwardPendingAsync(CancellationToken ct)
    {
        try
        {
            var pending = await LoadPendingAsync(ct);
            foreach (var item in pending)
            {
                ct.ThrowIfCancellationRequested();
                await ForwardOneAsync(item, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Witness Notification Center forwarding scan failed");
        }
    }

    private async Task<IReadOnlyList<PendingNotification>> LoadPendingAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT notification.id, notification.case_id, witness_case.request_no,
                   notification.alert_type, notification.source_reference,
                   notification.due_at, notification.severity, notification.title,
                   notification.message,
                   COALESCE(witness_case.current_owner_org_id, witness_case.owning_org_id),
                   ARRAY(
                       SELECT DISTINCT candidate.user_id
                       FROM (
                           SELECT witness_case.created_by AS user_id
                           UNION ALL SELECT witness_case.current_owner_user_id
                           UNION ALL
                           SELECT assignment.user_id
                           FROM witness.case_assignments assignment
                           WHERE assignment.case_id=witness_case.id
                             AND assignment.ended_at IS NULL
                       ) candidate
                       WHERE candidate.user_id IS NOT NULL),
                   notification.central_forward_attempts
            FROM witness.notifications notification
            JOIN witness.cases witness_case ON witness_case.id=notification.case_id
            WHERE notification.central_forwarded_at IS NULL
              AND notification.status <> 'acknowledged'
              AND (notification.due_at IS NULL OR notification.due_at >= TIMESTAMPTZ '1900-01-01 00:00:00+00')
            ORDER BY notification.created_at
            LIMIT 50
            """);
        var results = new List<PendingNotification>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new PendingNotification(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.GetFieldValue<Guid[]>(10), reader.GetInt32(11)));
        }
        return results;
    }

    private async Task ForwardOneAsync(PendingNotification item, CancellationToken ct)
    {
        var request = new PublishNotificationRequest(
            "witness",
            "ระบบคุ้มครองพยาน",
            "witness-case",
            item.CaseId.ToString(),
            item.RequestNo,
            item.AlertType,
            item.Severity,
            item.Title,
            item.Message,
            $"/witness-protect/case/{item.CaseId}",
            item.DueAt,
            item.OrganizationId,
            item.TargetUserIds,
            RequiredPermissions(item.AlertType),
            false,
            item.Id.ToString());

        using var message = new HttpRequestMessage(HttpMethod.Post, "api/v1/notifications/publish")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        message.Headers.TryAddWithoutValidation(
            "X-Notification-Api-Key", configuration["Notification:PublisherApiKey"]);

        try
        {
            using var response = await notificationApi.SendAsync(message, ct);
            var body = await response.Content.ReadFromJsonAsync<ApiEnvelope<NotificationItem>>(JsonOptions, ct);
            if (!response.IsSuccessStatusCode || body?.Success != true || body.Data is null)
            {
                await RecordFailureAsync(item.Id,
                    body?.Error ?? body?.Message ?? $"Notification API ตอบกลับ {(int)response.StatusCode}", ct);
                return;
            }
            await MarkForwardedAsync(item.Id, body.Data.Id, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            await RecordFailureAsync(item.Id, "ไม่สามารถส่งแจ้งเตือนไป Notification Center ได้", ct);
            logger.LogWarning(ex, "Unable to forward witness notification {NotificationId}", item.Id);
        }
    }

    private async Task MarkForwardedAsync(Guid localId, Guid centralId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE witness.notifications
            SET central_notification_id=$2, central_forwarded_at=NOW(),
                central_forward_attempts=central_forward_attempts+1,
                central_forward_error=NULL, updated_at=NOW()
            WHERE id=$1 AND central_forwarded_at IS NULL
            """);
        command.Parameters.AddWithValue(localId);
        command.Parameters.AddWithValue(centralId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task RecordFailureAsync(Guid localId, string error, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE witness.notifications
            SET central_forward_attempts=central_forward_attempts+1,
                central_forward_error=left($2,1000), updated_at=NOW()
            WHERE id=$1 AND central_forwarded_at IS NULL
            """);
        command.Parameters.AddWithValue(localId);
        command.Parameters.AddWithValue(error);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string[] RequiredPermissions(string alertType) => alertType switch
    {
        "appeal-deadline" => ["witness.appeal.manage", "witness.notice.manage"],
        "protection-expiry" => ["witness.protection.manage", "witness.review.supervisor", "witness.review.director"],
        "important-report" => ["witness.protection.manage", "witness.review.director"],
        _ => []
    };

    private sealed record PendingNotification(
        Guid Id,
        Guid CaseId,
        string RequestNo,
        string AlertType,
        string SourceReference,
        DateTimeOffset? DueAt,
        string Severity,
        string Title,
        string Message,
        Guid? OrganizationId,
        Guid[] TargetUserIds,
        int ForwardAttempts);

    private sealed record PublishNotificationRequest(
        string SourceModule,
        string SourceModuleLabel,
        string SourceEntityType,
        string SourceEntityId,
        string SourceReference,
        string NotificationType,
        string Severity,
        string Title,
        string Message,
        string ActionUrl,
        DateTimeOffset? DueAt,
        Guid? OrganizationId,
        IReadOnlyList<Guid> TargetUserIds,
        IReadOnlyList<string> RequiredPermissions,
        bool Broadcast,
        string DedupeKey);

    private sealed record ApiEnvelope<T>(bool Success, T? Data, string? Message, string? Error);
    private sealed record NotificationItem(Guid Id);
}
