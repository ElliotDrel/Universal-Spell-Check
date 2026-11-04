"""Serial disposable editor campaign owner. Native worker alone can access API credentials."""
import argparse, ctypes, ctypes.wintypes as W, hashlib, importlib.util, json, os
import pathlib, subprocess, sys, threading, time, urllib.parse, winreg, re, msvcrt, shutil, tempfile, secrets
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from score import score
from evidence import shape_for, verify_pilot, reservation_count, has_live_evidence

ROOT=pathlib.Path(__file__).resolve().parents[2]
HERE=pathlib.Path(__file__).resolve().parent
def write(path,value):
    with tempfile.NamedTemporaryFile(mode='w',dir=path.parent,delete=False,encoding='utf-8',newline='\n') as f:
        json.dump(value,f,ensure_ascii=False,indent=2);f.write('\n');f.flush();os.fsync(f.fileno());temporary=f.name
    os.replace(temporary,path)
def norm(text):return text.replace('\r\n','\n')
def qpc():
    value=ctypes.c_longlong();ctypes.windll.kernel32.QueryPerformanceCounter(ctypes.byref(value));return value.value
def startup():
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Microsoft\Windows\CurrentVersion\Run') as key:
        rows={};i=0
        while True:
            try:n,v,t=winreg.EnumValue(key,i);i+=1;rows[n]=(v,t)
            except OSError:return rows

