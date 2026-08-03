-- ผูกหลักฐานภาพลายมือชื่อกับลายมือชื่ออิเล็กทรอนิกส์โดยตรง
-- คอลัมน์เป็น nullable เพื่อรักษาข้อมูลและ client เดิมที่ใช้หลักฐานอ้างอิงชนิดอื่น

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'uq_witness_attachments_id_case'
          AND conrelid = 'witness.attachments'::regclass
    ) THEN
        ALTER TABLE witness.attachments
            ADD CONSTRAINT uq_witness_attachments_id_case UNIQUE (id, case_id);
    END IF;
END $$;

ALTER TABLE witness.form_signatures
    ADD COLUMN IF NOT EXISTS case_id uuid NULL,
    ADD COLUMN IF NOT EXISTS evidence_attachment_id uuid NULL;

UPDATE witness.form_signatures signature
SET case_id = form_row.case_id
FROM witness.forms form_row
WHERE signature.form_id = form_row.id
  AND signature.case_id IS NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_witness_form_signatures_case'
          AND conrelid = 'witness.form_signatures'::regclass
    ) THEN
        ALTER TABLE witness.form_signatures
            ADD CONSTRAINT fk_witness_form_signatures_case
            FOREIGN KEY (case_id) REFERENCES witness.cases(id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_witness_form_signatures_evidence_case'
          AND conrelid = 'witness.form_signatures'::regclass
    ) THEN
        ALTER TABLE witness.form_signatures
            ADD CONSTRAINT fk_witness_form_signatures_evidence_case
            FOREIGN KEY (evidence_attachment_id, case_id)
            REFERENCES witness.attachments(id, case_id);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_witness_form_signatures_evidence_case'
          AND conrelid = 'witness.form_signatures'::regclass
    ) THEN
        ALTER TABLE witness.form_signatures
            ADD CONSTRAINT ck_witness_form_signatures_evidence_case
            CHECK (evidence_attachment_id IS NULL OR case_id IS NOT NULL);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_witness_form_signatures_evidence_attachment
    ON witness.form_signatures(evidence_attachment_id)
    WHERE evidence_attachment_id IS NOT NULL;

INSERT INTO witness.schema_migrations(version)
VALUES ('018_kb1_public_signature_evidence')
ON CONFLICT (version) DO NOTHING;
