"""Serial disposable editor campaign owner. Native worker alone can access API credentials."""
import argparse, ctypes, ctypes.wintypes as W, hashlib, importlib.util, json, os
import pathlib, subprocess, sys, threading, time, urllib.parse, winreg, re, msvcrt
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from score import score

ROOT=pathlib.Path(__file__).resolve().parents[2]
HERE=pathlib.Path(__file__).resolve().parent
def write(path,value):
    path.write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def norm(text):return text.replace('\r\n','\n')
def qpc():
    value=ctypes.c_longlong();ctypes.windll.kernel32.QueryPerformanceCounter(ctypes.byref(value));return value.value
def startup():
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Microsoft\Windows\CurrentVersion\Run') as key:
        rows={};i=0
        while True:
            try:n,v,t=winreg.EnumValue(key,i);i+=1;rows[n]=(v,t)
            except OSError:return rows

def focus_fixture(guard):
    matches=[]
    callback=ctypes.WINFUNCTYPE(W.BOOL,W.HWND,W.LPARAM)
    def visit(hwnd,param):
        text=ctypes.create_unicode_buffer(512)
        ctypes.windll.user32.GetWindowTextW(hwnd,text,512)
        if 'USC disposable editor' in text.value:matches.append(hwnd)
        return True
    visit=callback(visit)
    guard.u.EnumWindows(visit,0)
    if len(matches)!=1:return False
    guard.u.SetForegroundWindow(matches[0])
    return guard.u.GetForegroundWindow()==matches[0]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',required=True,type=pathlib.Path)
    parser.add_argument('--pilot',action='store_true')
    parser.add_argument('--desktop-app-approved',action='store_true',help='Assert that the required Chrome tool app gate has actually been granted')
    parser.add_argument('--validated-pilot',type=pathlib.Path)
    parser.add_argument('--validated-negative',type=pathlib.Path)
    parser.add_argument('--negative-control',action='store_true')
    parser.add_argument('--phase',choices=['baseline','iteration','confirmation'],default='baseline')
    parser.add_argument('--iteration',type=int)
    args=parser.parse_args()
    if not args.desktop_app_approved:parser.error('Required Chrome tool app approval is pending; idle-desktop authorization alone does not satisfy it')
    if args.negative_control and not args.pilot:parser.error('Negative controls are offline pilot diagnostics only')
    out=args.output;out.mkdir(parents=True,exist_ok=False)
    owner_path=ROOT/'benchmark_runs/campaign-2026-11-04/desktop-owner.lock'
    owner_path.parent.mkdir(parents=True,exist_ok=True)
    owner=owner_path.open('a+b')
    if owner.tell()==0:owner.write(b'0');owner.flush()
    owner.seek(0);msvcrt.locking(owner.fileno(),msvcrt.LK_NBLCK,1)
    primary=pathlib.Path(subprocess.check_output(['git','rev-parse','--path-format=absolute','--git-common-dir'],cwd=ROOT,text=True).strip()).parent
    historical=primary/'benchmark_runs/experiment-evaluation-2026-10-31/desktop.py'
    spec=importlib.util.spec_from_file_location('historical_guard',historical);guard=importlib.util.module_from_spec(spec);spec.loader.exec_module(guard)
    cases={c['id']:c for c in json.loads((HERE/'cases-v1.json').read_text(encoding='utf-8'))}
    schedule=json.loads((HERE/'schedule-v2.json').read_text(encoding='utf-8'))['trials']
    if args.pilot:schedule=[dict(case_id=c,repetition=0,warmup=False) for c in cases]
    if args.negative_control:schedule=schedule[:1]
    browser=None;clipboard_guard=None;guard_entered=False
    rows=[];state={'index':0,'active':None,'ready':False,'observed':None,'stop':False};lock=threading.Lock();worker=None
    before=guard.integrity();reg=startup()
    ledger=ROOT/'benchmark_runs/campaign-2026-11-04/http-campaign.ledger'
    exe=ROOT/'rust/target/release/universal-spell-check.exe'
    folder=re.search(r'public const string SharedDataFolder = "([^"]+)";', (ROOT/'src/BuildChannel.cs').read_text()).group(1)
    settings_directory=pathlib.Path(os.environ['LOCALAPPDATA'])/folder
    settings_path=settings_directory/'settings.json'
    prompt=re.search(r'pub const PROMPT: &str = "(.*?)";', (ROOT/'rust/src/core.rs').read_text()).group(1)
    binding={name:hashlib.sha256(path.read_bytes()).hexdigest() for name,path in {
        'cases':HERE/'cases-v1.json','schedule':HERE/'schedule-v2.json','evaluator':HERE/'score.py','editor':HERE/'editor.html','settings':settings_path}.items()}
    binding['prompt']=hashlib.sha256(prompt.encode()).hexdigest()
    if not args.pilot:
        if not args.validated_pilot:raise RuntimeError('A validated independent fixture pilot is required before live API use')
        if not args.validated_negative:raise RuntimeError('A validated wrong-output control is required before live API use')
        pilot=json.loads((args.validated_pilot/'pilot.json').read_text())
        if pilot.get('passed') is not True or pilot.get('live_api') is not False:raise RuntimeError('Fixture pilot did not pass')
        manifest=json.loads((args.validated_pilot/'manifest.json').read_text())
        if manifest.get('binding')!=binding or manifest.get('binary_sha256')!=hashlib.sha256(exe.read_bytes()).hexdigest():raise RuntimeError('Pilot binary or frozen provenance differs')
        negative=json.loads((args.validated_negative/'negative.json').read_text())
        negative_manifest=json.loads((args.validated_negative/'manifest.json').read_text())
        if negative.get('passed') is not True or negative_manifest.get('binding')!=binding or negative_manifest.get('binary_sha256')!=manifest['binary_sha256']:raise RuntimeError('Wrong-output control did not pass with this provenance')
        campaign=ledger.parent/'binding.json'
        if campaign.exists():
            if json.loads(campaign.read_text())!=binding:raise RuntimeError('Frozen campaign provenance changed')
        else:
            with campaign.open('x') as f:json.dump(binding,f);f.flush();os.fsync(f.fileno())
        allocations=ledger.parent/'allocations';allocations.mkdir(exist_ok=True)
        if len(list(allocations.glob('*.json')))>=21:raise RuntimeError('All campaign benchmark allocations consumed')
        if args.phase=='iteration' and (args.iteration is None or not 1<=args.iteration<=19):raise RuntimeError('Candidate iteration must be 1..19; reserve final allocation for independent confirmation')
        name=args.phase+(f'-{args.iteration}' if args.phase=='iteration' else '')
        with (allocations/(name+'.json')).open('x') as f:json.dump({'output':str(out.resolve()),'binding':binding},f);f.flush();os.fsync(f.fileno())
    write(out/'manifest.json',{'source_sha256':hashlib.sha256(b''.join(p.name.encode()+b'\0'+p.read_bytes() for p in sorted((ROOT/'rust/src').glob('*.rs')))).hexdigest(),'binding':binding,'binary_sha256':hashlib.sha256(exe.read_bytes()).hexdigest(),'cases_sha256':hashlib.sha256((HERE/'cases-v1.json').read_bytes()).hexdigest(),'schedule':schedule,'pilot':args.pilot,'observer_pid':os.getpid(),'source':subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()})
    class Handler(BaseHTTPRequestHandler):
        def log_message(self,*args):pass
        def do_GET(self):
            if self.path=='/':body=(HERE/'editor.html').read_bytes();mime='text/html'
            elif self.path=='/next':
                with lock:
                    if state['active'] is None and state['index']<len(schedule):
                        trial=schedule[state['index']];state['active']=dict(cases[trial['case_id']],**{k:v for k,v in trial.items() if k!='case_id'});state['ready']=False;state['started']=time.monotonic();body=json.dumps(state['active']).encode()
                    else:body=b'{"wait":true}'
                mime='application/json'
            else:self.send_error(404);return
            self.send_response(200);self.send_header('Content-Type',mime);self.end_headers();self.wfile.write(body)
        def do_POST(self):
            data=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            with lock:
                if self.path=='/ready':state['ready']=True;state['source_shape']=data['shape'];write(out/f"source-{state['index']}.json",data)
                elif self.path=='/observed':state['observed']=(qpc(),data)
            self.send_response(200);self.end_headers();self.wfile.write(b'{}')
    server=ThreadingHTTPServer(('127.0.0.1',8767),Handler)
    thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
    try:
        clipboard_guard=guard.Guard();clipboard_guard.__enter__();guard_entered=True
        if True:
            command=[str(exe),'--campaign-worker','--allow-desktop','--output',str(out/'worker'),'--campaign-ledger',str(ledger),'--settings',str(settings_directory),'--replacements',str(ROOT/'replacements.json')]
            if args.pilot:
                replay_cases=HERE/'cases-v1.json'
                if args.negative_control:
                    replay_cases=out/'negative-cases.json';bad=[dict(c) for c in cases.values()];bad[0]['expected']+=' WRONG';write(replay_cases,bad)
                command+=['--replay-cases',str(replay_cases)]
            worker=subprocess.Popen(command,cwd=ROOT,stdout=(out/'worker.stdout').open('wb'),stderr=(out/'worker.stderr').open('wb'))
            deadline=time.monotonic()+20
            while not(out/'worker/ready.json').exists():
                if worker.poll() is not None or time.monotonic()>deadline:raise RuntimeError('Native campaign worker readiness failed')
                time.sleep(.05)
            browser=subprocess.Popen([r'C:\Program Files\Google\Chrome\Application\chrome.exe','--new-window','--no-first-run','--no-default-browser-check','--disable-sync','--disable-extensions','--user-data-dir='+str((out/'chrome-profile').resolve()),'http://127.0.0.1:8767'],stdout=(out/'chrome.stdout').open('wb'),stderr=(out/'chrome.stderr').open('wb'))
            print('FIXTURE READY http://127.0.0.1:8767',flush=True)
            deadline=time.monotonic()+180
            while state['index']<len(schedule):
                if worker.poll() is not None:raise RuntimeError('Native worker exited')
                if (out/'cancel').exists():raise RuntimeError('Pilot cancelled')
                if time.monotonic()>deadline:raise RuntimeError('Fixture browser did not connect or trial timed out')
                with lock:active=state['active'];ready=state['ready']
                if not active or not ready:time.sleep(.02);continue
                if not focus_fixture(guard):time.sleep(.05);continue
                i=state['index'];trial=schedule[i];state['ready']=False
                write(out/'worker/command.json',{'id':i+1,'expected_input':active['text']})
                started=time.monotonic();detail_path=out/f'worker/detail-{i+1}.json'
                while time.monotonic()-started<65:
                    if detail_path.exists():
                        interim=json.loads(detail_path.read_text())
                        if state['observed'] or interim.get('status')!='success':break
                    if worker.poll() is not None:raise RuntimeError('Native worker exited')
                    time.sleep(.005)
                detail=json.loads(detail_path.read_text()) if detail_path.exists() else {}
                observation=state['observed'];data=observation[1] if observation else {}
                triggerpath=out/f'worker/trigger-{i+1}.json'
                trigger=json.loads(triggerpath.read_text()) if triggerpath.exists() else {'ticks':0,'frequency':1}
                textok=norm(data.get('text',''))==norm(active['expected'])
                shape=data.get('shape',{});source=state['source_shape'];formatok=(shape.get('lists')==source['lists'] and shape.get('items')==source['items'] and [x[1] for x in shape.get('links',[])]==[x[1] for x in source['links']] and len(shape.get('bold',[]))==len(source['bold']) and shape.get('code')==source['code'])
                row=dict(trial,live_api=not args.pilot,success=detail.get('status')=='success' and bool(data.get('paste_received')),text_verified=textok,format_verified=formatok,observer_pid=os.getpid(),coordinator_pid=worker.pid,verified_insertion_ms=(observation[0]-trigger['ticks'])*1000/trigger['frequency'] if observation else None,observed=data,detail=detail)
                rows.append(row)
                with (out/'attempts.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps(row,ensure_ascii=False)+'\n');f.flush();os.fsync(f.fileno())
                print(i+1,trial['case_id'],row['verified_insertion_ms'],textok,formatok,flush=True)
                with lock:state['index']+=1;state['active']=None;state['observed']=None
                deadline=time.monotonic()+90
                if not row['success']:break
            write(out/'summary.json',score(schedule,rows))
            write(out/'pilot.json',{'passed':not args.negative_control and len(rows)==len(schedule) and all(r['success'] and r['text_verified'] and r['format_verified'] for r in rows),'live_api':not args.pilot})
            if args.negative_control:write(out/'negative.json',{'passed':len(rows)==1 and rows[0]['success'] and not rows[0]['text_verified'],'live_api':False})
    finally:
        if worker and worker.poll() is None:
            (out/'worker/stop').touch()
            try:worker.wait(timeout=10)
            except subprocess.TimeoutExpired:worker.terminate();worker.wait(timeout=5)
        if browser and browser.poll() is None:
            browser.terminate();browser.wait(timeout=10)
        server.shutdown()
        if guard_entered:clipboard_guard.__exit__(None,None,None)
        after=guard.integrity();regafter=startup();write(out/'integrity.json',{'production_unchanged':before==after,'startup_unchanged':reg==regafter,'worker_stopped':worker is None or worker.poll() is not None,'clipboard_verified':guard_entered and clipboard_guard.verified})
        if before!=after or reg!=regafter:raise RuntimeError('Protected production state changed')

if __name__=='__main__':main()
