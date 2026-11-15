-- Additive foundation. Existing grants and stable resource identities are unchanged.
ALTER TABLE resources ADD COLUMN domain text NOT NULL DEFAULT 'unclassified'
 CHECK (domain ~ '^[a-z][a-z0-9-]{0,63}$');
UPDATE resources SET domain='health' WHERE provider IN ('garmin','carelink','medical');
CREATE INDEX resources_domain ON resources(domain,provider,kind,id);

-- An additional restriction, never an alternative to the existing subject/provider/scope grant.
-- No restriction row preserves legacy access. A restriction with zero readers denies everyone.
-- Only administrators manage these tables; ingestion must not remove or widen restrictions.
CREATE TABLE resource_restrictions(
 resource_id uuid PRIMARY KEY REFERENCES resources ON DELETE RESTRICT,
 reason text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE resource_readers(
 resource_id uuid REFERENCES resource_restrictions(resource_id) ON DELETE CASCADE,
 principal_id uuid REFERENCES principals ON DELETE CASCADE,
 PRIMARY KEY(resource_id,principal_id)
);

-- Derived resources (pages, future chunks) inherit EVERY ancestor's visibility.
-- Ingest may append lineage, but cannot remove it to bypass a parent restriction.
CREATE TABLE resource_dependencies(
 child_id uuid REFERENCES resources ON DELETE RESTRICT,
 parent_id uuid REFERENCES resources ON DELETE RESTRICT,
 PRIMARY KEY(child_id,parent_id), CHECK(child_id<>parent_id)
);
INSERT INTO resource_dependencies(child_id,parent_id)
 SELECT l.target_id,l.source_id FROM relationships l
 JOIN resources p ON p.id=l.source_id JOIN resources c ON c.id=l.target_id
 WHERE l.type='contains' AND l.verification='verified' AND p.provider='medical'
 AND c.provider='medical' AND p.subject_id=c.subject_id;
