"""Independent checks of saved synthetic destination observations."""
import hashlib
import json
import math
import time
from pathlib import Path

EMPTY_SHAPE = dict(links=[], bold=[], lists=0, items=0, code=[], list_structure=[])


def shape_for(case_id, references, source=False):
    return references.get(case_id, {}).get('source' if source else 'expected', EMPTY_SHAPE)


def destination_matches(observed, case, references):
    return (
        observed.get('paste_received') is True
        and observed.get('inputType') == 'insertFromPaste'
        and observed.get('selection_collapsed') is True
        and observed.get('text', '').replace('\r\n', '\n') == case['expected'].replace('\r\n', '\n')
        and observed.get('shape') == shape_for(case['id'], references)
    )


def reservation_count(path):
    path = Path(path)
    for _ in range(20):
        raw = path.read_text(encoding='utf-8')
        if not raw or raw.endswith('\n'):
            rows = [json.loads(line) for line in raw.splitlines()]
            if any(row.get('reservation') != index + 1 or row.get('limit') != 100 for index, row in enumerate(rows)) or len(rows) > 100:
                raise ValueError('Invalid native reservation ledger')
            return len(rows)
        time.sleep(0.005)
    raise ValueError('Incomplete native reservation record')


def has_live_evidence(detail, reservations):
    tokens = detail.get('tokens') or {}
    native = detail.get('request_native') or {}
    attempts = detail.get('request_attempts')
    send = native.get('send_api_ms')
    return (
        detail.get('request_mode') == 'live' and detail.get('status_code') == 200
        and all(type(tokens.get(k)) is int and tokens[k] > 0 for k in ['input', 'output'])
        and type(attempts) is int and 1 <= attempts <= 2 and reservations >= attempts
        and type(send) in [int, float] and math.isfinite(send) and send >= 0
    )


def verify_pilot(directory, cases, references, negative=False):
    directory = Path(directory)
    manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8'))
    integrity = json.loads((directory / 'integrity.json').read_text(encoding='utf-8'))
    rows = [json.loads(line) for line in (directory / 'attempts.jsonl').read_text(encoding='utf-8').splitlines()]
    expected = list(cases.values())[:1] if negative else list(cases.values())
    if len(rows) != len(expected) or manifest.get('pilot') is not True:
        return False
    if not all(integrity.get(k) is True for k in ['production_unchanged', 'startup_unchanged', 'worker_stopped', 'clipboard_verified']):
        return False
    if hashlib.sha256((directory / 'candidate.exe').read_bytes()).hexdigest() != manifest.get('binary_sha256'):
        return False
    for row, case in zip(rows, expected):
        if row.get('case_id') != case['id'] or type(row.get('repetition')) is not int or row.get('repetition') != 0 or row.get('warmup') is not False or row.get('live_api') is not False:
            return False
        pids = [row.get(k) for k in ['observer_pid', 'coordinator_pid', 'collector_pid']]
        if not all(type(pid) is int and pid > 0 for pid in pids) or len(set(pids)) != 3 or row.get('editor_window_pid') != row['observer_pid']:
            return False
        detail = row.get('detail', {})
        if detail.get('status') != 'success' or detail.get('request_mode') != 'offline_replay':
            return False
        if detail.get('input_text','').replace('\r\n','\n') != case['text'].replace('\r\n','\n'):
            return False
        observed = row.get('observed', {})
        if negative:
            if observed.get('paste_received') is not True or observed.get('inputType') != 'insertFromPaste' or observed.get('selection_collapsed') is not True or observed.get('text') != case['expected'] + ' WRONG':
                return False
        elif not destination_matches(observed, case, references):
            return False
    return True
