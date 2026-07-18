using Npgsql;

namespace EcmisWitness.Api.Services;

public sealed class WitnessExpiryAlertService(
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    ILogger<WitnessExpiryAlertService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Clamp(
            configuration.GetValue("Witness:AlertScanIntervalSeconds", 300), 30, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        await ScanAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await ScanAsync(stoppingToken);
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("""
                INSERT INTO witness.notifications(
                    id, case_id, alert_type, source_reference, due_at, severity,
                    title, message, status, dedupe_key, created_at, updated_at)
                SELECT gen_random_uuid(), c.id, 'protection-expiry', p.id::text,
                       p.end_date::timestamp AT TIME ZONE 'Asia/Bangkok',
                       CASE WHEN p.end_date < (NOW() AT TIME ZONE 'Asia/Bangkok')::date THEN 'critical'
                            WHEN p.end_date - (NOW() AT TIME ZONE 'Asia/Bangkok')::date <= 3 THEN 'high'
                            ELSE 'warning' END,
                       'ใกล้ครบกำหนดคุ้มครอง',
                       format('แฟ้ม %s รอบที่ %s ครบกำหนดวันที่ %s (สะสม %s วัน)',
                              c.request_no, p.round_number, to_char(p.end_date, 'DD/MM/YYYY'), totals.total_days),
                       'unread', format('protection-expiry:%s:%s', c.id, p.id), NOW(), NOW()
                FROM witness.protection_periods p
                JOIN witness.cases c ON c.id=p.case_id
                JOIN LATERAL (
                    SELECT COALESCE(SUM(days),0)::int AS total_days
                    FROM witness.protection_periods total WHERE total.case_id=p.case_id
                ) totals ON true
                WHERE c.status IN ('protection_active','extension_supervisor_review','extension_director_review','extension_external_pending')
                  AND p.round_number > 0
                  AND p.end_date <= (NOW() AT TIME ZONE 'Asia/Bangkok')::date + 14
                ON CONFLICT(dedupe_key) DO UPDATE
                SET due_at=EXCLUDED.due_at, severity=EXCLUDED.severity,
                    title=EXCLUDED.title, message=EXCLUDED.message, updated_at=NOW()
                WHERE witness.notifications.status <> 'acknowledged';

                INSERT INTO witness.notifications(
                    id, case_id, alert_type, source_reference, due_at, severity,
                    title, message, status, dedupe_key, created_at, updated_at)
                SELECT gen_random_uuid(), c.id, 'appeal-deadline', c.id::text,
                       c.appeal_deadline::timestamp AT TIME ZONE 'Asia/Bangkok',
                       CASE WHEN c.appeal_deadline < (NOW() AT TIME ZONE 'Asia/Bangkok')::date THEN 'critical'
                            WHEN c.appeal_deadline - (NOW() AT TIME ZONE 'Asia/Bangkok')::date <= 1 THEN 'high'
                            ELSE 'warning' END,
                       'ใกล้ครบกำหนดอุทธรณ์',
                       format('แฟ้ม %s ครบกำหนดอุทธรณ์วันที่ %s', c.request_no, to_char(c.appeal_deadline, 'DD/MM/YYYY')),
                       'unread', format('appeal-deadline:%s:%s', c.id, c.appeal_deadline), NOW(), NOW()
                FROM witness.cases c
                WHERE c.status='appeal_window'
                  AND c.appeal_deadline IS NOT NULL
                  AND c.appeal_deadline <= (NOW() AT TIME ZONE 'Asia/Bangkok')::date + 7
                ON CONFLICT(dedupe_key) DO UPDATE
                SET due_at=EXCLUDED.due_at, severity=EXCLUDED.severity,
                    title=EXCLUDED.title, message=EXCLUDED.message, updated_at=NOW()
                WHERE witness.notifications.status <> 'acknowledged';

                INSERT INTO witness.notifications(
                    id, case_id, alert_type, source_reference, due_at, severity,
                    title, message, status, dedupe_key, created_at, updated_at)
                SELECT gen_random_uuid(), f.case_id, 'important-report', format('%s:v%s', f.id, f.version),
                       f.updated_at, 'critical', 'รายงานเหตุสำคัญจาก คบ.13',
                       format('แฟ้ม %s มีรายงานเหตุสำคัญที่ต้องติดตามทันที', c.request_no),
                       'unread', format('important-report:%s:v%s', f.id, f.version), NOW(), NOW()
                FROM witness.forms f
                JOIN witness.cases c ON c.id=f.case_id
                WHERE f.form_number=13
                  AND f.status IN ('completed','signed')
                  AND f.values_data->>'report_type'='รายงานเหตุสำคัญ/เร่งด่วน'
                ON CONFLICT(dedupe_key) DO NOTHING;
                """);
            cmd.CommandTimeout = 60;
            var affected = await cmd.ExecuteNonQueryAsync(ct);
            logger.LogDebug("Witness notification scan completed; affected rows {Affected}", affected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Witness notification scan failed; service will retry on the next interval");
        }
    }
}
