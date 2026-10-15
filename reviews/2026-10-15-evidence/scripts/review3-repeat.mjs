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
const [ca,a]=await make('reviewadmin',{serviceWorkers:'block'});
const [cb,b]=await make('reviewbob',{serviceWorkers:'block'});
const lobby=(await state(a)).rooms[0].id;
await run('Slow route module completes after a later navigation',async()=>{
 let release;const held=new Promise(r=>release=r);let requested=false;
 await a.route('**/js/pages/settings.js',async route=>{requested=true;await held;await route.continue();});
 await nav(a,'/settings');await waitTrue(()=>requested,3000);
 await nav(a,'/dm/reviewbob');await a.waitForSelector('.message-input');
 release();await sleep(3000);
 const settings=await a.locator('#display-name').count();
 const content=await a.locator('main').innerText().catch(()=>a.locator('body').innerText());
 await a.screenshot({path:'/tmp/yap-r2-review/navigation-race.png'});
 return {bug:settings>0&&new URL(a.url()).pathname==='/dm/reviewbob',url:a.url(),settings,composer:await a.locator('.message-input').count(),content:content.slice(0,250)};
});
await a.reload();await a.waitForSelector('.message-input');
await run('Account switch updates other tabs identity',async()=>{
 const second=await ca.newPage();second.setDefaultTimeout(5000);await second.goto(BASE+'/lobby');await second.waitForSelector('.message-input');await sleep(300);
 const link=await req(b,'/api/account/link',undefined,'GET');
 await nav(a,'/invite/'+link.body.code);await a.locator('.invite-button.primary').click();
 await waitTrue(async()=> (await state(a)).user?.username==='reviewbob',5000);await sleep(400);
 const stale=await state(second);const current=await req(second,'/api/bootstrap',undefined,'GET');
 await second.fill('.message-input','sent from stale account tab');await second.press('.message-input','Enter');await sleep(700);
 const actual=(await call(b,'OpenRoom',lobby)).messages.find(m=>m.text==='sent from stale account tab');
 const result={bug:stale.user.username!==current.body.user.username,tabUser:stale.user.username,authenticatedUser:current.body.user.username,actualAuthor:actual?.username};
 await second.close();return result;
});
} finally {await browser.close();console.log('SERVER_ISSUES',await server.stop());}
