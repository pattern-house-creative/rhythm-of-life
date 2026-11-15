-- The ambient screen's tall left corner is "main": it shows a dashboard's tall card, else the glucose tile once a
-- glucose source has data, else a short hint. It was called "glucose"; a card chosen for it keeps its place.
ALTER TABLE ambient_cards DROP CONSTRAINT ambient_cards_corner_check;
UPDATE ambient_cards SET corner='main' WHERE corner='glucose';
ALTER TABLE ambient_cards ADD CONSTRAINT ambient_cards_corner_check CHECK(corner IN ('main','recovery','training','changed'));

CREATE OR REPLACE FUNCTION set_ambient_card(p_principal uuid,p_corner text,p_dashboard uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Choosing corner cards');
 IF p_corner NOT IN ('main','recovery','training','changed') THEN RAISE EXCEPTION 'Unknown corner' USING ERRCODE='22023'; END IF;
 IF p_dashboard IS NULL THEN DELETE FROM ambient_cards WHERE corner=p_corner;
 ELSE
   IF NOT EXISTS(SELECT 1 FROM dashboards WHERE id=p_dashboard) THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
   INSERT INTO ambient_cards(corner,dashboard_id,set_by) VALUES(p_corner,p_dashboard,p_principal)
   ON CONFLICT(corner) DO UPDATE SET dashboard_id=excluded.dashboard_id,set_by=excluded.set_by,set_at=now();
 END IF;
 RETURN read_ambient_cards(p_principal);
END $$;
