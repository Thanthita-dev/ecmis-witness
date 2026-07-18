CREATE TABLE IF NOT EXISTS witness.case_links (
    witness_case_id uuid PRIMARY KEY REFERENCES witness.cases(id) ON DELETE CASCADE,
    complaint_id bigint NOT NULL,
    complaint_case_no varchar(100) NOT NULL,
    track_no varchar(100) NOT NULL,
    pcms_no varchar(100) NULL,
    investigation_no varchar(100) NULL,
    accused_display_name varchar(500) NULL,
    accused_agency varchar(500) NULL,
    relationship_reason varchar(3000) NOT NULL,
    risk_level varchar(40) NOT NULL,
    linked_by uuid NOT NULL,
    linked_by_name varchar(250) NOT NULL,
    linked_at timestamptz NOT NULL DEFAULT NOW(),
    updated_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_witness_case_links_complaint
ON witness.case_links(complaint_id);

INSERT INTO witness.schema_migrations(version)
VALUES ('004_case_link')
ON CONFLICT (version) DO NOTHING;
