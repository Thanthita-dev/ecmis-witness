ALTER TABLE witness.case_assignments
    ADD COLUMN IF NOT EXISTS target_username varchar(150) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS organization_name varchar(500) NOT NULL DEFAULT '';

UPDATE witness.case_assignments
SET target_username = user_id::text
WHERE target_username = '';

INSERT INTO witness.schema_migrations(version)
VALUES ('009_case_assignment_identity')
ON CONFLICT (version) DO NOTHING;
