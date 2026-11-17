-- A dashboard whose feeds read memories by filter may name no star (docs/dev/screens.md 10.12). A memories feed reads the
-- native memories its reader reads that match its filters (the app's Memories view's own, MemoryMatch), never a star, so
-- a Projects board over every project has no star to name. 013 made every version name 1 to 12 stars; now a version may
-- name none when one of its feeds is a memories feed, and 1 to 12 otherwise, as before. The engine checks the same before
-- it saves (DashboardDraft.Unanchored), and the database keeps it whatever writes a version, a restore included, which
-- copies an earlier version's feeds and stars together.
--
-- A version that names no star cites none, so with memory.scopes on it lands in the scope its logs and its earlier
-- version give it (027's derived_subject: Family when nothing narrows it); its memories feed serves each reader only the
-- memories that reader reads. Nothing else changes: no function is replaced, and every version saved before reads as it
-- did. The app shows a dashboard that names no star in its list and on a screen, and joins it to the sky only when a
-- screen is asked to open it, since the sky places a dashboard beside a star it reads.

ALTER TABLE dashboard_versions DROP CONSTRAINT dashboard_versions_stars_check;
ALTER TABLE dashboard_versions ADD CONSTRAINT dashboard_versions_stars_check CHECK(jsonb_typeof(stars)='array'
  AND jsonb_array_length(stars)<=12
  AND (jsonb_array_length(stars)>=1 OR jsonb_typeof(feeds)='array' AND feeds @> '[{"kind":"memories"}]'::jsonb));
