"""Native wait-only diagnostic. No clipboard, focus, input or timer-resolution changes."""
import ctypes as c
from ctypes import wintypes as w
import json
from pathlib import Path
import statistics
import time

u = c.WinDLL('user32', use_last_error=True)
k = c.WinDLL('kernel32', use_last_error=True)
u.MsgWaitForMultipleObjectsEx.argtypes = [w.DWORD, c.POINTER(w.HANDLE), w.DWORD, w.DWORD, w.DWORD]
u.MsgWaitForMultipleObjectsEx.restype = w.DWORD
u.PeekMessageW.argtypes = [c.POINTER(w.MSG), w.HWND, w.UINT, w.UINT, w.UINT]
u.TranslateMessage.argtypes = [c.POINTER(w.MSG)]
u.DispatchMessageW.argtypes = [c.POINTER(w.MSG)]
u.DispatchMessageW.restype = c.c_ssize_t
k.CreateWaitableTimerExW.argtypes = [c.c_void_p, w.LPCWSTR, w.DWORD, w.DWORD]
k.CreateWaitableTimerExW.restype = w.HANDLE
k.SetWaitableTimer.argtypes = [w.HANDLE, c.POINTER(c.c_longlong), w.LONG, c.c_void_p, c.c_void_p, w.BOOL]
k.CloseHandle.argtypes = [w.HANDLE]

def drain():
    msg = w.MSG()
    while u.PeekMessageW(c.byref(msg), None, 0, 0, 1):
        if msg.message == 0x12:
            raise RuntimeError('Unexpected quit')
        u.TranslateMessage(c.byref(msg))
        u.DispatchMessageW(c.byref(msg))

def sample(mode):
    start = time.perf_counter_ns()
    until = start + 50_000_000
    timer = None
    waits = []
    try:
        if mode != 'timeout':
            timer = k.CreateWaitableTimerExW(None, None, 2 if mode == 'high_resolution' else 0, 0x100002)
            if not timer:
                raise c.WinError(c.get_last_error())
        while (remaining := until - time.perf_counter_ns()) > 0:
            ms = max(1, min(50, remaining // 1_000_000))
            if timer:
                due = c.c_longlong(-max(1, (remaining + 99) // 100))
                if not k.SetWaitableTimer(timer, c.byref(due), 0, None, None, False):
                    raise c.WinError(c.get_last_error())
                handles = (w.HANDLE * 1)(timer)
            before = time.perf_counter_ns()
            result = u.MsgWaitForMultipleObjectsEx(1 if timer else 0, handles if timer else None, 0xffffffff if timer else ms, 0x4ff, 4)
            after = time.perf_counter_ns()
            if result == 0xffffffff:
                raise c.WinError(c.get_last_error())
            drain()
            waits.append({'remaining_ms': remaining / 1e6, 'timeout_ms': None if timer else ms, 'elapsed_ms': (after-before)/1e6, 'result': result})
        return {'mode': mode, 'elapsed_ms': (time.perf_counter_ns()-start)/1e6, 'waits': waits}
    finally:
        if timer:
            k.CloseHandle(timer)

import argparse
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True, help='Fresh JSONL journal; never overwritten')
parser.add_argument('--samples', type=int, default=52)
args = parser.parse_args()
if not 3 <= args.samples <= 1000:
    parser.error('--samples must be 3..1000, including two warmups')
rows = []
with args.output.open('x', encoding='utf-8') as journal:
    for block, mode in enumerate(['timeout', 'high_resolution', 'ordinary_timer', 'high_resolution', 'timeout']):
        for i in range(args.samples):
            row = dict(block=block, mode=mode, warmup=i<2)
            try:
                row.update(sample(mode), success=True)
            except Exception as error:
                row.update(success=False, error=str(error))
                journal.write(json.dumps(row)+'\n')
                journal.flush()
                raise
            journal.write(json.dumps(row)+'\n')
            journal.flush()
            rows.append(row)
for block in range(5):
    selected = [r for r in rows if r['block']==block and not r['warmup']]
    values = sorted(r['elapsed_ms'] for r in selected)
    print(json.dumps({'block': block, 'mode': selected[0]['mode'], 'n': len(values), 'min': min(values), 'median': statistics.median(values), 'mean': statistics.mean(values), 'max': max(values)}))
