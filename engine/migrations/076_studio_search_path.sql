-- 075's three definer functions set their search path to public by name. Every other definer function here pins
-- pg_catalog first, then the schema the migration runs in, then pg_temp last (019), so that a caller's temporary
-- objects, or anything in another schema, never stand in for what the function means. A database test holds every
-- definer function to that; these three now keep to it too. Nothing else about them changes.
DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION studio_access(uuid,uuid,text,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION studio_change(uuid,uuid,text,text,uuid,integer,jsonb,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION studio_publish(uuid,uuid,text,uuid,integer,jsonb,text[],text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
