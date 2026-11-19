// Local test server only; never an app entrypoint or production fallback. Serves

// the real built companion, with a synthetic facade and no outbound requests.

// Generate the short-lived test certificate as documented in README first.

import { createServer } from 'node:https';

import { readFileSync, statSync } from 'node:fs';

import { resolve, extname, sep } from 'node:path';

import { fileURLToPath } from 'node:url';

import { unavailableHealth } from '../../../app/build/server/healthState.js';
import { applyFeedback, feedbackInput } from '../../../app/build/src/insights.js';

import { assertGraphPage } from '../../../app/build/src/validation.js';

import { cleanDashboard } from '../../../app/build/src/dashboards.js';

const root = fileURLToPath(new URL('../../../app/dist/', import.meta.url));

const certificate = fileURLToPath(new URL('../build/fixture/server.p12', import.meta.url));

const now = new Date().toISOString();

const titles = ['Garden plan', 'Reading list', 'Weekend walk', 'Kitchen notes', 'Library project', 'Seed calendar', 'Trail map', 'Workshop', 'Sketchbook', 'Spring ideas'];

const nodes = titles.map((title, i) => {

  const identity = { provider: 'second-brain', account: 'fixture', externalId: `note-${i}` };

  return { id: `second-brain:fixture:note-${i}`, title, type: 'memory', overview: 'Synthetic emulator record',

    groups: [{id:'fixture',label:'Emulator fixtures'}], provenance: {sourceId:'second-brain',recordId:`note-${i}`,observedAt:now,demo:false},

    resource: {identity,canonicalUrl:null,sourceUrl:null,sourceModifiedAt:now,retrievedAt:now,revision:'1',owner:'Fixture',

      access:{scope:'household',canRead:true,canOpen:false}, availability:'available',representation:'original',

      content:`# ${title}\n\nFixture source opened successfully.\n\nThis non-sensitive record exists only on the disposable local test server.`} };

});

const edges = nodes.slice(1).map((n,i)=>({id:`edge-${i}`,source:nodes[i].id,target:n.id,type:'part_of_project',directed:true,

  verification:'verified',strength:.6,origin:{...n.resource.identity,externalId:`edge-${i}`},relationship:'Synthetic project connection',

  evidence:[{resourceId:n.id,excerpt:'Fixture citation',locator:'test',retrievedAt:now}],sourceModifiedAt:now,retrievedAt:now}));

const graph = {nodes,edges,nextCursor:null,cursors:{},completeness:'complete',issues:[]};

assertGraphPage(graph);

const extendedNodes=Array.from({length:360},(_,i)=> {

  const source=nodes[i%nodes.length];const id=`second-brain:fixture:large-${i}`;

  return {...source,id,title:`${['Garden','Library','Workshop','Walking','Cooking','Reading'][Math.floor(i/50)%6]} ${i+1}`,

    resource:{...source.resource,identity:{...source.resource.identity,externalId:`large-${i}`}}};

});

const extendedEdges=extendedNodes.slice(1,300).filter((_,i)=>(i+1)%50!==0).map(n=> {

 const i=extendedNodes.indexOf(n);const hub=Math.floor(i/50)*50;

 return {...edges[0],id:`large-edge-${i}`,source:extendedNodes[i%4===0?hub:i-1].id,target:n.id};

});

const largeGraph={...graph,nodes:extendedNodes,edges:extendedEdges,completeness:'partial',nextCursor:'fixture-next'};

const disconnectedGraph={...graph,nodes:extendedNodes.slice(0,64),edges:[]};

// Geometry regressions: every record is synthetic. The dense case is one
// connected component with three strongly linked neighborhoods and cross-links.
const geometryNodes=extendedNodes.slice(0,84);
const geometryEdges=[];
const addGeometryEdge=(a,b)=>geometryEdges.push({...edges[0],id:`geometry-${a}-${b}`,source:geometryNodes[a].id,target:geometryNodes[b].id});
for(let a=0;a<84;a++) for(let step=1;step<=5;step++) {
  const base=Math.floor(a/28)*28;const b=base+(a-base+step)%28;
  if(a<b)addGeometryEdge(a,b);
}
for(let a=0;a<56;a+=7)addGeometryEdge(a,a+28);
const denseGraph={...graph,nodes:geometryNodes,edges:geometryEdges};
const hubGraph={...graph,nodes:geometryNodes,edges:geometryNodes.slice(1).map((n,i)=>({...edges[0],id:`hub-${i}`,source:geometryNodes[0].id,target:n.id}))};
assertGraphPage(denseGraph);assertGraphPage(hubGraph);

