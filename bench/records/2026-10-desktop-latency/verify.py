import json, pathlib, statistics
root = pathlib.Path(__file__).resolve().parent
for mode in ("diagnostics-off", "diagnostics-on"):
    rows = [json.loads(x) for x in (root / (mode + ".jsonl")).read_text().splitlines()]
    measured = [x for x in rows if not x["warmup"]]
    assert len(rows) == 40 and len(measured) == 30
    assert all(x["success"] for x in rows)
    assert sum(x["quality_match"] for x in measured) == 27
    assert {x["case_id"] for x in measured if not x["quality_match"]} == {"rich-list"}
    assert all(x["coordinator_timings"]["paste"]["settle_ms"] >= 50 for x in rows)
    for x in rows:
        observed = x["observed_clipboard"]["editor_text_at_finish"].replace("\r\n", "\n")
        assert observed == x["output"].replace("\r\n", "\n")
    times = sorted(x["timings"]["total_ms"] for x in measured)
    p = .95 * (len(times) - 1)
    p95 = times[int(p)] + (times[int(p) + 1] - times[int(p)]) * (p - int(p))
    print(f"{mode}: 30/30 deliveries, 27/30 handwritten quality; median={statistics.median(times):.3f}ms p95={p95:.3f}ms; under500={sum(t < 500 for t in times)}/30")
assert len(json.loads((root / "preflights.json").read_text())) == 3
