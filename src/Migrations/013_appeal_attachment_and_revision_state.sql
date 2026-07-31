ALTER TABLE witness.appeals
    ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS decided_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT NOW();

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_witness_appeals_id_case'
          AND conrelid = 'witness.appeals'::regclass
    ) THEN
        ALTER TABLE witness.appeals
            ADD CONSTRAINT uq_witness_appeals_id_case UNIQUE (id, case_id);
    END IF;
END $$;

ALTER TABLE witness.attachments
    ADD COLUMN IF NOT EXISTS appeal_id uuid NULL,
    ADD COLUMN IF NOT EXISTS evidence_type varchar(80) NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_witness_attachments_appeal_case'
          AND conrelid = 'witness.attachments'::regclass
    ) THEN
        ALTER TABLE witness.attachments
            ADD CONSTRAINT fk_witness_attachments_appeal_case
            FOREIGN KEY (appeal_id, case_id)
            REFERENCES witness.appeals(id, case_id);
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_witness_attachments_appeal_evidence_type'
          AND conrelid = 'witness.attachments'::regclass
    ) THEN
        ALTER TABLE witness.attachments
            ADD CONSTRAINT ck_witness_attachments_appeal_evidence_type
            CHECK (
                evidence_type IS NULL
                OR evidence_type IN (
                    'appeal_new_evidence',
                    'late_filing_reason',
                    'external_result'
                )
            );
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_witness_attachments_appeal_requires_type'
          AND conrelid = 'witness.attachments'::regclass
    ) THEN
        ALTER TABLE witness.attachments
            ADD CONSTRAINT ck_witness_attachments_appeal_requires_type
            CHECK (appeal_id IS NULL OR evidence_type IS NOT NULL);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_witness_attachments_appeal
    ON witness.attachments(appeal_id, uploaded_at DESC)
    WHERE appeal_id IS NOT NULL AND deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_witness_appeals_case_current
    ON witness.appeals(case_id, created_at DESC);

-- Reconcile only the legacy contradiction that has enough state evidence:
-- the case is already waiting for appeal revision, while the latest appeal was
-- incorrectly closed by the old return-for-revision branch.
UPDATE witness.appeals AS appeal
SET status = 'received',
    decision = NULL,
    decided_at = NULL,
    row_version = appeal.row_version + 1,
    updated_at = NOW()
FROM witness.cases AS witness_case
WHERE witness_case.id = appeal.case_id
  AND witness_case.status = 'appeal_received'
  AND appeal.status = 'decided'
  AND appeal.decision = 'return-for-revision';

INSERT INTO witness.schema_migrations(version)
VALUES ('013_appeal_attachment_and_revision_state')
ON CONFLICT (version) DO NOTHING;