assertGraphPage(largeGraph);assertGraphPage(disconnectedGraph);



const boards = ['Garden overview','Reading overview'].map((title,i)=>({id:`00000000-0000-4000-8000-00000000000${i+1}`,version:1,latest:1,title,

  description:'Synthetic emulator dashboard',stars:[{graphId:nodes[i].id,title:nodes[i].title}],note:'Fixture version',restoredFrom:null,

  savedAt:now,createdAt:now,updatedAt:now,versions:[{version:1,note:'Fixture version',restoredFrom:null,savedAt:now}],writes:[],editsAllowed:false,corners:[],

  layout:null,layoutWhole:true,layoutRevision:0,layoutNote:null,layoutSavedAt:null,

  feeds:[{name:'progress',kind:'log',log:'fixture',subject:'household'}],

  html:`<style>body{font-family:system-ui;color:#d1e9e2;background:#14252b;margin:0;padding:18px}small{color:#8db8ab}h1{font-size:17px}strong{font-size:36px;color:#8dd7c0}p{line-height:1.5}html[data-mode=card] body{padding:12px}html[data-mode=card] small{font-size:8px}html[data-mode=card] h1{font-size:13px;margin:6px 0}html[data-mode=card] strong{font-size:32px}html[data-mode=card] p{font-size:11px;margin:6px 0}</style><small>EMULATOR FIXTURE</small><h1>${title}</h1><strong id="count">Loading</strong><p>tasks ready for the weekend</p><script>rhythm.onData(d=>{document.getElementById('count').textContent=d.feeds.progress.ready})</script>`}));

// Realistic authored and generated paths using only synthetic, declared feeds.

boards[0].html = `<meta name="rhythm-launcher-preview" content="1"><style>body{padding:13px;color:#d8eee5}header{font-size:11px;color:#bad8cb;margin:0 0 10px}strong{display:block;font:500 23px/1.2 var(--rhythm-display);letter-spacing:-.5px}p{font-size:11px;line-height:1.4;margin:8px 0}small{font-size:8px;color:#91b6a5}</style><header>Garden overview</header><strong id="count"></strong><p id="note"></p><small>Authored preview · synthetic</small><script>rhythm.onData(d=>{const f=d.feeds.progress;if(f.status!=='ok')throw Error('unavailable');document.getElementById('count').textContent=f.entries.length+' entries';document.getElementById('note').textContent=f.entries.at(-1).note})</script>`;

boards[1].feeds=[{name:'progress',kind:'data',star:nodes[1].id,by:'reading',days:14}];

const extraId='00000000-0000-4000-8000-000000000003';

let created=null;

const feedFor=(board)=>board.id===boards[0].id ? {kind:'log',log:'fixture',status:'ok',asOf:now,ready:7,entries:Array.from({length:7},(_,i)=>({date:'2026-11-'+String(i+6).padStart(2,'0'),type:'note',note:i===6?'Sow basil beside the sunny window.':'Water the seedlings.'}))} : board.id===extraId ? {kind:'contents',status:'ok',asOf:now,star:{graphId:nodes[2].id,title:'Weekend trail'},title:'Weekend trail',text:'Meet at the lake entrance. Bring water and a light jacket.'} : {kind:'data',status:'ok',asOf:now,star:{graphId:nodes[1].id,title:'Reading'},by:'reading',metrics:[{metric:'reading',label:'Reading time',unit:'min'}],rows:[12,18,15,24,21,30,28].map((value,i)=>({metric:'reading',unit:'min',date:'2026-11-'+String(i+6).padStart(2,'0'),value}))};

if (boards.some(d=>!cleanDashboard({dashboard:d}))) throw new Error('Invalid fixture dashboard');

let mode='normal'; const reads={};
let savedInsight = null;

