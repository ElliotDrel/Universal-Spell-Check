"""Score campaign evidence only; never reads private application logs."""
import argparse
import collections
import json
import math
import statistics
from pathlib import Path


def score(schedule, attempts):
    def identity(row):
        return (row.get('case_id'), row.get('repetition'), row.get('warmup'))
    identities = {identity(row) for row in schedule}
    visited = set()
    expected = collections.Counter(row['case_id'] for row in schedule if not row['warmup'])
    seen = collections.Counter()
    accepted = collections.defaultdict(list)
    failures = []
    for index, row in enumerate(attempts):
        key = identity(row)
        if (type(row.get('repetition')) is not int or type(row.get('warmup')) is not bool
                or key not in identities or key in visited or index >= len(schedule) or key != identity(schedule[index])):
            failures.append({'index': index, 'reason': 'unknown or duplicate trial identity'})
            continue
        visited.add(key)
        if row.get('warmup') is True:
            if row.get('success') is not True:
                failures.append({'index': index, 'reason': 'failed warmup'})
            continue
        case = row.get('case_id')
        seen[case] += 1
        latency = row.get('verified_insertion_ms')
        valid = (
            row.get('live_api') is True
            and row.get('success') is True
            and row.get('text_verified') is True
            and row.get('format_verified') is True
            and type(row.get('observer_pid')) is int
            and type(row.get('coordinator_pid')) is int
            and row['observer_pid'] > 0
            and row['coordinator_pid'] > 0
            and row['observer_pid'] != row['coordinator_pid']
            and isinstance(latency, (float, int))
            and not isinstance(latency, bool)
            and math.isfinite(latency)
            and latency >= 0
            and case in expected
        )
        if valid:
            accepted[case].append(latency)
        else:
            failures.append({'index': index, 'case_id': case, 'reason': 'failed or incomplete evidence'})
    per_case = {
        case: {'expected': count, 'attempted': seen[case], 'verified': len(accepted[case]),
               'median_ms': statistics.median(accepted[case]) if accepted[case] else None}
        for case, count in expected.items()
    }
    complete = bool(expected) and not failures and len(identities) == len(schedule) and visited == identities and seen == expected and all(
        len(accepted[case]) == count for case, count in expected.items())
    mean = statistics.mean(row['median_ms'] for row in per_case.values()) if complete else None
    return {'complete': complete, 'goal_verified': complete and mean < 1000,
            'mean_of_case_medians_ms': mean, 'per_case': per_case,
            'attempted': len(attempts), 'failures': failures,
            'note': 'Evidence fields require independent fixture validation; this scorer cannot attest provenance.'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('schedule', type=Path)
    parser.add_argument('attempts', type=Path)
    args = parser.parse_args()
    result = score(json.loads(args.schedule.read_text(encoding='utf-8'))['trials'],
                   [json.loads(line) for line in args.attempts.read_text(encoding='utf-8').splitlines()])
    print(json.dumps(result, indent=2))
    raise SystemExit(0 if result['goal_verified'] else 1)
