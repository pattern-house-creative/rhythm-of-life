-- Deleting a dashboard, and the stars pinned to the sky. A harness login deletes a dashboard once the person has said yes in
-- the chat, naming it by its current title so the wrong one is never removed; its versions, history of writes and
-- corner cards go with it, and log entries it wrote stay (they are the household's data). Pinned stars are kept here, so every
-- screen shows them; only the app's login pins. The engine's writer login receives EXECUTE on the entry
-- points and no table privileges.
CREATE TABLE sky_pins(
 graph_id text PRIMARY KEY CHECK(length(graph_id) BETWEEN 1 AND 1000),
 pinned_by uuid NOT NULL REFERENCES principals,
 pinned_at timestamptz NOT NULL DEFAULT now()
);
REVOKE ALL ON sky_pins FROM PUBLIC;

CREATE FUNCTION delete_dashboard(p_principal uuid,p_dashboard uuid,p_title text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE named text; versions int; writes int; cards int;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Deleting dashboards');
 PERFORM 1 FROM dashboards WHERE id=p_dashboard FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
 SELECT title INTO named FROM dashboard_versions WHERE dashboard_id=p_dashboard ORDER BY version DESC LIMIT 1;
 IF named IS DISTINCT FROM p_title THEN RAISE EXCEPTION 'The title does not match the dashboard' USING ERRCODE='22023'; END IF;
 SELECT count(*) INTO versions FROM dashboard_versions WHERE dashboard_id=p_dashboard;
 SELECT count(*) INTO writes FROM dashboard_writes WHERE dashboard_id=p_dashboard;
 SELECT count(*) INTO cards FROM ambient_cards WHERE dashboard_id=p_dashboard;
 DELETE FROM dashboards WHERE id=p_dashboard;
 RETURN jsonb_build_object('id',p_dashboard,'title',named,'versions',versions,'writes',writes,'corners',cards);
END $$;

-- Pins or unpins one star, at most 24 at once; answers every pinned star, oldest first.
CREATE FUNCTION set_sky_pin(p_principal uuid,p_star text,p_pinned boolean) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Pinning stars');
 IF p_star IS NULL OR length(p_star) NOT BETWEEN 1 AND 1000 THEN RAISE EXCEPTION 'A pin names a star' USING ERRCODE='22023'; END IF;
 IF NOT p_pinned THEN DELETE FROM sky_pins WHERE graph_id=p_star;
 ELSIF NOT EXISTS(SELECT 1 FROM sky_pins WHERE graph_id=p_star) THEN
   IF (SELECT count(*) FROM sky_pins)>=24 THEN RAISE EXCEPTION 'At most 24 stars are pinned' USING ERRCODE='22023'; END IF;
   INSERT INTO sky_pins(graph_id,pinned_by) VALUES(p_star,p_principal);
 END IF;
 RETURN read_sky_pins(p_principal);
END $$;
CREATE FUNCTION read_sky_pins(p_principal uuid) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading pins');
 RETURN (SELECT coalesce(jsonb_agg(graph_id ORDER BY pinned_at,graph_id),'[]'::jsonb) FROM sky_pins);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION delete_dashboard(uuid,uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION set_sky_pin(uuid,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_sky_pins(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION delete_dashboard(uuid,uuid,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION set_sky_pin(uuid,text,boolean) FROM PUBLIC;
REVOKE ALL ON FUNCTION read_sky_pins(uuid) FROM PUBLIC;
