ALTER TABLE witness.cases
    ADD COLUMN IF NOT EXISTS owning_org_name varchar(300) NULL,
    ADD COLUMN IF NOT EXISTS current_owner_org_name varchar(300) NULL;

CREATE INDEX IF NOT EXISTS ix_witness_cases_search_filters
ON witness.cases(status, intake_form_number, is_urgent, risk_level, created_at, updated_at);

CREATE INDEX IF NOT EXISTS ix_witness_cases_appeal_deadline
ON witness.cases(appeal_deadline) WHERE appeal_deadline IS NOT NULL;

INSERT INTO witness.schema_migrations(version)
VALUES ('007_case_search_scope')
ON CONFLICT (version) DO NOTHING;
