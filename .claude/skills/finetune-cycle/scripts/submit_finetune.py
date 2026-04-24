"""
submit_finetune.py — Upload training data and submit/poll an OpenAI fine-tune job.

Usage:
    python submit_finetune.py --run-dir fine_tune_runs/2026-04-23-143000

The script is idempotent:
  - If finetune_job.json already shows status=succeeded, it exits immediately.
  - If finetune_job.json shows status=running with a job_id, it skips
    upload/create and resumes polling.
"""

import argparse
import json
import os
import sys
import tempfile
import time
from datetime import datetime
from pathlib import Path

from dotenv import load_dotenv
from openai import OpenAI, APIError, APIConnectionError, APITimeoutError


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def load_job(run_dir: Path) -> dict:
    """Read finetune_job.json from run_dir, returning {} if absent."""
    job_path = run_dir / "finetune_job.json"
    if job_path.exists():
        with open(job_path) as f:
            return json.load(f)
    return {}


def save_job(run_dir: Path, data: dict) -> None:
    """Atomically write data to finetune_job.json using a temp-then-rename."""
    job_path = run_dir / "finetune_job.json"
    fd, tmp_path = tempfile.mkstemp(dir=run_dir, prefix=".finetune_job_", suffix=".json")
    try:
        with os.fdopen(fd, "w") as f:
            json.dump(data, f, indent=2)
        os.replace(tmp_path, job_path)
    except Exception:
        # Clean up temp file on error
        try:
            os.unlink(tmp_path)
        except OSError:
            pass
        raise


def elapsed_str(start: float) -> str:
    """Return elapsed time as 'Xm Ys' string."""
    secs = int(time.monotonic() - start)
    m, s = divmod(secs, 60)
    return f"{m}m {s}s"


def append_summary_success(run_dir: Path, job_id: str, fine_tuned_model: str,
                           elapsed: str, final_loss) -> None:
    """Append the Step 2 success section to summary.md (idempotent)."""
    summary_path = run_dir / "summary.md"
    header = "## 2. Fine-Tune Job"

    # Read existing content if present
    existing = ""
    if summary_path.exists():
        existing = summary_path.read_text(encoding="utf-8")

    if header in existing:
        # Already written — skip
        return

    completed_time = datetime.now().strftime("%H:%M")
    loss_str = str(final_loss) if final_loss is not None else "N/A"

    section = (
        f"\n{header} (completed {completed_time})\n"
        f"Job ID: {job_id} · Model: {fine_tuned_model}\n"
        f"Training duration: {elapsed} · Final loss: {loss_str}\n"
    )

    with open(summary_path, "a", encoding="utf-8") as f:
        f.write(section)


def append_summary_failure(run_dir: Path, job_id: str | None, error_msg: str) -> None:
    """Append the Step 2 failure section to summary.md (idempotent)."""
    summary_path = run_dir / "summary.md"
    header = "## 2. Fine-Tune Job"

    existing = ""
    if summary_path.exists():
        existing = summary_path.read_text(encoding="utf-8")

    if header in existing:
        return

    completed_time = datetime.now().strftime("%H:%M")
    job_str = job_id or "N/A"

    section = (
        f"\n{header} (FAILED at {completed_time})\n"
        f"Job ID: {job_str}\n"
        f"Error: {error_msg}\n"
    )

    with open(summary_path, "a", encoding="utf-8") as f:
        f.write(section)


def extract_final_loss(job) -> object:
    """Extract final training loss from job result files metadata, if available."""
    try:
        if job.result_files:
            # The loss is in training metrics; not directly on the job object
            # in the standard API. Return None and let callers show N/A.
            pass
    except Exception:
        pass
    return None


# ---------------------------------------------------------------------------
# Core steps
# ---------------------------------------------------------------------------

def upload_files(client: OpenAI, run_dir: Path, job_data: dict) -> dict:
    """Upload train.jsonl and validation.jsonl; update job_data and persist."""
    print("Uploading training file...")
    train_path = run_dir / "train.jsonl"
    with open(train_path, "rb") as f:
        train_file = client.files.create(file=f, purpose="fine-tune")

    print("Uploading validation file...")
    val_path = run_dir / "validation.jsonl"
    with open(val_path, "rb") as f:
        val_file = client.files.create(file=f, purpose="fine-tune")

    job_data["training_file_id"] = train_file.id
    job_data["validation_file_id"] = val_file.id
    save_job(run_dir, job_data)
    print(f"Uploaded — training_file_id={train_file.id}, validation_file_id={val_file.id}")
    return job_data


def create_job(client: OpenAI, run_dir: Path, job_data: dict) -> dict:
    """Create the fine-tune job; update job_data and persist."""
    print("Creating fine-tune job...")
    job = client.fine_tuning.jobs.create(
        model="gpt-4.1-2025-04-14",
        training_file=job_data["training_file_id"],
        validation_file=job_data["validation_file_id"],
    )
    job_data["job_id"] = job.id
    job_data["status"] = "running"
    save_job(run_dir, job_data)
    print(f"Job created — job_id={job.id}")
    return job_data