const server = createServer({pfx:readFileSync(certificate),passphrase:'fixture-only'},async(req,res)=>{

  const url = new URL(req.url,'https://10.0.2.2:9443'); const path=url.pathname;

  const json=(data,status=200)=>{res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify(data));};

  if(path==='/__fixture/control') {
    mode=url.searchParams.get('mode')||'normal';
    if (mode === 'normal') savedInsight=null;
    return json({mode});
  }

  if(path==='/__fixture/state') return json({mode,reads});

  // Only this loopback synthetic fixture can create a dashboard; never a real engine write.

  if(path==='/__fixture/create' && req.method==='POST') {

    let chunks=[];for await(const c of req) chunks.push(c);

    const draft=JSON.parse(Buffer.concat(chunks).toString());

    const candidate={...boards[1],id:extraId,title:draft.title,description:draft.description||'',html:draft.html,feeds:draft.feeds};

    if(!cleanDashboard({dashboard:candidate}))return json({error:'Invalid synthetic draft'},400);

    created=candidate;return json({id:created.id,version:1});

  }

  if(path==='/__fixture/reset-created' && req.method==='POST'){created=null;return json({ok:true});}

  reads[path]=(reads[path]||0)+1;

  if(mode==='offline') {req.socket.destroy();return;}

  if(path==='/launcher-companion.json') {

    if(mode==='old') return json({version:0});
    if(mode==='old-builtins') return json({version:1});

    if(mode==='slow') await new Promise(r=>setTimeout(r,1800));

  }

  if(mode==='denied' && path.startsWith('/api/')) return json({error:'Fixture denial'},403);

  if(mode==='error' && path==='/launcher.html') return json({error:'Fixture failure'},503);

  if(path==='/api/instance') return json({name:'Emulator fixture',people:[],timezone:'UTC',mode:'live',catalogProvider:'rhythm'});

  if(path==='/api/observatory') {
    if(mode==='glucose-error')return json({error:'Synthetic glucose failure'},503);
    const data={...unavailableHealth(new Date().toISOString()),healthMode:'live'};
    if(mode.startsWith('glucose-')) {
      const observedAt=new Date(Date.now()-(mode==='glucose-stale'?31:2)*60000).toISOString();
      data.current={...data.current,status:mode==='glucose-missing'?'missing':'available',value:mode==='glucose-missing'?null:mode==='glucose-mmol'?6.8:123,
        unit:mode==='glucose-mmol'?'mmol/L':'mg/dL',provenance:{...data.current.provenance,sourceId:'fixture',observedAt}};
      data.sources=data.sources.map(s=>s.id==='glucose'?{...s,connected:true,status:'available',name:'Synthetic glucose',sampleAt:observedAt}:s);
    }
    return json(data);
  }

  if(path==='/api/graph') {

    if(mode==='graph-error') return json({error:'Synthetic graph failure'},503);

    if(mode==='graph-denied') return json({error:'Synthetic graph denial'},403);

    if(mode==='graph-slow') await new Promise(r=>setTimeout(r,1500));

    let chunks=[];for await(const c of req) chunks.push(c);

    const body=JSON.parse(Buffer.concat(chunks).toString()||'{}');

    if(mode==='drive-preview') {
      const sheet = {...nodes[0], id:'drive:fixture:synthetic-sheet', type:'document', title:'Garden spreadsheet',
        provenance:{...nodes[0].provenance, sourceId:'drive', recordId:'synthetic-sheet'},
        resource:{...nodes[0].resource, identity:{provider:'drive',account:'fixture',externalId:'synthetic-sheet'},
          canonicalUrl:'https://docs.google.com/spreadsheets/d/synthetic-sheet/edit', content:null,
          access:{scope:'household',canRead:true,canOpen:true}}};
      return json({...graph,nodes:[...nodes,sheet]});
    }

    if(body.operation==='content') return json(nodes.find(n=>n.id===body.value)||null);

    if(mode==='missing' && body.operation==='neighbors') return json({...graph,nodes:[],edges:[]});

    if(body.operation==='search') return json({...graph,nodes:nodes.filter(n=>n.title.toLowerCase().includes(String(body.value).toLowerCase())),edges:[]});

    return json(mode==='large'?largeGraph:mode==='disconnected'?disconnectedGraph:mode==='dense'?denseGraph:mode==='hub'?hubGraph:graph);

  }

  if(path==='/api/dashboards') {

    if(mode==='boards-error') return json({error:'Synthetic dashboard failure'},503);

    if(url.searchParams.has('pins')) return json({pins:[]});

    if(url.searchParams.has('cards')) return json({cards:{}});

    let board=[...boards,...(created?[created]:[])].find(d=>d.id===url.searchParams.get('id'));

    if(board && mode==='preview-broken')board={...board,html:'<meta name="rhythm-launcher-preview" content="1"><script>rhythm.onData(()=>{throw Error("Synthetic broken renderer")})</script>'};

    if(url.searchParams.has('data')&&board) {

      if(mode==='preview-error')return json({error:'Synthetic feed failure'},503);

      const feed=feedFor(board);

      return json({asOf:now,dashboard:{id:board.id,version:1},feeds:{progress:mode==='preview-absent'?{...feed,rows:undefined,entries:undefined,text:undefined}:mode==='preview-denied'?{...feed,status:'unavailable'}:feed}});

    }

    if(url.searchParams.has('id')) return json({dashboard:board||null});

    return json({dashboards:[...boards,...(created?[created]:[])]});

  }

  if(path==='/api/insights') {
    if(mode==='insight-error') return json({error:'Synthetic unavailable'},503);
    if(mode==='insight-slow') await new Promise(r=>setTimeout(r,1800));
    const generatedAt=new Date(Date.now()-(mode==='insight-old'?4*86400000:0)).toISOString();
    const insight={id:'fixture-daily',kind:'discovery',title:mode==='insight-long'?'A little time outside can make room for the next chapter in your garden plan':'A small window for your garden',
      summary:'Your synthetic garden plan and weekend walk share a quiet morning.',
      whyItMatters:'This fixture tests the real insight contract without household data.',
      nextStep:'Look at the plan when you have a moment.',points:[{text:'Synthetic source observation.',type:'observed'}],
      evidenceLevel:'weak',limitations:'Synthetic test data only.',stars:[],sources:[{kind:'record',title:'Synthetic garden plan',retrievedAt:generatedAt}],
      generatedBy:'fixture',generatedAt,sourcesAsOf:generatedAt,supersedes:null,
      status:mode==='insight-dismissed'?'dismissed':'new',feedback:null,createdAt:generatedAt,updatedAt:generatedAt};
    if (req.method === 'POST') {
      if (mode === 'insight-feedback-error') return json({error:'Synthetic save failure'},502);
      if (req.headers.origin !== 'https://10.0.2.2:' + port) return json({error:'Unexpected origin'},403);
      let body='';
      for await (const chunk of req) { body+=chunk; if (body.length>8192) return json({},413); }
      let input;
      try { input=feedbackInput(JSON.parse(body)); } catch { return json({},400); }
      if (!input || input.id !== insight.id) return json({},400);
      savedInsight=applyFeedback(insight,input.value,input);
      return json(savedInsight);
    }
    return json({insights:mode==='insight-empty'?[]:[savedInsight ?? insight],runs:[]});
  }

  if(path==='/api/screens') return json({available:false});

  if(path.startsWith('/api/')) return json({available:false});

  const file=resolve(root,'.'+(path==='/'?'/index.html':decodeURIComponent(path)));

  if(!file.startsWith(resolve(root)+sep)) return json({},403);

  try {

    if(!statSync(file).isFile()) return json({},404);

    const types={'.html':'text/html','.js':'text/javascript','.css':'text/css','.json':'application/json','.woff2':'font/woff2','.svg':'image/svg+xml','.png':'image/png'};

    res.writeHead(200,{'Content-Type':types[extname(file)]||'application/octet-stream','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});

    const bytes=readFileSync(file);

    res.end(extname(file)==='.html' ? bytes.toString().replace('<head>', `<head><script>window.addEventListener('error',e=>document.documentElement.dataset.fixtureError=e.message);window.addEventListener('unhandledrejection',e=>document.documentElement.dataset.fixtureError=String(e.reason));</script>`) : bytes);

  }catch {json({},404);}

});

const port=Number(process.env.FIXTURE_PORT || 9443);

if(![9443,9444].includes(port)) throw new Error('Fixture port must be 9443 or 9444');

server.listen(port,'127.0.0.1',()=>console.log(`Fixture HTTPS companion listening on loopback port ${port}. No household data.`));

