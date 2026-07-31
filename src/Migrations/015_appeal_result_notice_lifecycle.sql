-- WIT-E2E-036: หนังสือแจ้งผลอุทธรณ์เป็นเอกสารคนละวัตถุประสงค์กับ
-- คบ.9/10/17 จึงไม่ใช้ witness.notice_deliveries ที่บังคับ form_number
-- และเก็บ lifecycle หลังผลชี้ขาดไว้ใน entity แยกโดยไม่กระทบข้อมูลเดิม

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'uq_witness_external_results_id_case'
          AND conrelid = 'witness.external_results'::regclass
    ) THEN
        ALTER TABLE witness.external_results
            ADD CONSTRAINT uq_witness_external_results_id_case UNIQUE (id, case_id);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'uq_witness_attachments_id_appeal_case'
          AND conrelid = 'witness.attachments'::regclass
    ) THEN
        ALTER TABLE witness.attachments
            ADD CONSTRAINT uq_witness_attachments_id_appeal_case
            UNIQUE (id, appeal_id, case_id);
    END IF;
END $$;

-- เพิ่ม taxonomy ขั้นต่ำที่จำเป็นสำหรับหลักฐานการส่งหรือรับผลอุทธรณ์
-- โดยคง OI-021 ไว้เพื่อให้ลูกค้ายืนยัน taxonomy/ช่องทางฉบับสุดท้าย
ALTER TABLE witness.attachments
    DROP CONSTRAINT IF EXISTS ck_witness_attachments_appeal_evidence_type;

ALTER TABLE witness.attachments
    ADD CONSTRAINT ck_witness_attachments_appeal_evidence_type
    CHECK (
        evidence_type IS NULL
        OR evidence_type IN (
            'appeal_new_evidence',
            'late_filing_reason',
            'external_result',
            'appeal_result_notice_proof'
        )
    );

CREATE TABLE IF NOT EXISTS witness.appeal_result_notices (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    appeal_id uuid NOT NULL,
    external_result_id uuid NOT NULL,
    external_reference varchar(500) NOT NULL,
    recipient varchar(500) NOT NULL,
    delivery_channel varchar(100) NOT NULL,
    sent_at timestamptz NOT NULL,
    proof_attachment_id uuid NOT NULL,
    received_at timestamptz NULL,
    actual_recipient varchar(500) NULL,
    receipt_note varchar(2000) NULL,
    receipt_proof_attachment_id uuid NULL,
    delivery_status varchar(30) NOT NULL DEFAULT 'sent',
    completion_status varchar(80) NOT NULL,
    correlation_reference varchar(200) NOT NULL,
    created_by uuid NOT NULL,
    created_by_name varchar(250) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    updated_by uuid NOT NULL,
    updated_by_name varchar(250) NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_witness_appeal_result_notice_appeal UNIQUE (appeal_id),
    CONSTRAINT fk_witness_appeal_result_notice_appeal_case
        FOREIGN KEY (appeal_id, case_id)
        REFERENCES witness.appeals(id, case_id),
    CONSTRAINT fk_witness_appeal_result_notice_external_case
        FOREIGN KEY (external_result_id, case_id)
        REFERENCES witness.external_results(id, case_id),
    CONSTRAINT fk_witness_appeal_result_notice_proof
        FOREIGN KEY (proof_attachment_id, appeal_id, case_id)
        REFERENCES witness.attachments(id, appeal_id, case_id),
    CONSTRAINT fk_witness_appeal_result_notice_receipt_proof
        FOREIGN KEY (receipt_proof_attachment_id, appeal_id, case_id)
        REFERENCES witness.attachments(id, appeal_id, case_id),
    CONSTRAINT ck_witness_appeal_result_notice_status
        CHECK (delivery_status IN ('sent', 'received', 'completed')),
    CONSTRAINT ck_witness_appeal_result_notice_chronology
        CHECK (received_at IS NULL OR received_at >= sent_at)
);

CREATE INDEX IF NOT EXISTS idx_witness_appeal_result_notice_case
    ON witness.appeal_result_notices(case_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_witness_appeal_result_notice_status
    ON witness.appeal_result_notices(delivery_status, updated_at DESC);

INSERT INTO witness.schema_migrations(version)
VALUES ('015_appeal_result_notice_lifecycle')
ON CONFLICT (version) DO NOTHING;
