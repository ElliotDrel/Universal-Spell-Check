# OpenAI Fine-Tuning API Reference

Quick reference for interpreting `finetune_job.json` and troubleshooting. All calls use the `openai` Python SDK.

## File Upload

```python
file = client.files.create(
    file=open("train.jsonl", "rb"),
    purpose="fine-tune"
)
# file.id → "file-abc123"
```

Files are validated asynchronously. If validation fails, the fine-tune job creation will return a `failed` status immediately.

## Create Fine-Tune Job

```python
job = client.fine_tuning.jobs.create(
    model="gpt-4.1-2025-04-14",   # base model — pinned snapshot, not alias
    training_file="file-abc123",
    validation_file="file-def456"
)
# job.id → "ftjob-abc123"
# job.status → "validating_files"
```

## Poll Job Status

```python
job = client.fine_tuning.jobs.retrieve("ftjob-abc123")
# job.status — see statuses below
# job.fine_tuned_model — set when status == "succeeded"
# job.error.message — set when status == "failed"
```

## Job Statuses

| Status | Meaning |
|---|---|
| `validating_files` | Uploaded files being checked |
| `queued` | Waiting for a training slot |
| `running` | Training in progress |
| `succeeded` | Done — `fine_tuned_model` is set |
| `failed` | Error — check `job.error.message` |
| `cancelled` | Manually cancelled |

`finetune_job.json` mirrors these statuses in its `status` field.

## `finetune_job.json` Schema

Written by `submit_finetune.py` — all fields may be absent on an incomplete run:

```json
{
  "training_file_id": "file-abc123",
  "validation_file_id": "file-def456",
  "job_id": "ftjob-abc123",
  "status": "succeeded",
  "fine_tuned_model": "ft:gpt-4.1-2025-04-14:personal::abc123",
  "error": "..."    // only present on failure
}
```

## Common Failure Patterns

| Error | Likely cause |
|---|---|
| `invalid_request_error` on job create | Bad JSONL format or too few examples (min ~10) |
| `status: failed` immediately after `validating_files` | File validation failed — check JSONL schema |
| `status: failed` mid-run | Usually a training instability; retry with fresh data |
| Network error during poll | `submit_finetune.py` retries with exponential backoff; Ctrl-C saves state safely |

## JSONL Training Format

Each line:
```json
{"messages": [
  {"role": "system", "content": "..."},
  {"role": "user", "content": "..."},
  {"role": "assistant", "content": "..."}
]}
```

Minimum ~10 examples; recommended 50–200 for meaningful improvement.
