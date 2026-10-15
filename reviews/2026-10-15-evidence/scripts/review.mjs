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
const [cc,c]=await make('revieweve');
await a.reload();await a.waitForSelector('.message-input');await sleep(500);
const lobby=(await state(a)).rooms[0].id;
let privateId,dm;
await run('DM quote authorization',async()=>{
 dm=(await call(a,'OpenDm','reviewbob')).channelId;
 const secret=await post(a,dm,'PRIVATE DM SENTINEL');privateId=secret.body.id;
 const direct=await call(c,'Older',dm,new Date(Date.now()+1000).toISOString(),crypto.randomUUID());
 const leak=await post(c,lobby,'cross-channel quote',{replyToId:privateId});
 return {bug:leak.body?.replyTo?.text==='PRIVATE DM SENTINEL',directDmMessages:direct.messages?.length,response:leak};
});
await run('Unauthorised DM typing',async()=>{
 await nav(b,'/dm/reviewadmin');
 await call(c,'Typing',dm,true);await sleep(200);
 return {bug:!!(await state(b)).open.typists.revieweve,typists:(await state(b)).open.typists};
});
await run('Phone pickers after navigation',async()=>{
 const [cm,m]=await make('reviewphone',{viewport:{width:390,height:844},isMobile:true,hasTouch:true});
 await m.click('.emoji-toggle-button');await sleep(200);
 const before=await m.locator('.combined-picker .emoji-btn').count();
 await nav(m,'/settings');await nav(m,'/lobby');await m.click('.emoji-toggle-button');await sleep(200);
 const after=await m.locator('.combined-picker .emoji-btn').count();
 await m.screenshot({path:'/tmp/yap-r2-review/phone-picker-after-navigation.png'});
 return {bug:before>0&&after===0,before,after,combinedHtml:await m.locator('.combined-picker').innerHTML()};
});
await run('Room reorder changes lobby destination',async()=>{
 const r=await req(a,'/api/rooms',{name:'firstroom',historyLimit:0,sinceJoined:false});
 await req(a,`/api/rooms/${r.body.id}/move`,{up:true});await sleep(200);
 await nav(c,'/settings');await nav(c,'/lobby');
 const s=await state(c);
 const result={bug:s.open.channelId!==lobby,lobby,actual:s.open.channelId,rooms:s.rooms};
 await req(a,`/api/rooms/${r.body.id}/move`,{up:false});return result;
});
await run('Signout with another tab open',async()=>{
 const second=await ca.newPage();second.setDefaultTimeout(5000);await second.goto(BASE+'/dm/reviewbob');await second.waitForSelector('.message-input');await sleep(500);
 await a.evaluate(async()=> (await import('/js/session.js')).signOut());await sleep(400);
 const newMessage=await post(b,dm,'PRIVATE AFTER SIGNOUT');await sleep(400);
 const stale=await state(second);const anon=await req(second,'/api/bootstrap',undefined,'GET');
 const result={bug:!anon.body.user&&stale.user?.username==='reviewadmin',secondTabUser:stale.user?.username,apiUser:anon.body.user??null,receivedAfterSignout:stale.open?.messages.some(x=>x.id===newMessage.body.id),connection:stale.connection};
 await second.close();return result;
});
await run('Offline signout then reconnect',async()=>{
 await cb.setOffline(true);
 await b.evaluate(async()=> (await import('/js/session.js')).signOut());await sleep(200);
 const signedOut=(await state(b)).user===null;
 await cb.setOffline(false);await b.reload();await b.waitForSelector('.message-input');
 return {bug:signedOut&&(await state(b)).user?.username==='reviewbob',signedOut,afterReload:(await state(b)).user?.username};
});
} finally {await browser.close();console.log('SERVER_ISSUES',await server.stop());}
