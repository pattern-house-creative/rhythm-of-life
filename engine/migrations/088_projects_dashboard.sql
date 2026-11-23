-- Keep the dashboard ID, layout, source stars, writes, and previous version. Upgrade only the
-- two known published page shapes. A separately customized page is never replaced by title.
ALTER TABLE dashboard_versions DROP CONSTRAINT dashboard_versions_stars_check;
ALTER TABLE dashboard_versions ADD CONSTRAINT dashboard_versions_stars_check CHECK(jsonb_typeof(stars)='array'
 AND jsonb_array_length(stars)<=12 AND (jsonb_array_length(stars)>=1
 OR feeds @> '[{"kind":"memories"}]'::jsonb OR feeds @> '[{"kind":"projects"}]'::jsonb));

DO $upgrade$
DECLARE board uuid; v dashboard_versions; page text; fingerprint text; arranged jsonb;
BEGIN
 FOR board IN SELECT id FROM dashboards ORDER BY id FOR UPDATE LOOP
  SELECT * INTO v FROM dashboard_versions WHERE dashboard_id=board ORDER BY version DESC LIMIT 1;
  IF v.title IS DISTINCT FROM 'Projects' OR v.version>=10000 OR jsonb_array_length(v.feeds)>=12
    OR v.feeds @> '[{"name":"portfolio"}]'::jsonb THEN CONTINUE; END IF;
  page:=rtrim(replace(v.html,chr(13)||chr(10),chr(10)),chr(10));
  fingerprint:=encode(sha256(convert_to(page,'UTF8')),'hex');
  IF fingerprint='660b6b804b7472cc76d8c6befaf1865aaf77a16172bc5df0d5554859efd3e95b' THEN
   page:=replace(page,$before$    const projects = new Map(
      [...allWork, ...ideas, ...records(feeds.projectGuides)]
        .filter((m) => m.project)
        .map((m) => [m.project.id, m.project.name || m.project.slug]),
    );
    if (!projects.has(project)) project = "";
$before$,$after$    const projects = new Map(
      (feeds.portfolio?.projects || []).map((p) => [p.id, p.name || p.slug]),
    );
    select.parentElement.hidden = view === "ideas";
    if (!projects.has(project)) project = "";
$after$);
   page:=replace(page,$before$      feedNotice(board, feeds.inbox);
      const list = ideas.filter(visible);
      if (!list.length)
$before$,$after$      feedNotice(board, feeds.inbox);
      const list = ideas;
      if (!list.length)
$after$);
   page:=replace(page,$before$    feedNotice(board, feeds.work);
    const work = allWork.filter(visible),
      related = new Map(allWork.map((m) => [m.id, m]));
    if (!work.length) {
$before$,$after$    feedNotice(board, feeds.work);
    feedNotice(board, feeds.portfolio);
    const work = allWork.filter((m) => projects.has(m.project?.id) && visible(m)),
      related = new Map(allWork.map((m) => [m.id, m]));
    if (!project) {
      const list = el("ul");
      for (const [id, name] of projects) {
        const item = el("li"), open = el("button", name);
        open.onclick = () => { project = id; draw(); };
        item.append(open);
        list.append(item);
      }
      board.append(list);
    }
    if (!work.length) {
$after$);
   page:=replace(page,$before$    if (!work.length) {$before$,$after$    if (project) {
      for (const overview of records(feeds.projectGuides).filter(visible))
        board.append(card(overview));
    }
    if (!work.length) {$after$);
   SELECT jsonb_agg(CASE WHEN f->>'name'='projectGuides' THEN jsonb_set(f,'{where,tag}',
     '["project-overview","portfolio-project"]'::jsonb) ELSE f END ORDER BY ord)
     INTO v.feeds FROM jsonb_array_elements(v.feeds) WITH ORDINALITY t(f,ord);
  ELSIF fingerprint='e1cce3c9663e904842ce09b3bf932996fe3b3c66fcc650bfa2e7a59441e32a2d' THEN
   page:=replace(page,$before$    if (!Array.isArray(f.memories)) return { error: "The work feed sent nothing readable." };
    const items = f.memories.filter((m) => m && m.id && m.work && typeof m.work.state === "string");
    now = Date.parse(f.asOf || (d && d.asOf)) || Date.now();
$before$,$after$    if (!Array.isArray(f.memories)) return { error: "The work feed sent nothing readable." };
    const designated = new Set((d?.feeds?.portfolio?.projects || []).map(p => p.id));
    const items = f.memories.filter(m => designated.has(m?.project?.id)).filter((m) => m && m.id && m.work && typeof m.work.state === "string");
    now = Date.parse(f.asOf || (d && d.asOf)) || Date.now();
$after$);
   page:=replace(page,$before$  function groups(items) {
    const map = new Map();
    for (const m of items) {
      const k = projKey(m);
      if (!map.has(k)) map.set(k, { key: k, project: m.project || null, name: projName(m.project), items: [] });
      map.get(k).items.push(m);
    }
    return [...map.values()];
$before$,$after$  function groups(items) {
    const map = new Map((last?.feeds?.portfolio?.projects || []).map(project => [
      project.id, {key:project.id, project, name:projName(project), items:[]}
    ]));
    for (const m of items) map.get(projKey(m))?.items.push(m);
    return [...map.values()];
$after$);
   page:=replace(page,$before$    root.className = "full";
    if (r.error) {
$before$,$after$    root.className = "full";
    if (r.error && (ui.section==="ideas" || last?.feeds?.ideaInbox)) {
      root.append(h("p","portfolio-notice",r.error));
      drawTop({items:[],byId:new Map(),blocks:new Map(),count:0,truncated:false,asOf:last.asOf});
      return;
    }
    if (r.error) {
$after$);
   page:=replace(page,$before$    }
    if (ui.project && !r.items.some((m) => projKey(m) === ui.project)) ui.project = null;
    if (ui.project) drawProject(r); else drawTop(r);
$before$,$after$    }
    if (ui.project && !groups(r.items).some(g => g.key === ui.project)) ui.project = null;
    if (ui.project) drawProject(r); else drawTop(r);
$after$);
   page:=replace(page,$before$      const k=projKey(m);
      if(k!=="none"&&!taken.has(k)&&!plannedUnique.has(k))plannedUnique.set(k,m);
    }
$before$,$after$      const k=projKey(m);
      if(taken.has(k)&&!plannedUnique.has(k))plannedUnique.set(k,m);
    }
$after$);
   page:=replace(page,$before$    const all=groups(r.items).map(g=>({...g,c:tally(split(g.items).work)}));
    const live=all.filter(g=>g.c.open>0||split(g.items).ms.some(isOpen)).sort(attention);
    const portfolio=portfolioData(last,r);
    const total=all.reduce((n,g)=>n+g.c.open,0);
    const projectsCount=live.length+portfolio.planned.length;
    root.append(add(h("header","top"),h("h1",null,"Projects"),h("p","sum",plural(projectsCount,"project")+" · "+plural(portfolio.ideas.length,"idea")+" · "+plural(total,"open work item"))));
$before$,$after$    const all=groups(r.items).map(g=>({...g,c:tally(split(g.items).work)}));
    const live=all.sort(attention);
    const portfolio=portfolioData(last,r);
    const total=all.reduce((n,g)=>n+g.c.open,0);
    const projectsCount=live.length;
    root.append(add(h("header","top"),h("h1",null,"Projects"),h("p","sum",plural(projectsCount,"project")+" · "+plural(portfolio.ideas.length,"idea")+" · "+plural(total,"open work item"))));
$after$);
   page:=replace(page,$before$    else {
      root.append(h("p","portfolio-intro","Projects with tracked work or a saved project overview. Select one to see its milestones and backlog."));
      if(!live.length&&!portfolio.planned.length)root.append(h("p","empty",all.length?"No open projects.":"No project work items yet."));
      else {
$before$,$after$    else {
      root.append(h("p","portfolio-intro","Select a project to see its milestones and backlog."));
      if(!live.length)root.append(h("p","empty","No collections are marked Show in Projects."));
      else {
$after$);
   page:=replace(page,$before$        for(const g of live)list.append(add(h("li","pcard"),...projectCard(g)));
        for(const m of portfolio.planned)list.append(drawPlanned(m));
        root.append(list);
      }
      if(!portfolio.available.portfolioProjects)root.append(h("p","portfolio-notice","Planned-project records are unavailable right now. Work-item projects are still shown."));
      root.append(h("p","portfolio-notice","A new project appears when its first Rhythm work item is saved, or when it has a project overview tagged portfolio-project. Ideas live in their own view and require no tasks."));
    }
$before$,$after$        for(const g of live)list.append(add(h("li","pcard"),...projectCard(g)));
        root.append(list);
      }
      if(last?.feeds?.portfolio?.status!=="ok")root.append(h("p","portfolio-notice","The collections list is unavailable."));
      if(!portfolio.available.portfolioProjects)root.append(h("p","portfolio-notice","Planned-project records are unavailable right now. Work-item projects are still shown."));
      root.append(h("p","portfolio-notice","Choose Show in Projects in a collection to add it here."));
    }
$after$);
   page:=replace(page,$before$      head.append(star);
    }
$before$,$after$      head.append(star);
    }
    const overview = portfolioData(last,r).planned.find(m=>projKey(m)===g.key);
    if(overview?.graphId){
      const source=button("open","Open project overview","overview");
      source.addEventListener("click",()=>window.rhythm.openStar(overview.graphId));
      head.append(source);
    }
$after$);
   page:=replace(page,$before$    root.className = "card " + (shape === "tall" || shape === "launcher" ? shape : "compact");
    if (r.error) {
$before$,$after$    root.className = "card " + (shape === "tall" || shape === "launcher" ? shape : "compact");
    if (r.error && (ui.section==="ideas" || last?.feeds?.ideaInbox)) {
      root.append(h("p","portfolio-notice",r.error));
      drawTop({items:[],byId:new Map(),blocks:new Map(),count:0,truncated:false,asOf:last.asOf});
      return;
    }
    if (r.error) {
$after$);
   page:=replace(page,$before$    const c = tally(sp.work);
    const gs = groups(r.items).map((g) => ({ ...g, c: tally(split(g.items).work) })).filter((g) => g.c.open > 0).sort(attention);
    add(root, add(h("p", "ctitle"), h("span", null, "Projects"), h("span", null, plural(gs.length, "project"))));
$before$,$after$    const c = tally(sp.work);
    const gs = groups(r.items).map((g) => ({ ...g, c: tally(split(g.items).work) })).sort(attention);
    add(root, add(h("p", "ctitle"), h("span", null, "Projects"), h("span", null, plural(gs.length, "project"))));
$after$);
  ELSE CONTINUE;
  END IF;
  SELECT coalesce((SELECT l.layout FROM dashboard_layouts l WHERE l.dashboard_id=board AND l.version=v.version),v.layout) INTO arranged;
  INSERT INTO dashboard_versions(dashboard_id,version,title,description,html,feeds,stars,writes,note,saved_by,layout)
   VALUES(board,v.version+1,v.title,v.description,page,v.feeds||'[{"name":"portfolio","kind":"projects"}]'::jsonb,
    v.stars,v.writes,'Show designated collections, including projects with no tickets, and keep the Ideas inbox.',v.saved_by,arranged);
  UPDATE dashboards SET updated_at=now() WHERE id=board;
 END LOOP;
END $upgrade$;
