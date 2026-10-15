import { chromium } from 'playwright';
import { BASE, sleep, waitTrue, startServer, signIn } from './lib.mjs';
const server = await startServer();
const browser = await chromium.launch({executablePath:'/home/agent/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome'});
const contexts=[];
const results=[];
const make=async(name,opts={})=>{const c=await browser.newContext(opts);contexts.push(c);const p=await c.newPage();p.setDefaultTimeout(5000);await signIn(p,name);await p.waitForFunction(async()=> (await import('/js/store.js')).state.connection==='connected' && !!(await import('/js/store.js')).state.open);return [c,p];};
const state=p=>p.evaluate(async()=> (await import('/js/store.js')).state);
const nav=async(p,path)=>{await p.evaluate(async path=>(await import('/js/router.js')).navigate(path),path);await sleep(250);};
const call=(p,method,...args)=>p.evaluate(async ({method,args})=>(await import('/js/hub.js')).invoke(method,...args),{method,args});
const req=(p,path,body,method='POST')=>p.evaluate(async({path,body,method})=>{const r=await fetch(path,{method,headers:{'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body)});return {status:r.status,body:await r.json().catch(()=>null)};},{path,body,method});
const post=(p,id,text,extra={})=>req(p,`/api/channels/${id}/messages`,{text,clientId:crypto.randomUUID(),...extra});
const run=async(name,fn)=>{try{const r=await fn();results.push({name,...r});console.log(JSON.stringify({name,...r}));}catch(e){console.log(JSON.stringify({name,error:e.stack}));}};
try {
const [ca,a]=await make('reviewadmin');
const [cb,b]=await make('reviewbob');
const lobby=(await state(a)).rooms[0].id;
await run('Reconnect after more than one page',async()=>{
 await post(b,lobby,'before gap');await sleep(250);
 await ca.setOffline(true);await sleep(150);
 for(let i=1;i<=60;i++) await post(b,lobby,`gap-${i}`);
 await ca.setOffline(false);
 await waitTrue(async()=> (await state(a)).connection==='connected',10000);await sleep(600);
 const s=await state(a);const missing=Array.from({length:60},(_,i)=>`gap-${i+1}`).filter(t=>!s.open.messages.some(m=>m.text===t));
 return {bug:missing.length>0&&!s.open.hasMore,count:s.open.messages.length,hasMore:s.open.hasMore,missing};
});
await run('Offline send into unopened DM',async()=>{
 await ca.setOffline(true);await nav(a,'/dm/reviewbob');
 await a.fill('.message-input','new offline DM');await a.press('.message-input','Enter');await sleep(200);
 const s=await state(a);const pending=s.pending.length;const draft=await a.inputValue('.message-input');
 await ca.setOffline(false);await sleep(500);await nav(a,'/lobby');
 return {bug:pending===0,pending,draft,open:s.open};
});
await run('Chosen Away preserves unread',async()=>{
 await nav(a,'/dm/reviewbob');await nav(b,'/dm/reviewadmin');
 await call(a,'SetStatus','away');await sleep(100);
 await post(b,(await state(b)).open.channelId,'message while away');await sleep(500);
 const s=await state(a);const actual=s.unread[s.open.channelId]??0;
 await call(a,'SetStatus','online');return {bug:actual===0,status:s.user.status,unread:actual};
});
await run('Multi GIF pick retains all attachments',async()=>{
 await nav(a,'/lobby');
 await a.setInputFiles('input[type=file]',[
 {name:'first.gif',mimeType:'image/gif',buffer:Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7','base64')},
 {name:'second.gif',mimeType:'image/gif',buffer:Buffer.from('R0lGODlhAQABAIAAAAD/AP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7','base64')}]);
 const done=await waitTrue(async()=> (await state(a)).open.messages.some(m=>m.gifs.length),15000);const s=await state(a);
 const sent=s.open.messages.filter(m=>m.username==='reviewadmin'&&m.gifs.length);
 return {bug:done&&sent.at(-1).gifs.length!==2,done,attachmentCounts:sent.map(m=>m.gifs.length),pending:s.pending.map(x=>({failed:x.failed,text:x.text}))};
});
await run('PWA signout prevents handoff reuse',async()=>{
 const manifest=await req(b,'/manifest.webmanifest',undefined,'GET');
 await b.evaluate(async()=> (await import('/js/session.js')).signOut());
 await b.goto(BASE+manifest.body.start_url);await sleep(700);
 return {bug:(await state(b)).user?.username==='reviewbob',afterLaunch:(await state(b)).user?.username};
});
} finally {await browser.close();console.log('SERVER_ISSUES',await server.stop());}