def focus_fixture(guard, expected_pid):
    matches=[]
    callback=ctypes.WINFUNCTYPE(W.BOOL,W.HWND,W.LPARAM)
    def visit(hwnd,param):
        text=ctypes.create_unicode_buffer(512)
        guard.u.GetWindowTextW(hwnd,text,512)
        pid=W.DWORD();guard.u.GetWindowThreadProcessId(hwnd,ctypes.byref(pid))
        if text.value in ['USC disposable editor','USC disposable editor - Google Chrome'] and pid.value==expected_pid:matches.append(hwnd)
        return True
    visit=callback(visit)
    guard.u.EnumWindows.argtypes=[callback,W.LPARAM];guard.u.EnumWindows.restype=W.BOOL
    guard.u.GetWindowTextW.argtypes=[W.HWND,W.LPWSTR,ctypes.c_int];guard.u.GetWindowTextW.restype=ctypes.c_int
    guard.u.EnumWindows(visit,0)
    if len(matches)!=1:return False
    guard.u.SetForegroundWindow(matches[0])
    return matches[0] if guard.u.GetForegroundWindow()==matches[0] else None

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
    primary=pathlib.Path(subprocess.check_output(['git','rev-parse','--path-format=absolute','--git-common-dir'],cwd=ROOT,text=True).strip()).parent
    historical=primary/'benchmark_runs/experiment-evaluation-2026-10-31/desktop.py'
    if not historical.is_file():raise RuntimeError('Prepared historical clipboard guard prerequisite is missing; see campaign README')
    spec=importlib.util.spec_from_file_location('historical_guard',historical);guard=importlib.util.module_from_spec(spec);spec.loader.exec_module(guard)
    out=args.output.resolve();out.mkdir(parents=True,exist_ok=False)
    owner_path=ROOT/'benchmark_runs/campaign-2026-11-04/desktop-owner.lock'
    owner_path.parent.mkdir(parents=True,exist_ok=True)
    owner=owner_path.open('a+b')
    if owner.tell()==0:owner.write(b'0');owner.flush()
    owner.seek(0);msvcrt.locking(owner.fileno(),msvcrt.LK_NBLCK,1)
    cases={c['id']:c for c in json.loads((HERE/'cases-v1.json').read_text(encoding='utf-8'))}
    schedule=json.loads((HERE/'schedule-v2.json').read_text(encoding='utf-8'))['trials']
    if args.pilot:schedule=[dict(case_id=c,repetition=0,warmup=False) for c in cases]
    if args.negative_control:schedule=schedule[:1]
    references=json.loads((HERE/'formats-v1.json').read_text(encoding='utf-8'))
    guard.u.GetWindowThreadProcessId.argtypes=[W.HWND,ctypes.POINTER(W.DWORD)];guard.u.GetWindowThreadProcessId.restype=W.DWORD
    session_token=secrets.token_hex(16)
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
        'runner':HERE/'run.py','cases':HERE/'cases-v1.json','schedule':HERE/'schedule-v2.json','evaluator':HERE/'score.py','editor':HERE/'editor.html','formats':HERE/'formats-v1.json','evidence':HERE/'evidence.py','replacements':ROOT/'replacements.json','channel_constants':ROOT/'src/BuildChannel.cs'}.items()}
    # Absence is a valid native configuration: Store::load_settings uses Settings::default.
    binding['settings_file_exists']=settings_path.exists()
    binding['settings']=hashlib.sha256(settings_path.read_bytes() if settings_path.exists() else b'native Settings::default; settings.json absent').hexdigest()
    binding['prompt']=hashlib.sha256(prompt.encode()).hexdigest()
    binding['effective_developer_logging']=False
    binding['encrypted_credential_store']=before.get('apikey.dat')
    if not args.pilot:
        if not args.validated_pilot:raise RuntimeError('A validated independent fixture pilot is required before live API use')
        if not args.validated_negative:raise RuntimeError('A validated wrong-output control is required before live API use')
        pilot=json.loads((args.validated_pilot/'pilot.json').read_text())
        if not verify_pilot(args.validated_pilot,cases,references):raise RuntimeError('Fixture pilot observations or restoration checks did not pass')
        manifest=json.loads((args.validated_pilot/'manifest.json').read_text())
        if manifest.get('binding')!=binding or manifest.get('binary_sha256')!=hashlib.sha256(exe.read_bytes()).hexdigest():raise RuntimeError('Pilot binary or frozen provenance differs')
        negative=json.loads((args.validated_negative/'negative.json').read_text())
        negative_manifest=json.loads((args.validated_negative/'manifest.json').read_text())
        if not verify_pilot(args.validated_negative,cases,references,negative=True) or negative_manifest.get('binding')!=binding or negative_manifest.get('binary_sha256')!=manifest['binary_sha256']:raise RuntimeError('Wrong-output control did not pass with this provenance')
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
    snapshot=out/'candidate.exe';shutil.copy2(exe,snapshot);exe=snapshot.resolve()
    source_snapshot=out/'source';source_snapshot.mkdir()
    for source in (ROOT/'rust/src').glob('*.rs'):shutil.copy2(source,source_snapshot/source.name)
    for name in ['cases-v1.json','schedule-v2.json','score.py','editor.html','formats-v1.json','evidence.py']:shutil.copy2(HERE/name,out/name)
    write(out/'manifest.json',{'source_sha256':hashlib.sha256(b''.join(p.name.encode()+b'\0'+p.read_bytes() for p in sorted((ROOT/'rust/src').glob('*.rs')))).hexdigest(),'binding':binding,'binary_sha256':hashlib.sha256(exe.read_bytes()).hexdigest(),'cases_sha256':hashlib.sha256((HERE/'cases-v1.json').read_bytes()).hexdigest(),'schedule':schedule,'pilot':args.pilot,'observer_pid':os.getpid(),'source':subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()})
    class Handler(BaseHTTPRequestHandler):
        def log_message(self,*args):pass
        def do_GET(self):
            if self.path=='/':body=(out/'editor.html').read_text(encoding='utf-8').replace('__USC_SESSION__',session_token).encode();mime='text/html'
            elif self.path.startswith('/next?'):
                if urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query).get('session')!=[session_token]:self.send_error(403);return
                with lock:
                    if state['active'] is None and state['index']<len(schedule):
                        trial=schedule[state['index']];state['active']=dict(cases[trial['case_id']],**{k:v for k,v in trial.items() if k!='case_id'});state['ready']=False;state['started']=time.monotonic();body=json.dumps(state['active']).encode()
                    else:body=b'{"wait":true}'
                mime='application/json'
            else:self.send_error(404);return
            self.send_response(200);self.send_header('Content-Type',mime);self.end_headers();self.wfile.write(body)
        def do_POST(self):
            data=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            if data.pop('session',None)!=session_token:self.send_error(403);return
            with lock:
                active=state['active']
                if not active or data.get('case_id')!=active['id'] or data.get('repetition')!=active['repetition']:self.send_error(409);return
                if self.path=='/ready' and data.get('shape')!=shape_for(active['id'],references,source=True):state['fixture_error']='Source formatting differs from frozen fixture';self.send_error(409);return
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
            # Trusted local executable/argv only; browser/API data never enters this no-shell launch.
            worker=subprocess.Popen(command,shell=False,cwd=ROOT,stdout=(out/'worker.stdout').open('wb'),stderr=(out/'worker.stderr').open('wb'))  # nosemgrep: python.lang.security.audit.dangerous-subprocess-use-audit, python.lang.security.audit.dangerous-subprocess-use-tainted-env-args
            deadline=time.monotonic()+20
            while not(out/'worker/ready.json').exists():
                if worker.poll() is not None or time.monotonic()>deadline:raise RuntimeError('Native campaign worker readiness failed')
                time.sleep(.05)
            # Fixed installed executable and operator-owned profile path; no external command input.
            browser=subprocess.Popen([r'C:\Program Files\Google\Chrome\Application\chrome.exe','--new-window','--no-first-run','--no-default-browser-check','--disable-sync','--disable-extensions','--user-data-dir='+str((out/'chrome-profile').resolve()),'http://127.0.0.1:8767'],shell=False,stdout=(out/'chrome.stdout').open('wb'),stderr=(out/'chrome.stderr').open('wb'))  # nosemgrep: python.lang.security.audit.dangerous-subprocess-use-audit, python.lang.security.audit.dangerous-subprocess-use-tainted-env-args
            print('FIXTURE READY http://127.0.0.1:8767',flush=True)
            deadline=time.monotonic()+180
            while state['index']<len(schedule):
                if state.get('fixture_error'):raise RuntimeError(state['fixture_error'])
                if worker.poll() is not None:raise RuntimeError('Native worker exited')
                if (out/'cancel').exists():raise RuntimeError('Pilot cancelled')
                if time.monotonic()>deadline:raise RuntimeError('Fixture browser did not connect or trial timed out')
                with lock:active=state['active'];ready=state['ready']
                if not active or not ready:time.sleep(.02);continue
                editor_hwnd=focus_fixture(guard,browser.pid)
                if not editor_hwnd:time.sleep(.05);continue
                i=state['index'];trial=schedule[i];state['ready']=False
                before_requests=reservation_count(out/'worker/http-reservations.jsonl')
                write(out/'worker/command.json',{'id':i+1,'expected_input':active['text'],'expected_hwnd':editor_hwnd,'expected_pid':browser.pid})
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
                formatok=data.get('shape')==shape_for(active['id'],references)
                used_requests=reservation_count(out/'worker/http-reservations.jsonl')-before_requests
                live_evidence=not args.pilot and has_live_evidence(detail,used_requests)
                verified_ticks=qpc()
                row=dict(trial,live_api=live_evidence,request_reservations=used_requests,success=detail.get('status')=='success' and bool(data.get('paste_received')) and data.get('selection_collapsed') is True and data.get('inputType')=='insertFromPaste' and (args.pilot or live_evidence),text_verified=textok,format_verified=formatok,observer_pid=browser.pid,collector_pid=os.getpid(),editor_window_pid=browser.pid,coordinator_pid=worker.pid,verified_insertion_ms=(verified_ticks-trigger['ticks'])*1000/trigger['frequency'] if observation and textok and formatok else None,snapshot_receipt_ms=(observation[0]-trigger['ticks'])*1000/trigger['frequency'] if observation else None,observed=data,detail=detail)
                rows.append(row)
                with (out/'attempts.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps(row,ensure_ascii=False)+'\n');f.flush();os.fsync(f.fileno())
                print(i+1,trial['case_id'],row['verified_insertion_ms'],textok,formatok,flush=True)
                with lock:state['index']+=1;state['active']=None;state['observed']=None
                deadline=time.monotonic()+90
                if not row['success'] or not row['text_verified'] or not row['format_verified']:break
            write(out/'summary.json',score(schedule,rows))
            write(out/'pilot.json',{'passed':not args.negative_control and len(rows)==len(schedule) and all(r['success'] and r['text_verified'] and r['format_verified'] for r in rows),'live_api':not args.pilot})
            if args.negative_control:write(out/'negative.json',{'passed':len(rows)==1 and rows[0]['success'] and not rows[0]['text_verified'],'live_api':False})
    except BaseException as error:
        write(out/'failure.json',{'error_type':type(error).__name__,'error':str(error),'attempted':len(rows),'expected':len(schedule),'live_api':not args.pilot})
        write(out/'summary.json',score(schedule,rows))
        raise
    finally:
        if worker and worker.poll() is None:
            (out/'worker/stop').touch()
            try:worker.wait(timeout=10)
            except subprocess.TimeoutExpired:worker.terminate();worker.wait(timeout=5)
        if browser and browser.poll() is None:
            browser.terminate();browser.wait(timeout=10)
        server.shutdown()
        restoration_error=None
        if guard_entered:
            try:clipboard_guard.__exit__(None,None,None)
            except Exception as error:restoration_error=str(error)
        after=guard.integrity();regafter=startup();write(out/'integrity.json',{'production_unchanged':before==after,'startup_unchanged':reg==regafter,'worker_stopped':worker is None or worker.poll() is not None,'clipboard_verified':guard_entered and getattr(clipboard_guard,'verified',False),'restoration_error':restoration_error})
        if before!=after or reg!=regafter or restoration_error or not (guard_entered and getattr(clipboard_guard,'verified',False)):
            write(out/'pilot.json',{'passed':False,'reason':'Protected state or clipboard restoration failed','live_api':not args.pilot})
            raise RuntimeError('Protected production state changed or clipboard restoration failed')

    accepted=verify_pilot(out,cases,references,negative=args.negative_control) if args.pilot else score(schedule,rows)['goal_verified']
    raise SystemExit(0 if accepted else 1)

if __name__=='__main__':main()
