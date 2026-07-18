ALTER TABLE witness.form_signatures
ADD COLUMN IF NOT EXISTS signer_purpose varchar(250) NOT NULL DEFAULT 'ผู้ลงนาม';

CREATE INDEX IF NOT EXISTS idx_witness_signature_purpose
ON witness.form_signatures(form_id, form_version, signer_purpose);

INSERT INTO witness.schema_migrations(version)
VALUES ('003_signature_purpose')
ON CONFLICT (version) DO NOTHING;
