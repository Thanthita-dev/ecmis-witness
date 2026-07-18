ALTER TABLE witness.form_signatures
DROP CONSTRAINT IF EXISTS form_signatures_form_id_form_version_signer_user_id_signer__key;

-- หนึ่งฐานะผู้ลงนามมีลายมือชื่อที่มีผลได้หนึ่งรายการต่อ Revision
-- ค่าเดิม "ผู้ลงนาม" เป็นข้อมูลก่อนเริ่มแยก Purpose จึงเก็บไว้เป็นประวัติและไม่บังคับ Unique
CREATE UNIQUE INDEX IF NOT EXISTS uq_witness_form_signature_purpose_revision
ON witness.form_signatures(form_id, form_version, signer_purpose)
WHERE signer_purpose <> 'ผู้ลงนาม';

INSERT INTO witness.schema_migrations(version)
VALUES ('011_signature_purpose_uniqueness')
ON CONFLICT (version) DO NOTHING;