def poll_job(client: OpenAI, run_dir: Path, job_data: dict) -> None:
    """Poll the fine-tune job until it reaches a terminal state."""
    job_id = job_data["job_id"]
    start = time.monotonic()
    max_retries = 5
    base_backoff = 5.0  # seconds, doubles each retry

    print(f"Polling job {job_id} every 60s…")

    while True:
        # Poll with exponential backoff on network errors
        retry = 0
        backoff = base_backoff
        job = None
        while retry <= max_retries:
            try:
                job = client.fine_tuning.jobs.retrieve(job_id)
                break
            except (APIConnectionError, APITimeoutError) as exc:
                retry += 1
                if retry > max_retries:
                    raise
                print(f"Network error ({exc}); retrying in {backoff:.0f}s (attempt {retry}/{max_retries})…")
                time.sleep(backoff)
                backoff = min(backoff * 2, 300)
            except APIError as exc:
                # Non-transient API errors — re-raise immediately
                raise

        # Persist current status
        job_data["status"] = job.status
        job_data["job_id"] = job.id
        save_job(run_dir, job_data)

        timestamp = datetime.now().strftime("%H:%M:%S")
        print(f"[{timestamp}] Status: {job.status} (elapsed: {elapsed_str(start)})")

        if job.status == "succeeded":
            fine_tuned_model = job.fine_tuned_model or "unknown"
            job_data["fine_tuned_model"] = fine_tuned_model
            save_job(run_dir, job_data)

            final_loss = extract_final_loss(job)
            elapsed = elapsed_str(start)
            append_summary_success(run_dir, job_id, fine_tuned_model, elapsed, final_loss)
            print(f"\nJob succeeded. Fine-tuned model: {fine_tuned_model}")
            sys.exit(0)

        if job.status == "failed":
            error_msg = "Unknown error"
            if job.error:
                error_msg = str(job.error)
            job_data["error"] = error_msg
            save_job(run_dir, job_data)

            append_summary_failure(run_dir, job_id, error_msg)
            print(f"\nJob failed. Error: {error_msg}")
            sys.exit(1)

        # Still running — wait before next poll
        try:
            time.sleep(60)
        except KeyboardInterrupt:
            print("\nInterrupted. State saved — rerun to resume polling.")
            sys.exit(0)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(description="Submit and poll an OpenAI fine-tune job.")
    parser.add_argument("--run-dir", required=True, help="Path to the fine-tune run directory")
    args = parser.parse_args()

    run_dir = Path(args.run_dir)
    if not run_dir.is_dir():
        print(f"Error: run-dir '{run_dir}' does not exist or is not a directory.", file=sys.stderr)
        sys.exit(1)

    # Load .env from CWD (users always run from project root)
    load_dotenv()

    api_key = os.environ.get("OPENAI_API_KEY")
    if not api_key:
        print("Error: OPENAI_API_KEY not set. Add it to your .env file.", file=sys.stderr)
        sys.exit(1)

    client = OpenAI(api_key=api_key)

    # Read existing state
    job_data = load_job(run_dir)

    # Idempotency: already succeeded
    if job_data.get("status") == "succeeded":
        print("Job already succeeded, nothing to do.")
        sys.exit(0)

    # Resume: already running with a job_id — skip upload/create
    if job_data.get("status") == "running" and job_data.get("job_id"):
        print(f"Resuming polling for existing job: {job_data['job_id']}")
        try:
            poll_job(client, run_dir, job_data)
        except KeyboardInterrupt:
            print("\nInterrupted. State saved — rerun to resume polling.")
            sys.exit(0)
        return

    # Fresh run: upload files
    try:
        job_data = upload_files(client, run_dir, job_data)
    except Exception as exc:
        error_msg = str(exc)
        job_data["error"] = error_msg
        save_job(run_dir, job_data)
        append_summary_failure(run_dir, job_data.get("job_id"), error_msg)
        print(f"Error during file upload: {error_msg}", file=sys.stderr)
        sys.exit(1)

    # Create fine-tune job
    try:
        job_data = create_job(client, run_dir, job_data)
    except Exception as exc:
        error_msg = str(exc)
        job_data["error"] = error_msg
        save_job(run_dir, job_data)
        append_summary_failure(run_dir, job_data.get("job_id"), error_msg)
        print(f"Error during job creation: {error_msg}", file=sys.stderr)
        sys.exit(1)

    # Poll until terminal state
    try:
        poll_job(client, run_dir, job_data)
    except KeyboardInterrupt:
        print("\nInterrupted. State saved — rerun to resume polling.")
        sys.exit(0)


if __name__ == "__main__":
    main()
