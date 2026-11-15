-- 018 replaced set_ambient_card with CREATE OR REPLACE, which resets a function's settings: it ran as its owner with
-- the caller's search path, so a caller's temporary table could stand in for ambient_cards. Every SECURITY DEFINER
-- function in the installation schema that has no search path of its own (set_ambient_card, and any an older install
-- kept) gets the installation schema first and temporary tables last. Extension functions are left as they are.
-- No login creates temporary tables (the engine uses none), so PUBLIC loses that right on this database; the migrate
-- service revokes it again at every run, which covers a database a restore created.
DO $$
DECLARE f regprocedure;
BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p
   WHERE p.pronamespace=current_schema()::regnamespace AND p.prosecdef
     AND NOT EXISTS(SELECT 1 FROM unnest(coalesce(p.proconfig,'{}'::text[])) c WHERE c LIKE 'search_path=%')
     AND NOT EXISTS(SELECT 1 FROM pg_depend d WHERE d.classid='pg_proc'::regclass AND d.objid=p.oid AND d.deptype='e')
 LOOP
  EXECUTE format('ALTER FUNCTION %s SET search_path TO pg_catalog,%I,pg_temp',f,current_schema());
 END LOOP;
 EXECUTE format('REVOKE TEMPORARY ON DATABASE %I FROM PUBLIC',current_database());
END $$;
