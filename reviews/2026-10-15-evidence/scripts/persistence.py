import pathlib,tempfile,shutil,subprocess,time,json,urllib.request,urllib.error,uuid,sqlite3,os
base=pathlib.Path('/tmp/yap-r2-review'); root=pathlib.Path(tempfile.mkdtemp(prefix='persistent-',dir=base)); (root/'Data').mkdir();(root/'wwwroot').symlink_to(base/'Yap/wwwroot')
for p in (base/'Yap/Data').glob('*.json'):shutil.copy(p,root/'Data'/p.name)
dll='/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/bin/Debug/net10.0/Yap.dll'; url='http://127.0.0.1:5102'; cookie=''
def req(path,body=None,method=None):
 r=urllib.request.Request(url+path,data=json.dumps(body).encode() if body is not None else None,method=method,headers={'Content-Type':'application/json','Cookie':cookie})
 try:
  with urllib.request.urlopen(r,timeout=8) as s:return s.status,json.loads(s.read() or b'null'),s.headers
 except urllib.error.HTTPError as e:return e.code,e.read().decode(),e.headers
def start():
 log=open(root/'server.log','a'); p=subprocess.Popen(['dotnet',dll,'--contentRoot',str(root),'--urls',url,'--ChatSettings:Persistence:Enabled=true','--ChatSettings:WelcomePageEnabled=false'],cwd=root,env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Development'},stdout=log,stderr=log)
 for _ in range(200):
  if p.poll() is not None:raise RuntimeError((root/'server.log').read_text()[-4000:])
  try:req('/api/bootstrap');return p
  except (OSError,urllib.error.URLError):time.sleep(.1)
 raise RuntimeError('startup timeout')
def stop(p):p.terminate();p.wait(timeout=15)
p=start()
try:
 status,user,headers=req('/api/auth/signin',{'username':'persistadmin'});cookie=headers['Set-Cookie'].split(';')[0]
 _,boot,_=req('/api/bootstrap');room=boot['rooms'][0]['id'];cid=str(uuid.uuid4())
 payload={'clientId':cid,'text':'Persistence retry sentinel'}
 before=req('/api/channels/'+room+'/messages',payload)
 time.sleep(1)
 with sqlite3.connect(root/'Data/yap.db') as db:rows=db.execute('select Id,Content,ClientId from Messages where Content=?',(payload['text'],)).fetchall()
 print(json.dumps({'test':'Stored clientId','bug':rows[0][2] is None,'rows':rows,'sentClientId':cid}),flush=True)
 stop(p);p=start()
 after=req('/api/channels/'+room+'/messages',payload)
 print(json.dumps({'test':'Same POST after restart','bug':before[1]['id']!=after[1]['id'],'before':before[:2],'after':after[:2]}),flush=True)
 time.sleep(.5)
 with sqlite3.connect(root/'Data/yap.db') as db:print(json.dumps({'test':'Persisted message count','count':db.execute('select count(*) from Messages where Content=?',(payload['text'],)).fetchone()[0]}),flush=True)
finally:stop(p); print('ROOT',root)
