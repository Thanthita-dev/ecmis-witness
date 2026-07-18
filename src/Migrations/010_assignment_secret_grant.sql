ALTER TABLE witness.case_secret_grants
    ADD COLUMN IF NOT EXISTS source_assignment_id uuid NULL;

CREATE INDEX IF NOT EXISTS idx_witness_secret_grants_assignment
    ON witness.case_secret_grants(source_assignment_id)
    WHERE source_assignment_id IS NOT NULL;

INSERT INTO witness.schema_migrations(version)
VALUES ('010_assignment_secret_grant')
ON CONFLICT (version) DO NOTHING;
