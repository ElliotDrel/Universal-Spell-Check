# Latency fixture setup failures — October 23, 2026

- Preserve three failed offline launcher attempts on the laptop; each aborts before
  posting a hotkey. Count zero measured corrections and zero warmups.
- Read `attempts.json` for hashes, source revision, failure reasons and the observed
  activation result. Runtime events were exported through the approved log reader.
- Treat these as fixture diagnostics, not a product failure rate or timing baseline.
- Retain the successful foreground-activation request alongside the later combined
  guard failure; it does not establish that every prerequisite stayed valid.
- Use source `3a258d2` or later for the next probe. It adds exact setup snapshots
  and a summary block-error field; it preserves the readiness guard.
- Rebuild on the desktop. The original frozen binary predates those diagnostics;
  the updated laptop Release build was intentionally cancelled for migration.
- Exclude private logs, credentials, user text, HWNDs, process IDs and executables
  from this portable record. Original local artifacts remain ignored on the laptop.
