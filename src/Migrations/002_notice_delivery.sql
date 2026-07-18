CREATE TABLE IF NOT EXISTS witness.notice_deliveries (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    form_number integer NOT NULL CHECK (form_number IN (9, 10, 17)),
    sent_at timestamptz NOT NULL,
    delivery_channel varchar(100) NOT NULL,
    recipient varchar(500) NOT NULL,
    tracking_reference varchar(500) NULL,
    proof_attachment_id uuid NULL REFERENCES witness.attachments(id) ON DELETE SET NULL,
    received_at timestamptz NULL,
    receipt_proof_attachment_id uuid NULL REFERENCES witness.attachments(id) ON DELETE SET NULL,
    created_by uuid NOT NULL,
    created_by_name varchar(250) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_witness_notice_delivery_case
ON witness.notice_deliveries(case_id, sent_at DESC);

INSERT INTO witness.schema_migrations(version)
VALUES ('002_notice_delivery')
ON CONFLICT (version) DO NOTHING;
