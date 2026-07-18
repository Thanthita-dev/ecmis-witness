CREATE TABLE IF NOT EXISTS witness.form_opinions (
    id uuid PRIMARY KEY,
    form_id uuid NOT NULL REFERENCES witness.forms(id) ON DELETE CASCADE,
    case_id uuid NOT NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    form_number integer NOT NULL CHECK (form_number BETWEEN 1 AND 17),
    form_version integer NOT NULL,
    opinion_purpose varchar(250) NOT NULL,
    opinion_text text NOT NULL,
    actor_user_id uuid NOT NULL,
    actor_name varchar(250) NOT NULL,
    actor_position varchar(250) NOT NULL,
    actor_role varchar(100) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_witness_form_opinions_form
    ON witness.form_opinions(form_id, form_version, created_at);

CREATE INDEX IF NOT EXISTS idx_witness_form_opinions_case
    ON witness.form_opinions(case_id, form_number, created_at);

INSERT INTO witness.schema_migrations(version)
VALUES ('008_append_only_form_opinions')
ON CONFLICT (version) DO NOTHING;
