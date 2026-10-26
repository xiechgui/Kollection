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

# Migration, pagination, independent edits, jobs and cache use only disposable data.
import sqlite3, base64
with tempfile.TemporaryDirectory() as tmp:
    proc=start(tmp)
    seed=call('/state');stop(proc)
    db=Path(tmp)/'collection.db';db.unlink()
    legacy=dict(seed)
    legacy['items']=[{'id':f'legacy-{n}','name':f'Legacy {n:04d}','tags':['text'],'values':{'body':{'text':'migration marker','refs':[]}},'draft':False,'updatedAt':'2026-01-01T00:00:00Z'} for n in range(125)]
    with sqlite3.connect(db) as conn:
        conn.execute('CREATE TABLE library(id INTEGER PRIMARY KEY,json TEXT NOT NULL)')
        conn.execute('INSERT INTO library VALUES(1,?)',(json.dumps(legacy),))
    proc=start(tmp)
    try:
        assert len(list(Path(tmp).glob('collection-v1-*.db')))==1
        page=call('/items?limit=60');second=call('/items?limit=60&offset=60')
        assert page['total']==125 and len(page['items'])==60
        assert not {i['id'] for i in page['items']} & {i['id'] for i in second['items']}
        assert call('/items?q=migration%20marker')['total']==125
        a,b=page['items'][:2];revision=call('/catalog')['revision']
        a['name']='Changed A';updated=call('/items/'+a['id']+'?compact=1','PUT',{'revision':revision,'item':a})
        assert 'items' not in updated and updated['item']['version']==2
        b['name']='Changed B';call('/items/'+b['id']+'?compact=1','PUT',{'revision':revision,'item':b})
        call('/items/'+a['id'],'PUT',{'revision':revision,'item':a},expected=409)
        # Unrelated records are not rewritten by a single-object save.
        with sqlite3.connect(db) as conn:
            assert conn.execute("SELECT version FROM items WHERE id='legacy-124'").fetchone()[0]==1
            assert conn.execute("SELECT count(*) FROM sqlite_master WHERE name='library'").fetchone()[0]==0
        files=Path(tmp)/'source';files.mkdir()
        for n in range(75):(files/f'{n}.txt').write_text('test')
        job=call('/imports','POST',{'revision':0,'path':str(files),'recursive':True})
        def wait_job(id):
            for _ in range(200):
                result=next(j for j in call('/imports') if j['id']==id)
                if result['status'] not in ('running','queued','cancelling'):return result
                time.sleep(.05)
            raise AssertionError('job timeout')
        done=wait_job(job['id']);assert done['status']=='completed' and done['added']==75,done
        retry=call('/imports/'+job['id']+'/retry','POST',{})
        assert wait_job(retry['id'])['skipped']==75
        drafts=call('/items?draft=true&limit=100')['items'];versions={i['id']:i['version'] for i in drafts}
        bad=dict(versions);bad[drafts[0]['id']]=999
        call('/drafts/batch','POST',{'versions':bad,'action':'confirm'},expected=409)
        assert call('/catalog')['draftCount']==75
        call('/drafts/batch','POST',{'versions':versions,'action':'tags','addTags':['later']})
        drafts=call('/items?draft=true&limit=100')['items']
        assert all('later' in i['tags'] for i in drafts)
        call('/drafts/batch','POST',{'versions':{i['id']:i['version'] for i in drafts},'action':'confirm'})
        assert call('/catalog')['draftCount']==0
        # A real 1x1 PNG must decode and produce a stable JPEG cache entry.
        png=files/'pixel.png';png.write_bytes(base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII='))
        imported=call('/import','POST',{'revision':call('/catalog')['revision'],'path':str(png),'recursive':False})
        pic=next(i for i in imported['state']['items'] if i['draft'])
        call('/items/'+pic['id']+'/confirm','POST',{'revision':imported['state']['revision'],'item':pic})
        with urllib.request.urlopen(BASE+'/api/items/'+pic['id']+'/thumbnail') as response:
            assert response.headers['Content-Type']=='image/jpeg' and response.read()[:2]==b'\xff\xd8'
        caches=list((Path(tmp)/'thumbnails').glob('*.jpg'));assert len(caches)==1
        with urllib.request.urlopen(BASE+'/api/items/'+pic['id']+'/thumbnail') as response:response.read()
        assert list((Path(tmp)/'thumbnails').glob('*.jpg'))==caches
        call('/thumbnails/rebuild','POST',{})
        assert not list((Path(tmp)/'thumbnails').glob('*.jpg'))
        # Startup preserves records and completed jobs; migration runs exactly once.
        stop(proc);proc=start(tmp)
        assert call('/catalog')['count']==201 and len(list(Path(tmp).glob('collection-v1-*.db')))==1
        assert next(j for j in call('/imports') if j['id']==job['id'])['status']=='completed'
        print('PASS: legacy backup/migration, SQL pagination/search, row versions, atomic batch, persistent jobs, deduplication and thumbnail cache')
    finally:stop(proc)
