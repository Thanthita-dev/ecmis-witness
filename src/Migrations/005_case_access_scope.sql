ALTER TABLE witness.cases
    ADD COLUMN IF NOT EXISTS owning_org_id uuid NULL,
    ADD COLUMN IF NOT EXISTS current_owner_org_id uuid NULL;

CREATE INDEX IF NOT EXISTS idx_witness_cases_owning_org
    ON witness.cases(owning_org_id, status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_witness_cases_current_owner_scope
    ON witness.cases(current_owner_org_id, current_owner_role, status, updated_at DESC);

CREATE TABLE IF NOT EXISTS witness.case_assignments (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    user_id uuid NOT NULL,
    assignment_role varchar(100) NOT NULL,
    org_id uuid NULL,
    source_form_number integer NULL CHECK (source_form_number IS NULL OR source_form_number BETWEEN 1 AND 17),
    reason varchar(2000) NOT NULL DEFAULT '',
    assigned_by uuid NOT NULL,
    assigned_by_name varchar(250) NOT NULL,
    assigned_at timestamptz NOT NULL DEFAULT NOW(),
    ended_at timestamptz NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_witness_case_assignments_active
    ON witness.case_assignments(case_id, user_id, assignment_role)
    WHERE ended_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_witness_case_assignments_user
    ON witness.case_assignments(user_id, case_id)
    WHERE ended_at IS NULL;

CREATE TABLE IF NOT EXISTS witness.case_secret_grants (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    user_id uuid NOT NULL,
    data_scope varchar(50) NOT NULL CHECK (data_scope IN ('pii', 'safe_house')),
    reason varchar(2000) NOT NULL,
    granted_by uuid NOT NULL,
    granted_by_name varchar(250) NOT NULL,
    valid_from timestamptz NOT NULL DEFAULT NOW(),
    valid_to timestamptz NULL,
    revoked_at timestamptz NULL
);

CREATE INDEX IF NOT EXISTS idx_witness_secret_grants_active
    ON witness.case_secret_grants(case_id, user_id, data_scope)
    WHERE revoked_at IS NULL;

-- Backfill owner organization from Activity 12 without creating a cross-service FK.
-- The Witness service remains deployable independently; Activity 12 is authoritative
-- for organization membership and updates new/active cases through the API boundary.
DO $migration$
BEGIN
    IF to_regclass('public.tbl_sys_user_org_assignments') IS NOT NULL THEN
        UPDATE witness.cases c
        SET owning_org_id = (
            SELECT oa.org_id
            FROM public.tbl_sys_user_org_assignments oa
            WHERE oa.user_id = c.created_by
              AND oa.is_active = TRUE
              AND (oa.effective_date IS NULL OR oa.effective_date <= CURRENT_DATE)
              AND (oa.expiry_date IS NULL OR oa.expiry_date >= CURRENT_DATE)
            ORDER BY oa.effective_date DESC NULLS LAST
            LIMIT 1
        ),
            current_owner_org_id = (
            SELECT oa.org_id
            FROM public.tbl_sys_user_org_assignments oa
            WHERE oa.user_id = c.created_by
              AND oa.is_active = TRUE
              AND (oa.effective_date IS NULL OR oa.effective_date <= CURRENT_DATE)
              AND (oa.expiry_date IS NULL OR oa.expiry_date >= CURRENT_DATE)
            ORDER BY oa.effective_date DESC NULLS LAST
            LIMIT 1
        )
        WHERE c.owning_org_id IS NULL
          AND EXISTS (
            SELECT 1
            FROM public.tbl_sys_user_org_assignments oa
            WHERE oa.user_id = c.created_by
              AND oa.is_active = TRUE
              AND (oa.effective_date IS NULL OR oa.effective_date <= CURRENT_DATE)
              AND (oa.expiry_date IS NULL OR oa.expiry_date >= CURRENT_DATE));
    END IF;
END
$migration$;

INSERT INTO witness.schema_migrations(version)
VALUES ('005_case_access_scope')
ON CONFLICT (version) DO NOTHING;
