CREATE TABLE subjects(id text PRIMARY KEY, kind text NOT NULL CHECK(kind IN ('person','household','synthetic')));
INSERT INTO subjects VALUES ('household','household'),('fixture','synthetic');
CREATE TABLE principals(id uuid PRIMARY KEY, name text NOT NULL, token_hash text UNIQUE NOT NULL, enabled boolean NOT NULL DEFAULT true);
CREATE TABLE grants(principal_id uuid REFERENCES principals ON DELETE CASCADE, subject_id text REFERENCES subjects, provider text NOT NULL, scope text NOT NULL CHECK(scope IN ('shared','private')), PRIMARY KEY(principal_id,subject_id,provider,scope));
CREATE TABLE resources(
 id uuid PRIMARY KEY, subject_id text NOT NULL REFERENCES subjects, provider text NOT NULL,
 external_id text NOT NULL, kind text NOT NULL, scope text NOT NULL CHECK(scope IN ('shared','private')),
 title text NOT NULL, source_url text, content text NOT NULL DEFAULT '', revision text, checksum text,
 source_updated_at timestamptz, indexed_at timestamptz NOT NULL DEFAULT now(),
 authorized_until timestamptz, status text NOT NULL CHECK(status IN ('current','missing','revoked','deleted')),
 local_date date, timezone text, raw_path text, metadata jsonb NOT NULL DEFAULT '{}',
 UNIQUE(subject_id,provider,external_id)
);
CREATE INDEX resources_search ON resources USING gin(to_tsvector('simple',title || ' ' || content));
CREATE INDEX resources_owner ON resources(subject_id,provider,status);
CREATE TABLE resource_versions(resource_id uuid REFERENCES resources ON DELETE CASCADE, checksum text, raw_path text NOT NULL, fetched_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(resource_id,checksum));
CREATE TABLE relationships(source_id uuid REFERENCES resources ON DELETE CASCADE,target_id uuid REFERENCES resources ON DELETE CASCADE,type text NOT NULL,
 verification text NOT NULL CHECK(verification IN ('verified','suggested','inferred')), evidence text NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(source_id,target_id,type));
CREATE TABLE measurements(resource_id uuid REFERENCES resources ON DELETE CASCADE,metric text NOT NULL,sample_key text NOT NULL,
 observed_at timestamptz, local_date date NOT NULL, value double precision NOT NULL, unit text NOT NULL,
 device_id text, quality text NOT NULL, PRIMARY KEY(resource_id,metric,sample_key));
CREATE INDEX measurements_range ON measurements(metric,local_date);
CREATE TABLE sync_jobs(id uuid PRIMARY KEY,subject_id text NOT NULL REFERENCES subjects,provider text NOT NULL,
 from_date date NOT NULL,until_date date NOT NULL,next_date date NOT NULL,status text NOT NULL DEFAULT 'pending',
 attempts integer NOT NULL DEFAULT 0,last_error text,updated_at timestamptz NOT NULL DEFAULT now(),CHECK(until_date>=from_date));
CREATE TABLE sync_events(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,job_id uuid REFERENCES sync_jobs,event text NOT NULL,at timestamptz NOT NULL DEFAULT now());
