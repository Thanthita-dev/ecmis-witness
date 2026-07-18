CREATE TABLE IF NOT EXISTS witness.notifications (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    alert_type varchar(80) NOT NULL,
    source_reference varchar(160) NOT NULL,
    due_at timestamptz NULL,
    severity varchar(20) NOT NULL,
    title varchar(300) NOT NULL,
    message text NOT NULL,
    status varchar(20) NOT NULL DEFAULT 'unread',
    dedupe_key varchar(300) NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    updated_at timestamptz NOT NULL DEFAULT NOW(),
    acknowledged_by uuid NULL,
    acknowledged_at timestamptz NULL
);

CREATE INDEX IF NOT EXISTS ix_witness_notifications_case_status
ON witness.notifications(case_id, status, due_at);

INSERT INTO witness.schema_migrations(version)
VALUES ('006_notifications')
ON CONFLICT (version) DO NOTHING;
