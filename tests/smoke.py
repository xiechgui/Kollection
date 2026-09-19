"""Real HTTP + SQLite integration checks. Uses a temporary DB and real server process."""
import json, os, subprocess, tempfile, time, urllib.request, urllib.error
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'src/Collection.Web/bin/Release/net10.0/Collection.Web.dll'
DOTNET = os.getenv('DOTNET_EXE', 'dotnet')
PORT = '15278'
BASE = 'http://127.0.0.1:' + PORT

def call(path, method='GET', body=None, expected=200, extra=None):
    headers={'Content-Type':'application/json','X-Collection-Client':'local-ui'}
    headers.update(extra or {})
    req=urllib.request.Request(BASE+'/api'+path, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
    try:
        with urllib.request.urlopen(req,timeout=15) as r: code, raw=r.status,r.read()
    except urllib.error.HTTPError as e: code,raw=e.code,e.read()
    assert code==expected,(path,code,raw)
    return json.loads(raw) if raw else None

def start(folder):
    env=dict(os.environ,COLLECTION_DATA=folder,COLLECTION_PORT=PORT)
    p=subprocess.Popen([DOTNET,str(DLL)],cwd=ROOT/'src/Collection.Web',env=env,stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
    for _ in range(100):
        try: call('/state');return p
        except (OSError,AssertionError):
            if p.poll() is not None: raise RuntimeError('Server exited')
            time.sleep(.1)
    p.terminate();raise RuntimeError('Startup timeout')

def stop(p):
    p.terminate();p.wait(timeout=10)

with tempfile.TemporaryDirectory() as tmp:
    proc=start(tmp)
    try:
        with urllib.request.urlopen(BASE) as r: assert '拾藏' in r.read().decode()
        s=call('/state');assert not s['items']
        actor={'id':'actor-1','name':'测试演员','tags':['actor'],'values':{}}
        s=call('/items','POST',{'revision':s['revision'],'item':actor})
        movie={'id':'movie-1','name':'测试电影','tags':['movie'],'values':{'actor':{'refs':['actor-1']},'rating':{'number':9}}}
        s=call('/items','POST',{'revision':s['revision'],'item':movie})
        call('/items/actor-1?revision='+str(s['revision']),'DELETE',expected=409)
        call('/items','POST',{'revision':0,'item':actor},expected=409)
        invalid=dict(movie,id='bad',values={'actor':{'refs':['missing']}})
        call('/items','POST',{'revision':s['revision'],'item':invalid},expected=400)
        invalid=dict(movie,id='bad',values={'rating':{'text':'not a number'}})
        call('/items','POST',{'revision':s['revision'],'item':invalid},expected=400)
        video=next(t for t in s['tags'] if t['id']=='video').copy();video['parentId']='movie'
        call('/tags','POST',{'revision':s['revision'],'tag':video},expected=400)
        f=Path(tmp)/'example.png';f.write_bytes(b'png test fixture')
        imported=call('/import','POST',{'revision':s['revision'],'path':str(f),'recursive':False})
        s=imported['state'];draft=next(i for i in s['items'] if i['draft'])
        assert draft['tags']==['file','image'] and imported['summary']['added']==1
        call('/items/'+draft['id']+'/media',expected=404)
        bad=dict(movie,id='bad',values={'cover':{'refs':[draft['id']]}})
        call('/items','POST',{'revision':s['revision'],'item':bad},expected=400)
        imported=call('/import','POST',{'revision':s['revision'],'path':str(f),'recursive':False})
        assert imported['summary']['skipped']==1;s=imported['state']
        draft['tags']=['file'];draft['values'].pop('size')
        s=call('/items/'+draft['id']+'/confirm','POST',{'revision':s['revision'],'item':draft})
        saved=next(i for i in s['items'] if i['id']==draft['id']);assert not saved['draft'] and 'size' not in saved['values'] and saved['tags']==['file']
        exported=call('/export');assert len(exported['items'])==3
        call('/state',expected=403,extra={'Origin':'https://example.com'})
        call('/state',expected=403,extra={'Host':'evil.example'})
        call('/items','POST',{'revision':s['revision'],'item':actor},expected=403,extra={'X-Collection-Client':''})
        stop(proc);proc=start(tmp)
        assert call('/state')['revision']==s['revision']
        s=call('/items/movie-1?revision='+str(s['revision']),'DELETE')
        s=call('/items/actor-1?revision='+str(s['revision']),'DELETE')
        s=call('/items/'+draft['id']+'?revision='+str(s['revision']),'DELETE')
        assert f.exists() and len(s['items'])==0
        print('PASS: CRUD, references, types, inheritance cycle, staging, review edits, deduplication, persistence, export, local API guards, source file preservation')
    finally: stop(proc)
