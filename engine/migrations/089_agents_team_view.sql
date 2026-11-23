-- Add the optional T3 team view to the two known Agents pages while retaining their prior versions.
-- The renderer is the release copy of the starter's team.html.
DO $upgrade$
DECLARE board uuid; v dashboard_versions; page text; fingerprint text; arranged jsonb;
  team text := $team$
<style>
  .rhythm-team { display:grid; gap:0.75rem; min-width:0; color:inherit; }
  .rhythm-team section { margin-block:0.25rem; }
  .rhythm-team ul { list-style:none; padding-inline-start:1rem; border-inline-start:2px solid currentColor; }
  .rhythm-team li { margin-block:0.7rem; }
  .rhythm-team p { margin-block:0.35rem; overflow-wrap:anywhere; }
  .rhythm-team .team-meta { font-size:.875em; opacity:.82; overflow-wrap:anywhere; }
  .rhythm-team button { max-width:100%; text-align:start; overflow-wrap:anywhere; }
</style>
<script>
  window.renderRhythmTeam = function (target, feeds, now = Date.now()) {
    target.replaceChildren();
    target.classList.add("rhythm-team");
    function add(parent, tag, text, style) {
      const child = document.createElement(tag);
      if (text !== undefined) child.textContent = text;
      if (style) child.className = style;
      parent.append(child);
      return child;
    }
    const label = {
      running:"Running", starting:"Starting", queued:"Queued", completed:"Run completed", failed:"Failed",
      interrupted:"Interrupted", cancelled:"Cancelled", idle:"Idle", stopped:"Stopped", waiting:"Waiting for input",
      blocked:"Blocked", error:"Error", unknown:"Status unknown", archived:"Archived", reserved:"Awaiting a session",
      unavailable:"Session unavailable"
    };
    function when(time) {
      return new Date(time).toLocaleString([], {dateStyle:"short",timeStyle:"short"});
    }
    function session(parent, role, data) {
      if (!data || typeof data !== "object") return;
      const expired = ["running","starting"].includes(data.state) && data.at && now-data.at > 45*60000;
      const line = add(parent,"p");
      add(line,"strong",role+" · "+(data.closed ? "Assignment closed" : expired ? "No recent activity" : label[data.state] || "Status unknown"));
      const details = [];
      if (Number.isFinite(data.at) && data.at > 0) details.push("Last activity "+when(data.at));
      if (typeof data.branch === "string") details.push(data.branch);
      if (details.length) add(parent,"p",details.join(" · "),"team-meta");
    }
    add(target,"h2","T3 team");
    const runtime = feeds.teamStatus, work = feeds.teamWork;
    if (runtime?.status !== "ok" || work?.status !== "ok") {
      add(target,"p","Team status is unavailable."); return;
    }
    const records = (runtime.memories || []).filter(m => m.scope === "shared" && m.state === "current" &&
      !m.truncated && m.tags?.includes("t3-team-status") && m.project?.slug === "agent-handoffs");
    if (!records.length) { add(target,"p","Waiting for the first T3 report."); return; }
    let status;
    try {
      const prefix="T3 team status\n";
      if (records.length!==1 || !records[0].preview.startsWith(prefix)) throw new Error();
      status=JSON.parse(records[0].preview.slice(prefix.length));
      if (status?.v!==1 || !Number.isFinite(status.observed) || !Array.isArray(status.groups)) throw new Error();
    } catch { add(target,"p","The latest T3 report could not be read."); return; }
    if (!status.observed) { add(target,"p","Waiting for the first T3 report."); return; }
    const stale = now-status.observed > 45*60000;
    add(target,"p",(stale ? "Last report · updates overdue · " : "Last checked ")+when(status.observed),"team-meta");
    session(target,"Master orchestrator",status.master);
    const tickets = new Map((work.memories || []).filter(m => m.scope==="shared" && m.state==="current").map(m=>[m.id,m]));
    let visible=0;
    for (const group of status.groups) {
      if (!group || !Array.isArray(group.workers)) continue;
      const rows=group.workers.filter(row=>tickets.has(row?.ticket) && tickets.get(row.ticket).project?.slug===group.project);
      if (!rows.length) continue;
      visible++;
      const block = add(target,"section");
      add(block,"h3",tickets.get(rows[0].ticket).project.name || group.project);
      if (group.lead) session(block,"Project lead",group.lead);
      else add(block,"p","Reporting directly to the master","team-meta");
      const list=add(block,"ul");
      for (const row of rows) {
        const entry=add(list,"li"), ticket=tickets.get(row.ticket);
        session(entry,"Worker",row);
        const button=add(entry,"button",String(ticket.preview || "Open work item").split("\n")[0].slice(0,160));
        button.type="button";
        button.addEventListener("click",()=>window.rhythm.openStar(ticket.graphId));
        let summary=ticket.work?.state || "";
        if (ticket.work?.blocked) summary+=" · blocked";
        if (row.report==="rework" && ticket.work?.state!=="done") summary+=" · check needs rework";
        if (row.report==="decision" && ticket.work?.state==="review") summary+=" · owner decision";
        if (summary) add(entry,"p",summary,"team-meta");
      }
    }
    if (!visible) add(target,"p","No team assignments are visible.");
    if (work.truncated) add(target,"p","Older tickets may be outside this dashboard’s work feed.","team-meta");
  };
