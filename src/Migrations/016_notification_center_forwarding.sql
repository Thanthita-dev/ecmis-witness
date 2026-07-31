ALTER TABLE witness.notifications
    ADD COLUMN IF NOT EXISTS central_notification_id uuid NULL,
    ADD COLUMN IF NOT EXISTS central_forwarded_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS central_forward_attempts integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS central_forward_error varchar(1000) NULL;

CREATE INDEX IF NOT EXISTS ix_witness_notifications_central_pending
ON witness.notifications(created_at)
WHERE central_forwarded_at IS NULL;

INSERT INTO witness.schema_migrations(version)
VALUES ('016_notification_center_forwarding')
ON CONFLICT(version) DO NOTHING;
