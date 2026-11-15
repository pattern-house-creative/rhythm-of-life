-- Viewer sessions in the app (SB-P1-11; docs/dev/memory.md section 3, decisions.md D-046 and D-070): a one-time code
-- the operator makes on the host for one person lets the app's server register one credential for one browser
-- session. The credential is an app principal bound to that person (023 allows it, D-070), so with memory.scopes on the
-- session reads that person's private records and Family, and the engine reads the person from the credential alone:
-- the shared app token never names a person. Suspending or removing the person disables it like any of their tokens
-- (Principals.Suspend, Principals.Install), and the door refuses it (401) whatever the app's server still holds.
--
-- Nothing writes these rows while app.viewers is off, every install's setting and the only one this version accepts
-- (ViewerSettings): rhythm viewer invite refuses, and the engine lists and accepts no app_viewer tool. The code itself
-- is shown once to the operator and never kept: only its SHA-256, as for every token. A code works once, within its
-- hour at most; a used one keeps the principal it made, so `rhythm viewer list` can say which code made which session.
-- A restore's quarantine disables every principal and removes every code not used yet
-- (engine/backup/restore-quarantine.sql), so a restore brings back no session and no code.
CREATE TABLE viewer_codes(
 code_hash text PRIMARY KEY,
 person_id text NOT NULL,
 person_kind text NOT NULL DEFAULT 'person' CHECK(person_kind='person'),
 created_at timestamptz NOT NULL DEFAULT now(),
 expires_at timestamptz NOT NULL,
 used_at timestamptz,
 principal_id uuid UNIQUE REFERENCES principals(id),
 FOREIGN KEY(person_id,person_kind) REFERENCES subjects(id,kind),
 CHECK(expires_at > created_at AND expires_at <= created_at + interval '1 hour'),
 CHECK((used_at IS NULL) = (principal_id IS NULL))
);
CREATE INDEX viewer_codes_person ON viewer_codes(person_id);
REVOKE ALL ON viewer_codes FROM PUBLIC;