</script>
$team$;
BEGIN
  FOR board IN SELECT id FROM dashboards ORDER BY id FOR UPDATE LOOP
    SELECT * INTO v FROM dashboard_versions WHERE dashboard_id=board ORDER BY version DESC LIMIT 1;
    IF v.title IS DISTINCT FROM 'Agents' OR v.version>=10000 OR jsonb_array_length(v.feeds)>10 OR EXISTS
      (SELECT 1 FROM jsonb_array_elements(v.feeds) f WHERE f->>'name' IN ('teamStatus','teamWork')) THEN CONTINUE; END IF;
    page:=rtrim(replace(v.html,chr(13)||chr(10),chr(10)),chr(10));
    fingerprint:=encode(sha256(convert_to(page,'UTF8')),'hex');
    IF fingerprint='8c99d9169bf4c602d522d1ea382ed3d7d21d3b91ae53e387f1904447164edb1d' THEN
      page:=replace(page,'<main>',team||'<main>');
    page:=replace(page,$old$["overview", "Overview"],$old$,$new$["overview", "Overview"],
        ["team", "T3 team"],$new$);
    page:=replace(page,$old$    if (view === "overview") {$old$,$new$    if (view === "team") {
      const team = el("section");
      board.append(team);
      window.renderRhythmTeam(team, feeds);
      return;
    }
    if (view === "overview") {$new$);
    ELSIF fingerprint='947a453d2cee129a24f61c460f827d645ababfdd43f49c213cdf058ebd59fa36' THEN
      page:=replace(page,'<script>',team||'<script>');
    page:=replace(page,$old$        <button type="button" data-view="activity"$old$,$new$        <button type="button" data-view="team" aria-pressed="false">T3 team</button>
        <button type="button" data-view="activity"$new$);
    page:=replace(page,$old$    if (state.view === 'activity') content = activity(s);$old$,$new$    if (state.view === 'team') content = '<section data-team></section>';
    else if (state.view === 'activity') content = activity(s);$new$);
    page:=replace(page,$old$    main.innerHTML = content + fresh;$old$,$new$    main.innerHTML = content + fresh;
    if (state.view === 'team') window.renderRhythmTeam(main.querySelector('[data-team]'), s.feeds);$new$);
    ELSE CONTINUE;
    END IF;
    SELECT coalesce((SELECT l.layout FROM dashboard_layouts l WHERE l.dashboard_id=board AND l.version=v.version),v.layout) INTO arranged;
    INSERT INTO dashboard_versions(dashboard_id,version,title,description,html,feeds,stars,writes,note,saved_by,layout)
      VALUES(board,v.version+1,v.title,v.description,page,v.feeds||'[{"name":"teamStatus","kind":"memories","where":{"project":"agent-handoffs","tag":"t3-team-status","work":false},"fullText":true,"limit":2},{"name":"teamWork","kind":"memories","where":{"work":true},"limit":200}]'::jsonb,
        v.stars,v.writes,'Show the reporting T3 master, project leads and workers.',v.saved_by,arranged);
    UPDATE dashboards SET updated_at=now() WHERE id=board;
  END LOOP;
END $upgrade$;
