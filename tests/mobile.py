"""Opt-in LAN authentication and real multipart upload checks; temporary data only."""
import json, os, re, socket, subprocess, tempfile, threading, time, sys, secrets
import urllib.request, urllib.error
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
PORT = 15280
BASE = f'http://127.0.0.1:{PORT}'
# Bind only an actual assigned private IPv4; requests go through loopback with LAN Host.
addresses = [a[4][0] for a in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)]
def private(ip):
    b = list(map(int, ip.split('.')))
    return b[0] == 10 or b[:2] == [192,168] or b[0] == 172 and 16 <= b[1] <= 31
CLOUD = "--cloud" in sys.argv
CLOUD_KEY = secrets.token_hex(32)
UPLOAD_ONLY = "--upload-only" in sys.argv
LAN = "collection.test" if CLOUD else "127.0.0.1" if UPLOAD_ONLY else next((a for a in addresses if private(a)), None)
if LAN is None:
    raise RuntimeError('Mobile integration tests require an assigned private IPv4 address')

def call(path, method='GET', data=None, cookie=None, expected=200, local=False, headers=None):
    h={'Host': f'127.0.0.1:{PORT}' if local else LAN if CLOUD else f'{LAN}:{PORT}', 'X-Collection-Client':'local-ui'}
    if CLOUD: h['Origin']='https://collection.test'
    if cookie: h['Cookie'] = cookie
    if isinstance(data, dict): h['Content-Type']='application/json'; data=json.dumps(data).encode()
    h.update(headers or {})
    req=urllib.request.Request(BASE+path, data=data, headers=h, method=method)
    try:
        with urllib.request.urlopen(req, timeout=10) as r: code, raw, hs=r.status,r.read(),r.headers
    except urllib.error.HTTPError as e: code,raw,hs=e.code,e.read(),e.headers
    assert code == expected, (path,code,raw)
    return raw,hs

def state(cookie): return json.loads(call('/api/state',cookie=cookie)[0])
def multipart(revision, name, content):
    boundary='kollection-test-boundary'
    body=(f'--{boundary}\r\nContent-Disposition: form-data; name="revision"\r\n\r\n{revision}\r\n'
          f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\nContent-Type: image/png\r\n\r\n').encode()+content+f'\r\n--{boundary}--\r\n'.encode()
    return body, {'Content-Type':f'multipart/form-data; boundary={boundary}'}

with tempfile.TemporaryDirectory() as folder:
    lines=[]
    proc=subprocess.Popen([os.getenv('DOTNET_EXE','dotnet'),str(ROOT/'src/Collection.Web/bin/Release/net10.0/Collection.Web.dll')]+([] if UPLOAD_ONLY or CLOUD else ['--lan']),
        cwd=ROOT/'src/Collection.Web',env=dict(os.environ,COLLECTION_DATA=folder,COLLECTION_PORT=str(PORT),COLLECTION_LAN_IP=LAN,COLLECTION_CLOUD="1" if CLOUD else "0",COLLECTION_PUBLIC_URL="https://collection.test",COLLECTION_ACCESS_KEY=CLOUD_KEY),
        stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,encoding='utf-8')
    # Read lines incrementally rather than waiting for EOF.
    def read_lines():
        for line in proc.stdout: lines.append(line)
    reader=threading.Thread(target=read_lines,daemon=True);reader.start()
    try:
        for _ in range(150):
            try:
                call('/health' if CLOUD else '/api/state',local=True)
                if UPLOAD_ONLY or CLOUD or re.search(r'访问口令: ([0-9a-f]{24})',''.join(lines)): break
            except OSError: pass
            if proc.poll() is not None: raise RuntimeError(''.join(lines))
            time.sleep(.1)
        cookie=None
        if not UPLOAD_ONLY:
            key=CLOUD_KEY if CLOUD else re.search(r'访问口令: ([0-9a-f]{24})',''.join(lines)).group(1)
            assert '连接你的收藏库' in call('/')[0].decode()
            call('/api/state',expected=401);call('/api/export',expected=401)
            call('/api/login','POST',{'key':'wrong'},expected=401)
            call('/api/login','POST',{'key':key},expected=403,headers={'Origin':'https://evil.example'})
            _,hs=call('/api/login','POST',{'key':key})
            cookie=hs['Set-Cookie'].split(';')[0]
            assert 'httponly' in hs['Set-Cookie'].lower() and 'samesite=strict' in hs['Set-Cookie'].lower()
            assert json.loads(call('/api/client',cookie=cookie)[0])['remote']
        if CLOUD:
            assert 'secure' in hs['Set-Cookie'].lower()
            call('/api/state', local=True, expected=403)
            call('/api/login','POST',{'key':key},expected=403,headers={'Origin':'http://collection.test'})
        s=state(cookie)
        if not UPLOAD_ONLY:
            call('/api/import','POST',{'revision':s['revision'],'path':folder,'recursive':False},cookie,expected=403)
        body,headers=multipart(s['revision'],'phone.png',b'phone fixture')
        uploaded=json.loads(call('/api/upload','POST',body,cookie,headers=headers)[0]);s=uploaded['state'];draft=s['items'][0]
        assert draft['draft'] and draft['tags']==['file','image']
        assert Path(draft['source']).read_bytes()==b'phone fixture'
        call('/api/items/'+draft['id']+'/media',cookie=cookie,expected=404)
        # Failed optimistic write must clean the uploaded file and directory.
        call('/api/upload','POST',body,cookie,expected=409,headers=headers)
        assert len(list((Path(folder)/'uploads').iterdir()))==1
        draft['name']='Phone photo'
        s=json.loads(call('/api/items/'+draft['id']+'/confirm','POST',{'revision':s['revision'],'item':draft},cookie)[0])
        assert call('/api/items/'+draft['id']+'/media',cookie=cookie)[0]==b'phone fixture'
        if not UPLOAD_ONLY:
            call('/api/items/'+draft['id']+'/media',expected=401)
        if not UPLOAD_ONLY:
            call('/api/items/'+draft['id']+'/locate','POST',{},cookie,expected=403)
        if CLOUD:
            outside=Path(folder)/'outside.png'; outside.write_bytes(b'not an uploaded file')
            modified=s['items'][0]; modified['values']['path']['text']=str(outside)
            call('/api/items/'+draft['id'],'PUT',{'revision':s['revision'],'item':modified},cookie)
            call('/api/items/'+draft['id']+'/media',cookie=cookie,expected=404)
        if not UPLOAD_ONLY:
            call('/api/logout','POST',{},cookie)
        if not UPLOAD_ONLY:
            call('/api/state',cookie=cookie,expected=401)
        print('PASS: upload staging, failure cleanup and confirmation' if UPLOAD_ONLY else 'PASS: cloud login, secure cookies, no local bypass, upload boundary and logout' if CLOUD else 'PASS: LAN login, cookie flags, origin guard, staging upload, failure cleanup, confirmation, media protection, logout revocation')
    finally:
        proc.terminate();proc.wait(timeout=10);reader.join(timeout=2)
