CREATE TABLE IF NOT EXISTS witness.kb1_share_links (
    id uuid PRIMARY KEY,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    form_id uuid NOT NULL REFERENCES witness.forms(id) ON DELETE CASCADE,
    token_sha256 varchar(64) NOT NULL UNIQUE,
    status varchar(30) NOT NULL DEFAULT 'active'
        CHECK (status IN ('active', 'submitted', 'revoked', 'expired')),
    expires_at timestamptz NOT NULL,
    created_by uuid NOT NULL,
    created_by_name varchar(250) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    last_accessed_at timestamptz NULL,
    submitted_at timestamptz NULL,
    revoked_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    UNIQUE (id, case_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_witness_kb1_share_links_active_case
    ON witness.kb1_share_links(case_id)
    WHERE status = 'active';

CREATE INDEX IF NOT EXISTS idx_witness_kb1_share_links_case_created
    ON witness.kb1_share_links(case_id, created_at DESC);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_witness_forms_id_case'
          AND conrelid = 'witness.forms'::regclass
    ) THEN
        ALTER TABLE witness.forms
            ADD CONSTRAINT uq_witness_forms_id_case UNIQUE (id, case_id);
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_witness_kb1_share_form_case'
          AND conrelid = 'witness.kb1_share_links'::regclass
    ) THEN
        ALTER TABLE witness.kb1_share_links
            ADD CONSTRAINT fk_witness_kb1_share_form_case
            FOREIGN KEY (form_id, case_id)
            REFERENCES witness.forms(id, case_id);
    END IF;
END $$;

ALTER TABLE witness.attachments
    ADD COLUMN IF NOT EXISTS kb1_share_link_id uuid NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_witness_attachments_kb1_share_case'
          AND conrelid = 'witness.attachments'::regclass
    ) THEN
        ALTER TABLE witness.attachments
            ADD CONSTRAINT fk_witness_attachments_kb1_share_case
            FOREIGN KEY (kb1_share_link_id, case_id)
            REFERENCES witness.kb1_share_links(id, case_id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_witness_attachments_kb1_share
    ON witness.attachments(kb1_share_link_id, uploaded_at DESC)
    WHERE kb1_share_link_id IS NOT NULL AND deleted_at IS NULL;

INSERT INTO witness.schema_migrations(version)
VALUES ('017_kb1_public_share')
ON CONFLICT (version) DO NOTHING;
