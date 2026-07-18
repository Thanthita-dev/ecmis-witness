CREATE SCHEMA IF NOT EXISTS witness;

CREATE TABLE IF NOT EXISTS witness.schema_migrations (
    version text PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE SEQUENCE IF NOT EXISTS witness.request_number_seq START WITH 1;

CREATE TABLE IF NOT EXISTS witness.cases (
    id uuid PRIMARY KEY,
    request_no varchar(40) NOT NULL UNIQUE,
    intake_form_number integer NOT NULL CHECK (intake_form_number IN (1, 2)),
    status varchar(80) NOT NULL,
    urgent_status varchar(80) NOT NULL DEFAULT 'none',
    current_owner_role varchar(80) NOT NULL,
    current_owner_user_id uuid NULL,
    current_owner_name varchar(250) NOT NULL DEFAULT '',
    risk_level varchar(40) NOT NULL DEFAULT 'ยังไม่ประเมิน',
    is_urgent boolean NOT NULL DEFAULT false,
    summary_data jsonb NOT NULL DEFAULT '{}'::jsonb,
    notice_received_at timestamptz NULL,
    appeal_deadline date NULL,
    row_version bigint NOT NULL DEFAULT 1,
    created_by uuid NOT NULL,
    created_by_name varchar(250) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    updated_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_witness_cases_status ON witness.cases(status);
CREATE INDEX IF NOT EXISTS idx_witness_cases_owner ON witness.cases(current_owner_role, current_owner_user_id);
CREATE INDEX IF NOT EXISTS idx_witness_cases_updated ON witness.cases(updated_at DESC);

CREATE TABLE IF NOT EXISTS witness.forms (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    form_number integer NOT NULL CHECK (form_number BETWEEN 1 AND 17),
    version integer NOT NULL DEFAULT 0,
    status varchar(30) NOT NULL DEFAULT 'draft',
    values_data jsonb NOT NULL DEFAULT '{}'::jsonb,
    updated_by uuid NOT NULL,
    updated_by_name varchar(250) NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT NOW(),
    UNIQUE(case_id, form_number)
);

CREATE TABLE IF NOT EXISTS witness.form_versions (
    id uuid PRIMARY KEY,
    form_id uuid NOT NULL REFERENCES witness.forms(id) ON DELETE CASCADE,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    form_number integer NOT NULL,
    version integer NOT NULL,
    status varchar(30) NOT NULL,
    values_data jsonb NOT NULL,
    content_sha256 varchar(64) NOT NULL,
    created_by uuid NOT NULL,
    created_by_name varchar(250) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    UNIQUE(form_id, version)
);

CREATE TABLE IF NOT EXISTS witness.form_signatures (
    id uuid PRIMARY KEY,
    form_id uuid NOT NULL REFERENCES witness.forms(id) ON DELETE CASCADE,
    form_version integer NOT NULL,
    signer_user_id uuid NOT NULL,
    signer_name varchar(250) NOT NULL,
    signer_position varchar(250) NOT NULL,
    signer_role varchar(100) NOT NULL,
    verification_method varchar(100) NOT NULL,
    evidence_reference varchar(500) NOT NULL,
    document_hash varchar(64) NOT NULL,
    delegation_reference varchar(500) NULL,
    signed_at timestamptz NOT NULL DEFAULT NOW(),
    UNIQUE(form_id, form_version, signer_user_id, signer_role)
);

CREATE TABLE IF NOT EXISTS witness.attachments (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    form_number integer NULL,
    form_version integer NULL,
    file_name varchar(500) NOT NULL,
    content_type varchar(150) NOT NULL,
    size_bytes bigint NOT NULL,
    sha256 varchar(64) NOT NULL,
    classification varchar(50) NOT NULL DEFAULT 'ลับ',
    content bytea NOT NULL,
    uploaded_by uuid NOT NULL,
    uploaded_by_name varchar(250) NOT NULL,
    uploaded_at timestamptz NOT NULL DEFAULT NOW(),
    deleted_at timestamptz NULL,
    deleted_by uuid NULL,
    deleted_reason varchar(1000) NULL
);

CREATE INDEX IF NOT EXISTS idx_witness_attachments_case ON witness.attachments(case_id, uploaded_at DESC) WHERE deleted_at IS NULL;

CREATE TABLE IF NOT EXISTS witness.workflow_events (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    action varchar(100) NOT NULL,
    from_status varchar(80) NOT NULL,
    to_status varchar(80) NOT NULL,
    actor_user_id uuid NOT NULL,
    actor_name varchar(250) NOT NULL,
    actor_role varchar(100) NOT NULL,
    reason varchar(3000) NOT NULL DEFAULT '',
    external_reference varchar(500) NULL,
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at timestamptz NOT NULL DEFAULT NOW(),
    idempotency_key varchar(100) NULL,
    UNIQUE(case_id, idempotency_key)
);

CREATE INDEX IF NOT EXISTS idx_witness_events_case ON witness.workflow_events(case_id, occurred_at);

CREATE TABLE IF NOT EXISTS witness.audit_events (
    id uuid PRIMARY KEY,
    case_id uuid NULL REFERENCES witness.cases(id) ON DELETE SET NULL,
    action varchar(100) NOT NULL,
    entity_type varchar(100) NOT NULL,
    entity_id varchar(100) NOT NULL,
    actor_user_id uuid NOT NULL,
    actor_name varchar(250) NOT NULL,
    actor_role varchar(100) NOT NULL,
    ip_address varchar(80) NOT NULL DEFAULT '',
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_witness_audit_case ON witness.audit_events(case_id, occurred_at DESC);

CREATE TABLE IF NOT EXISTS witness.external_results (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    result_type varchar(80) NOT NULL,
    reference_no varchar(200) NOT NULL,
    decision_at timestamptz NOT NULL,
    reason varchar(3000) NOT NULL,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    received_by uuid NOT NULL,
    received_by_name varchar(250) NOT NULL,
    received_at timestamptz NOT NULL DEFAULT NOW(),
    UNIQUE(case_id, result_type, reference_no)
);

CREATE TABLE IF NOT EXISTS witness.acting_authorities (
    id uuid PRIMARY KEY,
    acting_user_id uuid NOT NULL,
    acting_user_name varchar(250) NOT NULL,
    target_role varchar(100) NOT NULL,
    principal_name varchar(250) NOT NULL,
    delegation_reference varchar(500) NOT NULL,
    valid_from timestamptz NOT NULL,
    valid_to timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    revoked_at timestamptz NULL
);

CREATE TABLE IF NOT EXISTS witness.appeals (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    filed_at timestamptz NOT NULL,
    filed_channel varchar(100) NOT NULL,
    statement text NOT NULL,
    late_reason text NULL,
    is_late boolean NOT NULL,
    status varchar(80) NOT NULL,
    external_reference varchar(500) NULL,
    decision text NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS witness.protection_periods (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    round_number integer NOT NULL,
    start_date date NOT NULL,
    end_date date NOT NULL,
    days integer NOT NULL,
    source_form_number integer NOT NULL,
    external_reference varchar(500) NULL,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    UNIQUE(case_id, round_number)
);

INSERT INTO witness.schema_migrations(version)
VALUES ('001_witness_schema')
ON CONFLICT (version) DO NOTHING;
