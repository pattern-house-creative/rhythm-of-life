-- Existing credentials have no expiry and no recorded use until they next authenticate.
ALTER TABLE principals ADD COLUMN last_used_at timestamptz, ADD COLUMN expires_at timestamptz;
