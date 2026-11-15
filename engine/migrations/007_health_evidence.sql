-- Derived, revision-bound projections of already authorized originals. Never an access grant.
CREATE TABLE source_payloads (
 resource_id uuid PRIMARY KEY REFERENCES resources ON DELETE CASCADE,
 checksum text NOT NULL, format text NOT NULL, adapter_version integer NOT NULL,
 payload jsonb NOT NULL, fields jsonb NOT NULL, projected_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX measurements_resource_date ON measurements(resource_id,local_date);
