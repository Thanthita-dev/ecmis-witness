CREATE TABLE IF NOT EXISTS witness.idempotency_records (
    id uuid PRIMARY KEY,
    actor_user_id uuid NOT NULL,
    resource_scope varchar(200) NOT NULL,
    idempotency_key varchar(100) NOT NULL,
    operation varchar(100) NOT NULL,
    request_hash char(64) NOT NULL,
    status varchar(20) NOT NULL CHECK (status IN ('processing', 'completed', 'legacy')),
    resource_id uuid NULL REFERENCES witness.cases(id) ON DELETE CASCADE,
    response_status integer NULL CHECK (response_status BETWEEN 100 AND 599),
    response_body jsonb NULL,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    completed_at timestamptz NULL,
    CONSTRAINT uq_witness_idempotency_actor_scope_key
        UNIQUE(actor_user_id, resource_scope, idempotency_key)
);

CREATE INDEX IF NOT EXISTS idx_witness_idempotency_resource
ON witness.idempotency_records(resource_id)
WHERE resource_id IS NOT NULL;

-- เดิม workflow_events ผูก key กับ case อย่างเดียว ทำให้ผู้ใช้คนละคนใช้ key
-- เดียวกันในแฟ้มเดียวกันไม่ได้ ทั้งที่ idempotency scope ต้องแยกตาม actor
ALTER TABLE witness.workflow_events
DROP CONSTRAINT IF EXISTS workflow_events_case_id_idempotency_key_key;

CREATE UNIQUE INDEX IF NOT EXISTS uq_witness_workflow_event_actor_key
ON witness.workflow_events(case_id, actor_user_id, idempotency_key)
WHERE idempotency_key IS NOT NULL;

-- เก็บ key เดิมไว้เป็น legacy claim เพื่อให้ retry ของข้อมูลก่อน migration
-- ได้ 409 ที่ควบคุมได้แทน PostgreSQL 23505/HTTP 500 ข้อมูลเดิมไม่ถูกลบ
INSERT INTO witness.idempotency_records(
    id, actor_user_id, resource_scope, idempotency_key, operation,
    request_hash, status, resource_id, created_at)
SELECT DISTINCT ON (event.actor_user_id, scope.resource_scope, event.idempotency_key)
       event.id,
       event.actor_user_id,
       scope.resource_scope,
       event.idempotency_key,
       event.action,
       repeat('0', 64),
       'legacy',
       event.case_id,
       event.occurred_at
FROM witness.workflow_events event
CROSS JOIN LATERAL (
    SELECT CASE
        WHEN event.action IN ('create-request', 'create-draft') THEN 'witness:cases'
        ELSE 'witness:case:' || event.case_id::text
    END AS resource_scope
) scope
WHERE event.idempotency_key IS NOT NULL
  AND btrim(event.idempotency_key) <> ''
ORDER BY event.actor_user_id, scope.resource_scope, event.idempotency_key,
         event.occurred_at, event.id
ON CONFLICT (actor_user_id, resource_scope, idempotency_key) DO NOTHING;

INSERT INTO witness.schema_migrations(version)
VALUES ('012_idempotency_records')
ON CONFLICT (version) DO NOTHING;
