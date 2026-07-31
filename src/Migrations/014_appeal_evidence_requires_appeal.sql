DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_witness_attachments_evidence_requires_appeal'
          AND conrelid = 'witness.attachments'::regclass
    ) THEN
        ALTER TABLE witness.attachments
            ADD CONSTRAINT ck_witness_attachments_evidence_requires_appeal
            CHECK (evidence_type IS NULL OR appeal_id IS NOT NULL);
    END IF;
END $$;

INSERT INTO witness.schema_migrations(version)
VALUES ('014_appeal_evidence_requires_appeal')
ON CONFLICT (version) DO NOTHING;
